import 'dart:async';
import 'package:flutter/material.dart';
import '../../auth.dart';
import '../../core/network/upload_control.dart';
import '../../core/localization/app_strings.dart';
import '../../core/media/picture_in_picture.dart';
import '../content/content_models.dart';
import '../playlists/playlist_service.dart';

enum UploadPhase {
  idle,
  uploading,
  processing,
  completed,
  failed,
  cancelled,
  partial,
}

typedef VideoUploadTask =
    Future<VideoDetail> Function(
      UploadControl control,
      String key,
      void Function(int, int) progress,
    );

class UploadController extends ChangeNotifier {
  UploadController(this.auth) {
    auth.addListener(_identityChanged);
  }
  final AuthController auth;
  UploadPhase phase = UploadPhase.idle;
  String fileName = '';
  String? error;
  int sent = 0, total = 0;
  VideoDetail? video;
  VideoUploadTask? _task;
  Future<void> Function()? _onSaved;
  final List<String> _pendingPlaylists = [];
  String _key = '';
  int _session = -1;
  int _run = 0;
  bool _disposed = false;
  UploadControl? _control;
  bool get busy =>
      phase == UploadPhase.uploading || phase == UploadPhase.processing;
  bool get visible => phase != UploadPhase.idle;
  double? get progress => total > 0 ? (sent / total).clamp(0, 1) : null;
  void _notify() {
    if (!_disposed) notifyListeners();
  }

  bool start({
    required String name,
    required VideoUploadTask task,
    List<String> playlistIds = const [],
    Future<void> Function()? onSaved,
  }) {
    if (busy || !auth.authenticated) return false;
    fileName = name;
    video = null;
    sent = 0;
    total = 0;
    error = null;
    _key = newUploadId();
    _session = auth.sessionGeneration;
    _task = task;
    _onSaved = onSaved;
    _pendingPlaylists
      ..clear()
      ..addAll(playlistIds.toSet());
    phase = UploadPhase.uploading;
    unawaited(_execute());
    return true;
  }

  bool _current(int run) =>
      !_disposed &&
      run == _run &&
      _session == auth.sessionGeneration &&
      auth.authenticated;
  Future<void> _execute() async {
    final run = ++_run;
    _control = UploadControl();
    error = null;
    phase = video == null ? UploadPhase.uploading : UploadPhase.processing;
    _notify();
    var previousPercent = -1;
    try {
      if (video == null) {
        final result = await _task!(_control!, _key, (loaded, length) {
          if (!_current(run)) return;
          sent = loaded;
          total = length;
          phase = loaded >= length && length > 0
              ? UploadPhase.processing
              : UploadPhase.uploading;
          final percent = length > 0 ? (loaded * 100 ~/ length) : 0;
          if (percent != previousPercent) {
            previousPercent = percent;
            _notify();
          }
        });
        if (!_current(run)) return;
        video = result;
      }
      final pending = List<String>.of(_pendingPlaylists);
      for (final id in pending) {
        if (!_current(run)) return;
        try {
          await PlaylistService(auth).addVideo(id, video!.id);
          if (!_current(run)) return;
          _pendingPlaylists.remove(id);
        } on ApiFailure catch (e) {
          if (e.code == 'PLAYLIST_VIDEO_EXISTS') _pendingPlaylists.remove(id);
        } catch (_) {
          /* Keep failed attachments for a separate retry. */
        }
      }
      if (!_current(run)) return;
      phase = _pendingPlaylists.isEmpty
          ? UploadPhase.completed
          : UploadPhase.partial;
      if (_onSaved != null) {
        try {
          await _onSaved!();
        } catch (_) {
          /* Video is already saved. */
        }
      }
    } on UploadCancelled {
      if (_current(run)) phase = UploadPhase.cancelled;
    } on ApiFailure catch (e) {
      if (_current(run)) {
        phase = UploadPhase.failed;
        error = AppStrings.apiError(e);
      }
    } catch (_) {
      if (_current(run)) {
        phase = UploadPhase.failed;
        error = AppStrings.t('parity.uploadFailed');
      }
    } finally {
      if (_current(run)) _notify();
    }
  }

  void cancel() {
    if (!busy) return;
    ++_run;
    _control?.cancel();
    phase = video == null ? UploadPhase.cancelled : UploadPhase.partial;
    _notify();
  }

  void retry() {
    if (busy || _task == null || _session != auth.sessionGeneration) return;
    unawaited(_execute());
  }

  void dismiss() {
    if (busy) return;
    ++_run;
    _task = null;
    _onSaved = null;
    video = null;
    error = null;
    _pendingPlaylists.clear();
    phase = UploadPhase.idle;
    _notify();
  }

  void _identityChanged() {
    if (_session == -1 || _session == auth.sessionGeneration) return;
    ++_run;
    _control?.cancel();
    phase = UploadPhase.idle;
    _task = null;
    _onSaved = null;
    video = null;
    _pendingPlaylists.clear();
    _notify();
  }

  @override
  void dispose() {
    _disposed = true;
    ++_run;
    _control?.cancel();
    auth.removeListener(_identityChanged);
    super.dispose();
  }
}

class UploadScope extends InheritedNotifier<UploadController> {
  const UploadScope({
    super.key,
    required UploadController controller,
    required super.child,
  }) : super(notifier: controller);
  static UploadController of(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<UploadScope>()!.notifier!;
}

class UploadTray extends StatelessWidget {
  const UploadTray({super.key, required this.controller});
  final UploadController controller;

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: Listenable.merge([controller, AppPictureInPicture.active]),
    builder: (context, _) {
      final c = controller;
      if (!c.visible || AppPictureInPicture.active.value) {
        return const SizedBox.shrink();
      }
      final colors = Theme.of(context).colorScheme;
      final accent = _uploadAccent(colors, c);
      final title = _uploadTitle(c);
      return Semantics(
        button: true,
        label: '$title, ${c.fileName}',
        value: c.phase == UploadPhase.uploading && c.progress != null
            ? '${((c.progress!) * 100).round()}%'
            : null,
        child: FloatingActionButton.small(
          key: const ValueKey('upload-progress-button'),
          heroTag: 'upload-progress-button',
          tooltip: title,
          backgroundColor: colors.surfaceContainerLow,
          foregroundColor: accent,
          elevation: 2,
          onPressed: () => showDialog<void>(
            context: context,
            builder: (_) => _UploadProgressDialog(controller: c),
          ),
          child: SizedBox.square(
            dimension: 30,
            child: Stack(
              alignment: Alignment.center,
              children: [
                if (c.busy)
                  Positioned.fill(
                    child: CircularProgressIndicator(
                      value: c.phase == UploadPhase.uploading
                          ? c.progress
                          : null,
                      strokeWidth: 2.5,
                      color: accent,
                      backgroundColor: accent.withValues(alpha: .16),
                    ),
                  )
                else
                  Positioned.fill(
                    child: DecoratedBox(
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        border: Border.all(color: accent, width: 2),
                      ),
                    ),
                  ),
                Icon(_uploadIcon(c), size: 16, color: accent),
              ],
            ),
          ),
        ),
      );
    },
  );
}

class _UploadProgressDialog extends StatelessWidget {
  const _UploadProgressDialog({required this.controller});
  final UploadController controller;

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: controller,
    builder: (context, _) {
      final c = controller;
      final colors = Theme.of(context).colorScheme;
      final accent = _uploadAccent(colors, c);
      final title = _uploadTitle(c);
      final done = c.phase == UploadPhase.completed;

      void close({bool dismissUpload = false}) {
        if (dismissUpload) c.dismiss();
        Navigator.of(context).pop();
      }

      return Dialog(
        key: const ValueKey('upload-progress-dialog'),
        insetPadding: const EdgeInsets.symmetric(horizontal: 12, vertical: 24),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(22)),
        child: ConstrainedBox(
          constraints: BoxConstraints(
            maxWidth: 360,
            maxHeight: MediaQuery.sizeOf(context).height - 48,
          ),
          child: Padding(
            padding: const EdgeInsets.fromLTRB(20, 18, 20, 16),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Container(
                      width: 42,
                      height: 42,
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        color: accent.withValues(alpha: .1),
                      ),
                      alignment: Alignment.center,
                      child: Icon(_uploadIcon(c), color: accent, size: 21),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Semantics(
                            liveRegion: true,
                            child: Text(
                              title,
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                              style: Theme.of(context).textTheme.titleSmall
                                  ?.copyWith(
                                    color: accent,
                                    fontWeight: FontWeight.w800,
                                  ),
                            ),
                          ),
                          const SizedBox(height: 3),
                          Text(
                            c.fileName,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: Theme.of(context).textTheme.bodySmall
                                ?.copyWith(color: colors.onSurfaceVariant),
                          ),
                        ],
                      ),
                    ),
                    IconButton(
                      tooltip: AppStrings.t('parity.close'),
                      visualDensity: VisualDensity.compact,
                      onPressed: () => close(),
                      icon: const Icon(Icons.close_rounded),
                    ),
                  ],
                ),
                Flexible(
                  child: SingleChildScrollView(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        if (c.phase == UploadPhase.uploading) ...[
                          const SizedBox(height: 16),
                          ClipRRect(
                            borderRadius: BorderRadius.circular(6),
                            child: LinearProgressIndicator(
                              value: c.progress,
                              minHeight: 5,
                              color: accent,
                              backgroundColor: accent.withValues(alpha: .12),
                            ),
                          ),
                          const SizedBox(height: 7),
                          Text(
                            c.total > 0
                                ? '${((c.progress ?? 0) * 100).round()}% · ${_formatUploadBytes(c.sent)} / ${_formatUploadBytes(c.total)}'
                                : AppStrings.t('uploadTray.connecting'),
                            style: Theme.of(context).textTheme.bodySmall
                                ?.copyWith(color: colors.onSurfaceVariant),
                          ),
                        ] else if (c.phase == UploadPhase.processing) ...[
                          const SizedBox(height: 9),
                          Text(
                            AppStrings.t('uploadTray.processingHint'),
                            style: Theme.of(context).textTheme.bodySmall
                                ?.copyWith(color: colors.onSurfaceVariant),
                          ),
                        ],
                        if (c.error != null) ...[
                          const SizedBox(height: 9),
                          Text(
                            c.error!,
                            maxLines: 4,
                            overflow: TextOverflow.ellipsis,
                            style: Theme.of(context).textTheme.bodySmall
                                ?.copyWith(color: colors.error, height: 1.35),
                          ),
                        ],
                      ],
                    ),
                  ),
                ),
                const SizedBox(height: 10),
                Wrap(
                  alignment: WrapAlignment.end,
                  spacing: 4,
                  runSpacing: 4,
                  children: [
                    if (c.busy)
                      TextButton.icon(
                        onPressed: c.cancel,
                        icon: const Icon(Icons.stop_circle_outlined, size: 18),
                        label: Text(AppStrings.t('parity.cancel')),
                      )
                    else if (!done) ...[
                      TextButton(
                        onPressed: () => close(dismissUpload: true),
                        child: Text(AppStrings.t('parity.close')),
                      ),
                      FilledButton.icon(
                        onPressed: c.retry,
                        icon: const Icon(Icons.refresh_rounded, size: 18),
                        label: Text(AppStrings.t('parity.retry')),
                      ),
                    ] else
                      FilledButton(
                        onPressed: () => close(dismissUpload: true),
                        child: Text(AppStrings.t('parity.close')),
                      ),
                  ],
                ),
              ],
            ),
          ),
        ),
      );
    },
  );
}

String _uploadTitle(UploadController controller) =>
    AppStrings.t(switch (controller.phase) {
      UploadPhase.uploading => 'uploadTray.uploading',
      UploadPhase.processing => 'uploadTray.processing',
      UploadPhase.completed => 'parity.uploadDone',
      UploadPhase.partial => 'parity.playlistPartial',
      UploadPhase.cancelled => 'parity.cancelled',
      UploadPhase.failed => 'uploadTray.failed',
      _ => 'uploadTray.failed',
    });

IconData _uploadIcon(UploadController controller) => switch (controller.phase) {
  UploadPhase.failed => Icons.error_outline_rounded,
  UploadPhase.completed => Icons.check_rounded,
  UploadPhase.cancelled => Icons.pause_rounded,
  UploadPhase.partial => Icons.playlist_add_check_rounded,
  _ => Icons.arrow_upward_rounded,
};

Color _uploadAccent(ColorScheme colors, UploadController controller) =>
    switch (controller.phase) {
      UploadPhase.failed => colors.error,
      UploadPhase.completed => const Color(0xFF10B981),
      _ => colors.primary,
    };

String _formatUploadBytes(int bytes) {
  if (bytes < 1024) return '$bytes B';
  if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(1)} KB';
  if (bytes < 1024 * 1024 * 1024) {
    return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';
  }
  return '${(bytes / (1024 * 1024 * 1024)).toStringAsFixed(1)} GB';
}
