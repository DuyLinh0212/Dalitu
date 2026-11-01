import 'dart:async';
import 'dart:io';

import 'package:better_native_video_player/better_native_video_player.dart';
import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';

import '../../auth.dart';
import '../../channel/models/channel_models.dart';
import '../../core/localization/app_strings.dart';
import '../../core/theme/app_icons.dart';
import '../../core/theme/app_theme.dart';
import '../../core/widgets/hutube_widgets.dart';
import '../content/content_models.dart';
import '../content/content_service.dart';
import '../plans/plan_service.dart';
import 'creator_service.dart';
import 'video_metadata.dart';
import 'upload_controller.dart';
import '../moderation/moderation_screen.dart';
import 'upload_draft_store.dart';
import '../playlists/playlist_selector.dart';

class VideoUploadScreen extends StatefulWidget {
  const VideoUploadScreen({
    super.key,
    required this.auth,
    required this.channel,
  });
  final AuthController auth;
  final ChannelDetail channel;

  @override
  State<VideoUploadScreen> createState() => _VideoUploadScreenState();
}

class _VideoUploadScreenState extends State<VideoUploadScreen> {
  final _formKey = GlobalKey<FormState>();
  final _picker = ImagePicker();
  final _title = TextEditingController();
  final _description = TextEditingController();
  final _tags = TextEditingController();
  late final CreatorService _creator = CreatorService(widget.auth);
  late final ContentService _content = ContentService(widget.auth);
  List<Category> _categories = const [];
  XFile? _video;
  XFile? _thumbnail;
  NativeVideoPlayerController? _previewPlayer;
  int _durationSeconds = 0;
  String? _categoryId;
  List<VideoDetail> _relatedVideos = const [];
  final List<Map<String, dynamic>> _videoCards = [];
  String? _selectedRelatedVideoId;
  final _relatedTime = TextEditingController(text: '0:00');
  String _visibility = 'private';
  bool _ageRestricted = false;
  bool _canPromote = false;
  bool _promotionEnabled = false;
  bool _policyAccepted = false;
  bool _loadingCategories = true;
  bool _loadingRelatedVideos = true;
  bool _uploading = false;
  String? _error;
  final List<Map<String, dynamic>> _chapters = [];
  final _drafts = const UploadDraftStore();
  String _languageCode = 'vi';
  bool _allowComments = true;
  VideoMetadata? _metadata;
  String? _sourceQuality;
  String? _draftQuality;
  bool _preparing = false;
  int _fileGeneration = 0;
  Set<String> _playlistIds = {};
  late final String _draftUser = '${widget.auth.user?['userId'] ?? ''}';

  Future<void> _draftAction(String action) async {
    if (_uploading || _draftUser.isEmpty) return;
    try {
      if (action == 'save') {
        await _drafts.save(_draftUser, widget.channel.id, {
          'title': _title.text,
          'description': _description.text,
          'tags': _tags.text,
          'languageCode': _languageCode,
          'allowComments': _allowComments,
          'sourceQuality': _sourceQuality ?? _draftQuality,
          'visibility': _visibility,
          'categoryId': _categoryId,
          'ageRestricted': _ageRestricted,
          'promotionEnabled': _promotionEnabled,
          'chapters': _chapters,
          'videoCards': _videoCards,
          'playlistIds': _playlistIds.toList(),
        });
      } else if (action == 'discard') {
        await _drafts.remove(_draftUser, widget.channel.id);
      } else {
        final data = await _drafts.read(_draftUser, widget.channel.id);
        if (data == null) throw const FormatException('Missing draft');
        if (!mounted || '${widget.auth.user?['userId'] ?? ''}' != _draftUser) {
          return;
        }
        ++_fileGeneration;
        final oldPlayer = _previewPlayer;
        setState(() {
          _title.text = data['title'] as String;
          _description.text = data['description'] as String;
          _tags.text = data['tags'] as String;
          _languageCode = data['languageCode'] as String;
          _visibility = data['visibility'] as String;
          _draftQuality = data['sourceQuality'] is String
              ? data['sourceQuality'] as String
              : null;
          final category = data['categoryId'];
          _categoryId = _categories.any((c) => c.id == category)
              ? category as String?
              : null;
          _allowComments = data['allowComments'] != false;
          _ageRestricted = data['ageRestricted'] == true;
          _promotionEnabled = data['promotionEnabled'] == true;
          _chapters
            ..clear()
            ..addAll(
              (data['chapters'] as List).map(
                (v) => Map<String, dynamic>.from(v as Map),
              ),
            );
          _videoCards
            ..clear()
            ..addAll(
              (data['videoCards'] as List).map(
                (v) => Map<String, dynamic>.from(v as Map),
              ),
            );
          _playlistIds = (data['playlistIds'] as List).cast<String>().toSet();
          _video = null;
          _thumbnail = null;
          _metadata = null;
          _sourceQuality = null;
          _durationSeconds = 0;
          _previewPlayer = null;
          _preparing = false;
          _policyAccepted = false;
        });
        unawaited(oldPlayer?.dispose());
      }
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              AppStrings.t(
                action == 'restore'
                    ? 'parity.draftRestored'
                    : action == 'save'
                    ? 'parity.draftSaved'
                    : 'parity.discardDraft',
              ),
            ),
          ),
        );
      }
    } catch (_) {
      if (mounted) setState(() => _error = AppStrings.t('parity.draftError'));
    }
  }

  Future<void> _inspectVideo(XFile file) async {
    final generation = ++_fileGeneration;
    setState(() {
      _preparing = true;
      _metadata = null;
      _sourceQuality = null;
    });
    try {
      final data = await VideoMetadata.inspect(file.path);
      if (!mounted || generation != _fileGeneration || _video != file) return;
      setState(() {
        _metadata = data;
        _durationSeconds = data.durationSeconds;
        _sourceQuality = data.selectQuality(_draftQuality);
        _draftQuality = null;
        _thumbnail = data.thumbnails.isEmpty
            ? null
            : XFile(data.thumbnails.first);
      });
    } catch (_) {
      if (mounted && generation == _fileGeneration) {
        setState(() => _error = AppStrings.t('parity.metadataError'));
      }
    } finally {
      if (mounted && generation == _fileGeneration) {
        setState(() => _preparing = false);
      }
    }
  }

  List<VideoDetail> get _availableRelatedVideos => _relatedVideos
      .where((item) => !_videoCards.any((card) => card['videoId'] == item.id))
      .toList();

  void _addChapter() {
    setState(() {
      final lastSec = _chapters.isEmpty
          ? 0
          : (_chapters.last['startSeconds'] as int) + 60;
      _chapters.add({
        'startSeconds': lastSec,
        'title': 'Chương ${_chapters.length + 1}',
      });
    });
  }

  @override
  void initState() {
    super.initState();
    if (widget.channel.status == 'suspended') return;
    _loadCategories();
    _loadRelatedVideos();
    _loadPromotionEntitlement();
  }

  @override
  void dispose() {
    _title.dispose();
    _description.dispose();
    _tags.dispose();
    _relatedTime.dispose();
    unawaited(_previewPlayer?.dispose());
    super.dispose();
  }

  Future<void> _loadCategories() async {
    try {
      final categories = await _content.categories();
      if (mounted) {
        setState(() {
          _categories = categories;
          _loadingCategories = false;
        });
      }
    } catch (_) {
      if (mounted) setState(() => _loadingCategories = false);
    }
  }

  Future<void> _pickVideo() async {
    final file = await _picker.pickVideo(source: ImageSource.gallery);
    if (file == null || !mounted) return;
    final oldPlayer = _previewPlayer;
    setState(() {
      _video = file;
      _previewPlayer = null;
      _durationSeconds = 0;
      _error = null;
      if (_title.text.trim().isEmpty) {
        final rawName = file.name.split('.').first;
        _title.text = rawName.replaceAll('_', ' ').replaceAll('-', ' ');
      }
    });
    unawaited(oldPlayer?.dispose());
    if (Platform.isAndroid) {
      unawaited(_inspectVideo(file));
    } else {
      setState(() {
        _metadata = null;
        _sourceQuality = null;
        _preparing = true;
      });
    }
    final player = NativeVideoPlayerController(
      id: DateTime.now().microsecondsSinceEpoch & 0x7fffffff,
      autoPlay: false,
      showNativeControls: true,
    );
    setState(() => _previewPlayer = player);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && identical(_previewPlayer, player)) {
        unawaited(_initializePreview(player, file));
      }
    });
  }

  Future<void> _initializePreview(
    NativeVideoPlayerController player,
    XFile file,
  ) async {
    try {
      final durationFuture = player.durationStream
          .firstWhere((duration) => duration > Duration.zero)
          .timeout(const Duration(seconds: 20));
      await player.initialize();
      await player.loadFile(path: file.path);
      final duration = await durationFuture;
      if (!mounted || !identical(_previewPlayer, player) || _video != file) {
        return;
      }
      if (!Platform.isAndroid) {
        final size =
            player.videoSize ??
            await player.videoSizeStream
                .firstWhere((size) => size.width > 0 && size.height > 0)
                .timeout(const Duration(seconds: 20));
        if (!mounted || !identical(_previewPlayer, player) || _video != file) {
          return;
        }
        final metadata = VideoMetadata(
          durationSeconds: (duration.inMilliseconds / 1000).ceil(),
          width: size.width.round(),
          height: size.height.round(),
        );
        if (metadata.qualities.isEmpty) {
          throw const FormatException('Invalid video size');
        }
        setState(() {
          _metadata = metadata;
          _sourceQuality = metadata.selectQuality(_draftQuality);
          _draftQuality = null;
          _durationSeconds = metadata.durationSeconds;
          _preparing = false;
        });
      } else if (_metadata == null && _durationSeconds == 0) {
        setState(() => _durationSeconds = duration.inSeconds);
      }
    } catch (_) {
      if (mounted && identical(_previewPlayer, player)) {
        setState(() {
          _error = AppStrings.t('upload.previewError');
          if (!Platform.isAndroid) _preparing = false;
        });
      }
    }
  }

  Future<void> _loadRelatedVideos() async {
    try {
      final result = await _creator.videoMetrics(
        widget.channel.id,
        includeStats: false,
      );
      if (!mounted) return;
      setState(() {
        _relatedVideos = result.items
            .where(
              (item) =>
                  item.processingStatus == 'published' &&
                  item.moderationStatus == 'approved' &&
                  item.visibility == 'public' &&
                  item.publishedAt != null,
            )
            .toList();
        _loadingRelatedVideos = false;
      });
    } catch (_) {
      if (mounted) setState(() => _loadingRelatedVideos = false);
    }
  }

  Future<void> _loadPromotionEntitlement() async {
    try {
      final plan = await PlanService(widget.auth).myPlan();
      final features = plan?['features'];
      final allowed = features is Map && features['video_promotion'] == true;
      if (mounted) setState(() => _canPromote = allowed);
    } catch (_) {
      if (mounted) setState(() => _canPromote = false);
    }
  }

  static int _parseVideoCardTime(String value) {
    final match = RegExp(r'^(\d+):([0-5]?\d)$').firstMatch(value.trim());
    if (match == null) return -1;
    return int.parse(match.group(1)!) * 60 + int.parse(match.group(2)!);
  }

  static String _formatVideoCardTime(int seconds) =>
      '${seconds ~/ 60}:${(seconds % 60).toString().padLeft(2, '0')}';

  void _addVideoCard() {
    final videoId = _selectedRelatedVideoId;
    final seconds = _parseVideoCardTime(_relatedTime.text);
    if (videoId == null ||
        seconds < 0 ||
        seconds >= _durationSeconds ||
        _videoCards.length >= 5 ||
        _videoCards.any(
          (card) =>
              card['videoId'] == videoId || card['startSeconds'] == seconds,
        )) {
      setState(() => _error = AppStrings.t('upload.videoCardFieldsInvalid'));
      return;
    }
    setState(() {
      _videoCards.add({'videoId': videoId, 'startSeconds': seconds});
      _videoCards.sort(
        (left, right) => (left['startSeconds'] as int).compareTo(
          right['startSeconds'] as int,
        ),
      );
      _selectedRelatedVideoId = null;
      _relatedTime.text = '0:00';
      _error = null;
    });
  }

  Future<void> _pickThumbnail() async {
    final file = await _picker.pickImage(
      source: ImageSource.gallery,
      imageQuality: 90,
    );
    if (file != null && mounted) setState(() => _thumbnail = file);
  }

  String _typeFor(String name, {required bool video}) {
    final extension = name.split('.').last.toLowerCase();
    if (video) {
      return switch (extension) {
        'webm' => 'video/webm',
        'mov' => 'video/quicktime',
        'mkv' => 'video/x-matroska',
        _ => 'video/mp4',
      };
    }
    return extension == 'png'
        ? 'image/png'
        : extension == 'webp'
        ? 'image/webp'
        : 'image/jpeg';
  }

  Future<void> _upload() async {
    setState(() => _error = null);
    if (!_formKey.currentState!.validate()) return;
    if (_video == null) {
      setState(() => _error = AppStrings.t('upload.chooseVideo'));
      return;
    }
    if (!_policyAccepted) {
      setState(() => _error = AppStrings.t('upload.policyRequired'));
      return;
    }
    if (_preparing || _sourceQuality == null || _metadata == null) {
      setState(() => _error = AppStrings.t('parity.metadataError'));
      return;
    }
    final uploader = UploadScope.of(context);
    if (uploader.busy) {
      setState(() => _error = AppStrings.t('parity.uploadBusy'));
      return;
    }
    final duration = _durationSeconds;
    var previousChapter = -1;
    for (final chapter in _chapters) {
      final seconds = chapter['startSeconds'] as int;
      if ('${chapter['title']}'.trim().isEmpty ||
          seconds <= previousChapter ||
          seconds >= duration) {
        setState(() => _error = AppStrings.t('upload.chaptersInvalid'));
        return;
      }
      previousChapter = seconds;
    }
    if (duration <= 0) {
      setState(() => _error = AppStrings.t('upload.durationInvalid'));
      return;
    }
    if (_videoCards.any((card) {
      final seconds = card['startSeconds'] as int;
      return seconds < 0 || seconds >= duration;
    })) {
      setState(() => _error = AppStrings.t('upload.videoCardsInvalid'));
      return;
    }
    final video = _video!;
    final contentType = _typeFor(video.name, video: true);
    setState(() => _uploading = true);
    try {
      String? draftRevision;
      try {
        draftRevision = await _drafts.revision(_draftUser, widget.channel.id);
      } catch (_) {
        /* Storage is optional for uploading. */
      }
      final size = await video.length();
      final allowed = await _creator.preflight(
        channelId: widget.channel.id,
        fileSize: size,
        duration: duration,
        contentType: contentType,
        quality: _sourceQuality!,
      );
      if (!allowed.allowed) {
        if (mounted) {
          setState(
            () => _error = AppStrings.format('upload.quotaExceeded', {
              'size': _bytes(allowed.maxUploadSize),
              'minutes': AppStrings.number(allowed.maxDuration ~/ 60),
            }),
          );
        }
        return;
      }
      final tags = _tags.text
          .split(',')
          .map((tag) => tag.trim())
          .where((tag) => tag.isNotEmpty)
          .toSet()
          .take(10)
          .toList();
      if (!mounted || '${widget.auth.user?['userId'] ?? ''}' != _draftUser) {
        return;
      }
      final creator = _creator;
      final channelId = widget.channel.id;
      final title = _title.text, description = _description.text;
      final categoryId = _categoryId,
          visibility = _visibility,
          languageCode = _languageCode;
      final quality = _sourceQuality!,
          ageRestricted = _ageRestricted,
          allowComments = _allowComments;
      final chapters = _chapters
          .map((c) => Map<String, dynamic>.of(c))
          .toList();
      final cards = _videoCards.map((c) => Map<String, dynamic>.of(c)).toList();
      final promotion = _canPromote && _promotionEnabled;
      final thumbnail = _thumbnail;
      final videoPart = MultipartFilePayload(
        field: 'Video',
        path: video.path,
        fileName: video.name,
        contentType: contentType,
      );
      final thumbnailPart = thumbnail == null
          ? null
          : MultipartFilePayload(
              field: 'Thumbnail',
              path: thumbnail.path,
              fileName: thumbnail.name,
              contentType: _typeFor(thumbnail.name, video: false),
            );
      final drafts = _drafts, draftUser = _draftUser;
      final started = uploader.start(
        name: video.name,
        playlistIds: _playlistIds.toList(),
        onSaved: () =>
            drafts.removeIfUnchanged(draftUser, channelId, draftRevision),
        task: (control, key, progress) => creator.upload(
          channelId: channelId,
          title: title,
          description: description,
          categoryId: categoryId,
          visibility: visibility,
          ageRestricted: ageRestricted,
          duration: duration,
          quality: quality,
          languageCode: languageCode,
          allowComments: allowComments,
          tags: tags,
          chapters: chapters,
          videoCards: cards,
          promotionEnabled: promotion,
          video: videoPart,
          thumbnail: thumbnailPart,
          idempotencyKey: key,
          uploadControl: control,
          onProgress: progress,
        ),
      );
      if (started && mounted) {
        Navigator.pop(context, true);
      } else if (mounted) {
        setState(() => _error = AppStrings.t('parity.uploadBusy'));
      }
    } on ApiFailure catch (error) {
      if (mounted) {
        setState(
          () => _error = AppStrings.apiError(error, fallback: 'upload.error'),
        );
      }
    } on FileSystemException {
      if (mounted) {
        setState(() => _error = AppStrings.t('upload.fileReadError'));
      }
    } catch (_) {
      if (mounted) {
        setState(() => _error = AppStrings.t('upload.error'));
      }
    } finally {
      if (mounted) setState(() => _uploading = false);
    }
  }

  static String _bytes(int bytes) {
    if (bytes <= 0) return '0 MB';
    return '${(bytes / (1024 * 1024)).toStringAsFixed(0)} MB';
  }

  @override
  Widget build(BuildContext context) => widget.channel.status == 'suspended'
      ? Scaffold(
          appBar: AppBar(title: Text(AppStrings.t('upload.title'))),
          body: HuTubeStateView(
            icon: Icons.pause_circle_outline_rounded,
            title: AppStrings.t('channel.suspendedTitle'),
            message: AppStrings.t('creator.uploadSuspended'),
            actionLabel: AppStrings.t('channel.statusAndAppeal'),
            onAction: () => Navigator.of(context).push(
              MaterialPageRoute(
                builder: (_) => Scaffold(
                  appBar: AppBar(
                    title: Text(AppStrings.t('channel.statusAndAppeal')),
                  ),
                  body: ModerationScreen(
                    auth: widget.auth,
                    channel: widget.channel,
                  ),
                ),
              ),
            ),
          ),
        )
      : Scaffold(
          appBar: AppBar(
            title: Text(AppStrings.t('upload.title')),
            actions: [
              PopupMenuButton<String>(
                onSelected: _uploading ? null : _draftAction,
                itemBuilder: (_) => [
                  for (final action in ['save', 'restore', 'discard'])
                    PopupMenuItem(
                      value: action,
                      child: Text(AppStrings.t('parity.${action}Draft')),
                    ),
                ],
              ),
            ],
          ),
          bottomNavigationBar: Material(
            color: Theme.of(context).colorScheme.surface,
            child: SafeArea(
              top: false,
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 10, 16, 10),
                child: FilledButton.icon(
                  onPressed: _uploading ? null : _upload,
                  icon: _uploading
                      ? const SizedBox.square(
                          dimension: 18,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: Colors.white,
                          ),
                        )
                      : const Icon(Icons.cloud_upload_outlined),
                  label: Text(
                    _uploading
                        ? AppStrings.t('upload.uploading')
                        : AppStrings.t('upload.uploadAction'),
                  ),
                ),
              ),
            ),
          ),
          body: SafeArea(
            child: Form(
              key: _formKey,
              child: ListView(
                keyboardDismissBehavior:
                    ScrollViewKeyboardDismissBehavior.onDrag,
                padding: const EdgeInsets.fromLTRB(16, 20, 16, 32),
                children: [
                  _FilePicker(
                    customIcon: AppIcons.asset(AppIcons.upload, size: 24),
                    title: _video?.name ?? AppStrings.t('upload.chooseVideo'),
                    subtitle: _video == null
                        ? AppStrings.t('upload.videoQuotaHint')
                        : AppStrings.t('upload.selectedVideo'),
                    onTap: _uploading ? null : _pickVideo,
                  ),
                  if (_video != null) ...[
                    const SizedBox(height: 12),
                    ClipRRect(
                      borderRadius: BorderRadius.circular(14),
                      child: AspectRatio(
                        aspectRatio: 16 / 9,
                        child: _previewPlayer == null
                            ? const Center(child: CircularProgressIndicator())
                            : NativeVideoPlayer(controller: _previewPlayer!),
                      ),
                    ),
                  ],
                  const SizedBox(height: 24),
                  Text(
                    AppStrings.t('upload.detailsSection'),
                    style: Theme.of(context).textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  const SizedBox(height: 12),
                  TextFormField(
                    controller: _title,
                    enabled: !_uploading,
                    maxLength: 100,
                    decoration: InputDecoration(
                      labelText: AppStrings.t('upload.titleField'),
                    ),
                    validator: (value) => (value ?? '').trim().isEmpty
                        ? AppStrings.t('upload.titleRequired')
                        : null,
                  ),
                  const SizedBox(height: 16),
                  TextFormField(
                    controller: _description,
                    enabled: !_uploading,
                    minLines: 3,
                    maxLines: 6,
                    maxLength: 5000,
                    decoration: InputDecoration(
                      labelText: AppStrings.t('creator.descriptionField'),
                      alignLabelWithHint: true,
                    ),
                  ),
                  const SizedBox(height: 16),
                  _FilePicker(
                    icon: Icons.image_outlined,
                    title:
                        _thumbnail?.name ??
                        AppStrings.t('upload.chooseThumbnail'),
                    subtitle: _thumbnail == null
                        ? AppStrings.t('upload.thumbnailHint')
                        : AppStrings.t('upload.selectedThumbnail'),
                    onTap: _uploading ? null : _pickThumbnail,
                  ),
                  if (_preparing) ...[
                    const SizedBox(height: 16),
                    const LinearProgressIndicator(),
                  ],
                  if (_metadata != null) ...[
                    const SizedBox(height: 16),
                    if (_metadata!.thumbnails.isNotEmpty) ...[
                      Text(AppStrings.t('parity.thumbnail')),
                      Wrap(
                        spacing: 8,
                        runSpacing: 8,
                        children: [
                          for (final path in _metadata!.thumbnails)
                            Semantics(
                              button: true,
                              selected: _thumbnail?.path == path,
                              label: AppStrings.t('parity.thumbnail'),
                              child: InkWell(
                                onTap: _uploading
                                    ? null
                                    : () => setState(
                                        () => _thumbnail = XFile(path),
                                      ),
                                child: Container(
                                  padding: const EdgeInsets.all(3),
                                  decoration: BoxDecoration(
                                    border: Border.all(
                                      width: 3,
                                      color: _thumbnail?.path == path
                                          ? Theme.of(
                                              context,
                                            ).colorScheme.primary
                                          : Colors.transparent,
                                    ),
                                  ),
                                  child: Image.file(
                                    File(path),
                                    width: 86,
                                    height: 60,
                                    fit: BoxFit.cover,
                                  ),
                                ),
                              ),
                            ),
                        ],
                      ),
                    ],
                    DropdownButtonFormField<String>(
                      isExpanded: true,
                      key: ValueKey('quality-$_fileGeneration-$_sourceQuality'),
                      initialValue: _sourceQuality,
                      decoration: InputDecoration(
                        labelText: AppStrings.t('parity.quality'),
                        helperText: AppStrings.t('parity.sourceHint'),
                        helperMaxLines: 2,
                      ),
                      items: _metadata!.qualities
                          .map(
                            (q) => DropdownMenuItem(value: q, child: Text(q)),
                          )
                          .toList(),
                      onChanged: _uploading
                          ? null
                          : (q) => setState(() => _sourceQuality = q),
                    ),
                  ],
                  const SizedBox(height: 24),
                  Card(
                    margin: EdgeInsets.zero,
                    clipBehavior: Clip.antiAlias,
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(16),
                      side: BorderSide(color: AppColors.borderFor(context)),
                    ),
                    child: ExpansionTile(
                      maintainState: true,
                      shape: const Border(),
                      collapsedShape: const Border(),
                      tilePadding: const EdgeInsets.symmetric(
                        horizontal: 16,
                        vertical: 8,
                      ),
                      leading: const Icon(Icons.tune_rounded),
                      title: Text(AppStrings.t('upload.optionsSection')),
                      childrenPadding: const EdgeInsets.fromLTRB(
                        16,
                        16,
                        16,
                        20,
                      ),
                      children: [
                        DropdownButtonFormField<String>(
                          isExpanded: true,
                          key: ValueKey('language-$_languageCode'),
                          initialValue: _languageCode,
                          decoration: InputDecoration(
                            labelText: AppStrings.t('parity.language'),
                          ),
                          items: [
                            DropdownMenuItem(
                              value: 'vi',
                              child: Text('Tiếng Việt'),
                            ),
                            DropdownMenuItem(
                              value: 'en',
                              child: Text('English'),
                            ),
                            DropdownMenuItem(value: 'ja', child: Text('日本語')),
                            DropdownMenuItem(value: 'ko', child: Text('한국어')),
                            DropdownMenuItem(value: 'zh', child: Text('中文')),
                            DropdownMenuItem(
                              value: 'other',
                              child: Text(AppStrings.t('upload.otherLanguage')),
                            ),
                          ],
                          onChanged: _uploading
                              ? null
                              : (value) =>
                                    setState(() => _languageCode = value!),
                        ),
                        const SizedBox(height: 16),
                        SwitchListTile(
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 4,
                            vertical: 8,
                          ),
                          value: _allowComments,
                          title: Text(AppStrings.t('parity.comments')),
                          onChanged: _uploading
                              ? null
                              : (v) => setState(() => _allowComments = v),
                        ),
                        const SizedBox(height: 12),
                        DecoratedBox(
                          decoration: BoxDecoration(
                            border: Border.all(
                              color: AppColors.borderFor(context),
                            ),
                            borderRadius: BorderRadius.circular(12),
                          ),
                          child: PlaylistSelector(
                            auth: widget.auth,
                            selected: _playlistIds,
                            enabled: !_uploading,
                            onChanged: (values) =>
                                setState(() => _playlistIds = values),
                          ),
                        ),
                        const SizedBox(height: 20),
                        InputDecorator(
                          decoration: InputDecoration(
                            labelText: AppStrings.t('upload.durationField'),
                            helperText: AppStrings.t('upload.durationHint'),
                          ),
                          child: Align(
                            alignment: Alignment.centerLeft,
                            child: Text(
                              _durationSeconds > 0
                                  ? _formatVideoCardTime(_durationSeconds)
                                  : _video == null
                                  ? AppStrings.t(
                                      'upload.durationWaitingForVideo',
                                    )
                                  : AppStrings.t('upload.preparing'),
                            ),
                          ),
                        ),
                        const SizedBox(height: 14),
                        DropdownButtonFormField<String?>(
                          key: ValueKey('upload-category-$_categoryId'),
                          initialValue: _categoryId,
                          isExpanded: true,
                          decoration: InputDecoration(
                            labelText: AppStrings.t('upload.topic'),
                          ),
                          items: [
                            DropdownMenuItem<String?>(
                              value: null,
                              child: Text(AppStrings.t('upload.none')),
                            ),
                            ..._categories.map(
                              (category) => DropdownMenuItem<String?>(
                                value: category.id,
                                child: Text(category.displayName),
                              ),
                            ),
                          ],
                          onChanged: _loadingCategories || _uploading
                              ? null
                              : (value) => setState(() => _categoryId = value),
                        ),
                        const SizedBox(height: 14),
                        TextFormField(
                          controller: _tags,
                          enabled: !_uploading,
                          decoration: InputDecoration(
                            labelText: AppStrings.t('upload.tags'),
                            helperText: AppStrings.t('upload.tagsHint'),
                          ),
                        ),
                        const SizedBox(height: 14),
                      ],
                    ),
                  ),
                  const SizedBox(height: 16),
                  Card(
                    margin: EdgeInsets.zero,
                    clipBehavior: Clip.antiAlias,
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(16),
                      side: BorderSide(color: AppColors.borderFor(context)),
                    ),
                    child: ExpansionTile(
                      maintainState: true,
                      shape: const Border(),
                      collapsedShape: const Border(),
                      tilePadding: const EdgeInsets.symmetric(
                        horizontal: 16,
                        vertical: 8,
                      ),
                      leading: const Icon(Icons.video_settings_outlined),
                      title: Text(AppStrings.t('upload.advancedSection')),
                      childrenPadding: const EdgeInsets.fromLTRB(
                        16,
                        16,
                        16,
                        20,
                      ),
                      children: [
                        Wrap(
                          alignment: WrapAlignment.spaceBetween,
                          spacing: 8,
                          runSpacing: 8,
                          children: [
                            const Text(
                              'Chương video (Chapters)',
                              style: TextStyle(
                                fontWeight: FontWeight.w700,
                                fontSize: 13,
                              ),
                            ),
                            TextButton.icon(
                              onPressed: _uploading ? null : _addChapter,
                              icon: const Icon(Icons.add, size: 16),
                              label: const Text('Thêm chương'),
                            ),
                          ],
                        ),
                        if (_chapters.isNotEmpty)
                          ..._chapters.asMap().entries.map((entry) {
                            final idx = entry.key;
                            final ch = entry.value;
                            return Padding(
                              padding: const EdgeInsets.only(bottom: 8),
                              child: Row(
                                children: [
                                  SizedBox(
                                    width: 80,
                                    child: TextFormField(
                                      initialValue: '${ch['startSeconds']}',
                                      enabled: !_uploading,
                                      keyboardType: TextInputType.number,
                                      decoration: const InputDecoration(
                                        labelText: 'Giây',
                                        isDense: true,
                                      ),
                                      onChanged: (val) {
                                        ch['startSeconds'] =
                                            int.tryParse(val) ?? 0;
                                      },
                                    ),
                                  ),
                                  const SizedBox(width: 8),
                                  Expanded(
                                    child: TextFormField(
                                      initialValue: '${ch['title']}',
                                      enabled: !_uploading,
                                      decoration: const InputDecoration(
                                        labelText: 'Tiêu đề chương',
                                        isDense: true,
                                      ),
                                      onChanged: (val) {
                                        ch['title'] = val.trim();
                                      },
                                    ),
                                  ),
                                  IconButton(
                                    icon: const Icon(
                                      Icons.delete_outline,
                                      size: 20,
                                    ),
                                    onPressed: _uploading
                                        ? null
                                        : () => setState(
                                            () => _chapters.removeAt(idx),
                                          ),
                                  ),
                                ],
                              ),
                            );
                          }),
                        const SizedBox(height: 14),
                        Text(
                          AppStrings.t('upload.relatedVideos'),
                          style: const TextStyle(
                            fontWeight: FontWeight.w700,
                            fontSize: 13,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          AppStrings.t('upload.relatedVideosHint'),
                          style: Theme.of(context).textTheme.bodySmall,
                        ),
                        if (_loadingRelatedVideos)
                          const Padding(
                            padding: EdgeInsets.symmetric(vertical: 12),
                            child: LinearProgressIndicator(),
                          )
                        else if (_availableRelatedVideos.isNotEmpty &&
                            _videoCards.length < 5) ...[
                          const SizedBox(height: 8),
                          DropdownButtonFormField<String>(
                            initialValue: _selectedRelatedVideoId,
                            isExpanded: true,
                            decoration: InputDecoration(
                              labelText: AppStrings.t(
                                'upload.relatedVideoSelect',
                              ),
                            ),
                            items: _availableRelatedVideos
                                .map(
                                  (item) => DropdownMenuItem(
                                    value: item.id,
                                    child: Text(
                                      item.title,
                                      maxLines: 1,
                                      overflow: TextOverflow.ellipsis,
                                    ),
                                  ),
                                )
                                .toList(),
                            onChanged: _uploading || _durationSeconds <= 0
                                ? null
                                : (value) => setState(
                                    () => _selectedRelatedVideoId = value,
                                  ),
                          ),
                          const SizedBox(height: 8),
                          Row(
                            children: [
                              Expanded(
                                child: TextField(
                                  controller: _relatedTime,
                                  enabled: !_uploading,
                                  keyboardType: TextInputType.datetime,
                                  decoration: InputDecoration(
                                    labelText: AppStrings.t(
                                      'upload.relatedVideoTime',
                                    ),
                                    hintText: '0:00',
                                  ),
                                ),
                              ),
                              const SizedBox(width: 8),
                              OutlinedButton.icon(
                                onPressed:
                                    _uploading ||
                                        _durationSeconds <= 0 ||
                                        _selectedRelatedVideoId == null
                                    ? null
                                    : _addVideoCard,
                                icon: const Icon(Icons.add),
                                label: Text(
                                  AppStrings.t('upload.addRelatedVideo'),
                                ),
                              ),
                            ],
                          ),
                        ] else if (_relatedVideos.isEmpty &&
                            !_loadingRelatedVideos)
                          Padding(
                            padding: const EdgeInsets.symmetric(vertical: 10),
                            child: Text(AppStrings.t('upload.noRelatedVideos')),
                          ),
                        if (_videoCards.isNotEmpty)
                          ..._videoCards.map((card) {
                            final relatedVideo = _relatedVideos
                                .where((item) => item.id == card['videoId'])
                                .firstOrNull;
                            return ListTile(
                              contentPadding: EdgeInsets.zero,
                              dense: true,
                              title: Text(
                                relatedVideo?.title ?? '',
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                              ),
                              subtitle: Text(
                                _formatVideoCardTime(
                                  card['startSeconds'] as int,
                                ),
                              ),
                              trailing: IconButton(
                                tooltip: AppStrings.t(
                                  'upload.removeRelatedVideo',
                                ),
                                icon: const Icon(Icons.close),
                                onPressed: _uploading
                                    ? null
                                    : () => setState(
                                        () => _videoCards.remove(card),
                                      ),
                              ),
                            );
                          }),
                        if (_canPromote)
                          SwitchListTile.adaptive(
                            contentPadding: EdgeInsets.zero,
                            value: _promotionEnabled,
                            onChanged: _uploading
                                ? null
                                : (value) =>
                                      setState(() => _promotionEnabled = value),
                            title: Text(AppStrings.t('upload.promoteVideo')),
                            subtitle: Text(
                              AppStrings.t('upload.promoteVideoHint'),
                            ),
                          ),
                        const SizedBox(height: 14),
                      ],
                    ),
                  ),
                  const SizedBox(height: 24),
                  DropdownButtonFormField<String>(
                    isExpanded: true,
                    key: ValueKey('upload-visibility-$_visibility'),
                    initialValue: _visibility,
                    decoration: InputDecoration(
                      labelText: AppStrings.t('upload.visibility'),
                    ),
                    items: [
                      DropdownMenuItem(
                        value: 'private',
                        child: Text(AppStrings.t('upload.private')),
                      ),
                      DropdownMenuItem(
                        value: 'unlisted',
                        child: Text(AppStrings.t('upload.unlisted')),
                      ),
                      DropdownMenuItem(
                        value: 'public',
                        child: Text(AppStrings.t('upload.public')),
                      ),
                    ],
                    onChanged: _uploading
                        ? null
                        : (value) => setState(() => _visibility = value!),
                  ),
                  const SizedBox(height: 16),
                  SwitchListTile.adaptive(
                    contentPadding: EdgeInsets.zero,
                    value: _ageRestricted,
                    onChanged: _uploading
                        ? null
                        : (value) => setState(() => _ageRestricted = value),
                    title: Text(AppStrings.t('upload.ageLimit')),
                    subtitle: Text(
                      AppStrings.t('upload.ageRestrictionDescription'),
                    ),
                  ),
                  const SizedBox(height: 12),
                  CheckboxListTile(
                    contentPadding: EdgeInsets.zero,
                    value: _policyAccepted,
                    onChanged: _uploading
                        ? null
                        : (value) =>
                              setState(() => _policyAccepted = value ?? false),
                    title: Text(AppStrings.t('upload.policyAgreement')),
                    controlAffinity: ListTileControlAffinity.leading,
                  ),
                  if (_error != null)
                    Padding(
                      padding: const EdgeInsets.only(bottom: 12),
                      child: HuTubeSurface(
                        padding: const EdgeInsets.all(13),
                        color: Theme.of(context).colorScheme.errorContainer,
                        border: BorderSide(
                          color: Theme.of(
                            context,
                          ).colorScheme.error.withValues(alpha: .25),
                        ),
                        child: Text(
                          _error!,
                          style: TextStyle(
                            color: Theme.of(
                              context,
                            ).colorScheme.onErrorContainer,
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                      ),
                    ),
                ],
              ),
            ),
          ),
        );
}

class _FilePicker extends StatelessWidget {
  const _FilePicker({
    this.icon,
    this.customIcon,
    required this.title,
    required this.subtitle,
    this.onTap,
  });
  final IconData? icon;
  final Widget? customIcon;
  final String title;
  final String subtitle;
  final VoidCallback? onTap;
  @override
  Widget build(BuildContext context) => Material(
    color: AppColors.surfaceFor(context),
    shape: RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(14),
      side: BorderSide(color: AppColors.borderFor(context)),
    ),
    clipBehavior: Clip.antiAlias,
    child: ListTile(
      contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
      onTap: onTap,
      leading: Container(
        width: 42,
        height: 42,
        decoration: BoxDecoration(
          color: AppColors.primary.withValues(alpha: .1),
          borderRadius: BorderRadius.circular(12),
        ),
        alignment: Alignment.center,
        child: customIcon ?? Icon(icon, color: AppColors.primary),
      ),
      title: Text(
        title,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: const TextStyle(fontWeight: FontWeight.w800),
      ),
      subtitle: Text(subtitle),
      trailing: const Icon(Icons.chevron_right_rounded),
    ),
  );
}
