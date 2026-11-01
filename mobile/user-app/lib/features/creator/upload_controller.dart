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
      final failed = c.phase == UploadPhase.failed;
      final done = c.phase == UploadPhase.completed;
      final accent = failed
          ? colors.error
          : done
          ? const Color(0xFF10B981)
          : colors.primary;
      final title = AppStrings.t(switch (c.phase) {
        UploadPhase.uploading => 'uploadTray.uploading',
        UploadPhase.processing => 'uploadTray.processing',
        UploadPhase.completed =>
          c.video?.processingStatus == 'published'
              ? 'uploadTray.published'
              : _savedHint(c.video) ?? 'parity.uploadDone',
        UploadPhase.partial => 'parity.playlistPartial',
        UploadPhase.cancelled => 'parity.cancelled',
        _ => 'parity.uploadFailed',
      });
      final statusIcon = failed
          ? Icons.error_outline_rounded
          : done
          ? Icons.check_rounded
          : c.phase == UploadPhase.cancelled
          ? Icons.pause_rounded
          : c.phase == UploadPhase.partial
          ? Icons.playlist_add_check_rounded
          : Icons.arrow_upward_rounded;
      return SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(10, 4, 10, 6),
          child: Align(
            heightFactor: 1,
            alignment: Alignment.bottomCenter,
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 600),
              child: Material(
                key: const ValueKey('upload-progress-card'),
                elevation: 1,
                shadowColor: colors.shadow.withValues(alpha: .08),
                color: colors.surfaceContainerLow,
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(16),
                  side: BorderSide(
                    color: colors.outlineVariant.withValues(alpha: .6),
                  ),
                ),
                clipBehavior: Clip.antiAlias,
                child: Padding(
                  padding: const EdgeInsets.fromLTRB(12, 6, 4, 6),
                  child: Row(
                    crossAxisAlignment: CrossAxisAlignment.center,
                    children: [
                      Padding(
                        padding: EdgeInsets.zero,
                        child: SizedBox(
                          width: 32,
                          height: 32,
                          child: Stack(
                            alignment: Alignment.center,
                            children: [
                              DecoratedBox(
                                decoration: BoxDecoration(
                                  color: accent.withValues(alpha: .1),
                                  shape: BoxShape.circle,
                                ),
                                child: SizedBox.expand(
                                  child: Icon(
                                    statusIcon,
                                    size: 20,
                                    color: accent,
                                  ),
                                ),
                              ),
                              if (c.busy)
                                SizedBox.expand(
                                  child: CircularProgressIndicator(
                                    value: c.phase == UploadPhase.uploading
                                        ? c.progress
                                        : null,
                                    strokeWidth: 2.5,
                                    color: accent,
                                    backgroundColor: accent.withValues(
                                      alpha: .1,
                                    ),
                                  ),
                                ),
                            ],
                          ),
                        ),
                      ),
                      const SizedBox(width: 10),
                      Expanded(
                        child: Column(
                          mainAxisSize: MainAxisSize.min,
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Semantics(
                              liveRegion: true,
                              child: Text(
                                title,
                                style: Theme.of(context).textTheme.labelMedium
                                    ?.copyWith(
                                      color: accent,
                                      fontWeight: FontWeight.w700,
                                    ),
                              ),
                            ),
                            const SizedBox(height: 2),
                            Text(
                              c.fileName,
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                              style: Theme.of(context).textTheme.bodyMedium
                                  ?.copyWith(fontWeight: FontWeight.w600),
                            ),
                            if (c.phase == UploadPhase.uploading) ...[
                              const SizedBox(height: 8),
                              ClipRRect(
                                borderRadius: BorderRadius.circular(4),
                                child: LinearProgressIndicator(
                                  value: c.progress,
                                  minHeight: 4,
                                  color: accent,
                                  backgroundColor: accent.withValues(
                                    alpha: .12,
                                  ),
                                ),
                              ),
                              const SizedBox(height: 5),
                              Text(
                                c.total > 0
                                    ? '${((c.progress ?? 0) * 100).round()}% · ${_bytes(c.sent)} / ${_bytes(c.total)}'
                                    : AppStrings.t('uploadTray.connecting'),
                                style: Theme.of(context).textTheme.bodySmall
                                    ?.copyWith(color: colors.onSurfaceVariant),
                              ),
                            ],
                            if (c.error != null) ...[
                              const SizedBox(height: 4),
                              Text(
                                c.error!,
                                maxLines: 3,
                                overflow: TextOverflow.ellipsis,
                                style: Theme.of(context).textTheme.bodySmall
                                    ?.copyWith(
                                      color: colors.onSurfaceVariant,
                                      height: 1.4,
                                    ),
                              ),
                            ],
                            if (!c.busy && !done)
                              TextButton.icon(
                                style: TextButton.styleFrom(
                                  padding: const EdgeInsets.symmetric(
                                    horizontal: 4,
                                  ),
                                  minimumSize: const Size(48, 44),
                                  alignment: Alignment.centerLeft,
                                ),
                                onPressed: c.retry,
                                icon: const Icon(
                                  Icons.refresh_rounded,
                                  size: 18,
                                ),
                                label: Text(AppStrings.t('parity.retry')),
                              ),
                          ],
                        ),
                      ),
                      IconButton(
                        tooltip: AppStrings.t(
                          c.busy ? 'parity.cancel' : 'parity.close',
                        ),
                        onPressed: c.busy ? c.cancel : c.dismiss,
                        icon: Icon(
                          c.busy
                              ? Icons.stop_circle_outlined
                              : Icons.close_rounded,
                          size: 22,
                          color: colors.onSurfaceVariant,
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
      );
    },
  );

  static String? _savedHint(VideoDetail? video) {
    if (video?.processingStatus == 'failed') return 'uploadTray.savedFailed';
    if (video?.moderationStatus == 'pending' ||
        video?.moderationStatus == 'reviewing') {
      return 'uploadTray.savedPending';
    }
    if (video?.processingStatus == 'processing') {
      return 'uploadTray.savedProcessing';
    }
    return null;
  }

  static String _bytes(int bytes) {
    if (bytes < 1024) return '$bytes B';
    if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(1)} KB';
    if (bytes < 1024 * 1024 * 1024) {
      return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';
    }
    return '${(bytes / (1024 * 1024 * 1024)).toStringAsFixed(1)} GB';
  }
}
