import '../../core/widgets/confirmation_dialog.dart';
import '../../account/services/account_service.dart';
import '../../core/widgets/scrollable_sheet.dart';

import 'dart:async';
import 'dart:io';

import 'package:better_native_video_player/better_native_video_player.dart';
import 'package:flutter/foundation.dart' show kDebugMode;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:go_router/go_router.dart';
import 'package:share_plus/share_plus.dart';

import '../../auth.dart';
import '../../core/localization/app_strings.dart';
import '../../core/storage/app_preferences.dart';
import '../../core/theme/app_icons.dart';
import '../../core/media/picture_in_picture.dart';
import '../../core/theme/app_theme.dart';
import '../../core/widgets/hutube_widgets.dart';
import '../../channel/services/channel_service.dart';
import '../playlists/playlist_service.dart';
import '../playlists/save_playlist_sheet.dart';
import 'content_models.dart';
import 'content_service.dart';
import 'local_download_manager.dart';
import 'download_preferences.dart';
import 'media_entitlements.dart';
import 'media_feature_gate.dart';
import 'playback_session.dart';
import 'video_card.dart';
import 'widgets/playlist_queue_sheet.dart';
import 'widgets/comment_sheets.dart';
import 'widgets/video_description.dart';
import 'widgets/rating_sheet.dart';
import 'widgets/autoplay_next_notice.dart';
import '../moderation/report_dialog.dart';

class WatchScreen extends StatefulWidget {
  const WatchScreen({
    super.key,
    required this.auth,
    required this.playback,
    required this.videoId,
    this.playlistId,
    this.playlistIndex,
    this.playlistShuffle = false,
    this.restartFromBeginning = false,
  });
  final AuthController auth;
  final PlaybackSession playback;
  final String videoId;
  final String? playlistId;
  final int? playlistIndex;
  final bool playlistShuffle;
  final bool restartFromBeginning;

  @override
  State<WatchScreen> createState() => _WatchScreenState();
}

class _WatchScreenState extends State<WatchScreen> with WidgetsBindingObserver {
  late final ContentService _content;
  final _comment = TextEditingController();
  final _commentFocus = FocusNode();
  final _commentSheetController = DraggableScrollableController();
  final _prefs = const AppPreferencesStore();
  Timer? _metricsDebounce;
  Timer? _autoplayTimer;
  VideoCard? _upNext;
  int _autoplayIntentGeneration = 0;
  VideoDetail? _video;
  List<Rendition> _renditions = const [];
  MediaEntitlements _entitlements = const MediaEntitlements.none();
  List<CommentItem> _comments = [];
  final _commentChanges = ValueNotifier<int>(0);
  int _commentPage = 1;
  int _commentTotal = 0;
  int _commentRequest = 0;
  bool _commentsLoading = false;
  String? _commentsError;
  bool _commentRetryReset = false;
  List<VideoCard> _related = [];
  final Map<String, List<VideoCard>> _recommendationCache = {};
  String _recommendationFilter = 'all';
  int _recommendationGeneration = 0;
  bool _recommendationsLoading = false;
  String? _recommendationsError;
  int _generation = 0;
  bool _loading = true;
  bool _sendingComment = false;
  bool _isOwner = false;
  bool _isFullScreen = false;
  bool _changingFullScreen = false;
  bool _controlsLocked = false;
  bool _zoomToFill = false;
  bool _pipEnabled = false;
  bool _pipEntering = false;
  final _videoStageKey = GlobalKey();
  final _pipSurfaceKey = GlobalKey();
  Timer? _pipRestoreTimer;
  bool _restoringPip = false;
  bool _backgroundPlaybackEnabled = false;
  String _commentSort = 'top';
  String? _error;
  String? _actionMessage;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    AppPictureInPicture.active.addListener(_onPipModeChanged);
    if (Platform.isAndroid) AppPictureInPicture.bind(this, _prepareForPip);
    _commentFocus.addListener(() {
      if (_commentFocus.hasFocus && _commentSheetController.isAttached) {
        _commentSheetController.jumpTo(1);
      }
    });
    SystemChrome.setPreferredOrientations([
      DeviceOrientation.portraitUp,
      DeviceOrientation.landscapeLeft,
      DeviceOrientation.landscapeRight,
    ]);
    _content = ContentService(widget.auth);
    widget.playback.onVideoCompleted = _handleVideoCompleted;
    widget.playback.addListener(_onPlaybackChanged);
    _prefs.readZoomToFill().then((v) {
      if (mounted) setState(() => _zoomToFill = v);
    });
    _prefs.readPipEnabled().then((v) {
      if (mounted) setState(() => _pipEnabled = v);
    });
    _load();
  }

  @override
  void didChangeMetrics() {
    super.didChangeMetrics();
    if (!mounted ||
        _changingFullScreen ||
        _restoringPip ||
        AppPictureInPicture.active.value) {
      return;
    }
    _metricsDebounce?.cancel();
    _metricsDebounce = Timer(const Duration(milliseconds: 60), () {
      if (!mounted ||
          _changingFullScreen ||
          _restoringPip ||
          AppPictureInPicture.active.value) {
        return;
      }
      final view = View.of(context);
      if (view.physicalSize.width <= 0 || view.physicalSize.height <= 0) return;
      final isLandscapeNow = view.physicalSize.width > view.physicalSize.height;
      if (isLandscapeNow && !_isFullScreen) {
        _toggleFullScreen(toFullScreen: true, userInitiated: false);
      } else if (!isLandscapeNow && _isFullScreen) {
        _toggleFullScreen(toFullScreen: false, userInitiated: false);
      }
    });
  }

  @override
  void dispose() {
    _pipRestoreTimer?.cancel();
    AppPictureInPicture.active.removeListener(_onPipModeChanged);
    AppPictureInPicture.unbind(this);
    WidgetsBinding.instance.removeObserver(this);
    _metricsDebounce?.cancel();
    _autoplayTimer?.cancel();
    widget.playback.removeListener(_onPlaybackChanged);
    if (_isFullScreen) {
      _isFullScreen = false;
    }
    SystemChrome.setPreferredOrientations([DeviceOrientation.portraitUp]);
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    _comment.dispose();
    _commentFocus.dispose();
    _commentSheetController.dispose();
    _commentChanges.dispose();
    if (widget.playback.onVideoCompleted == _handleVideoCompleted) {
      widget.playback.onVideoCompleted = null;
    }
    if (widget.playback.videoId == widget.videoId && widget.playback.ready) {
      widget.playback.minimize();
    }
    super.dispose();
  }

  Future<void> _toggleFullScreen({
    bool? toFullScreen,
    bool userInitiated = true,
  }) async {
    final entering = toFullScreen ?? !_isFullScreen;
    if (_changingFullScreen || !mounted || _isFullScreen == entering) return;
    _changingFullScreen = true;
    setState(() => _isFullScreen = entering);
    try {
      if (userInitiated) {
        if (entering) {
          // Specifying landscape directly commands Android's WindowManager to rotate
          await SystemChrome.setPreferredOrientations([
            DeviceOrientation.landscapeRight,
            DeviceOrientation.landscapeLeft,
          ]);
          await SystemChrome.setEnabledSystemUIMode(
            SystemUiMode.immersiveSticky,
          );
        } else {
          await SystemChrome.setPreferredOrientations([
            DeviceOrientation.portraitUp,
          ]);
          await SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
          Future.delayed(const Duration(milliseconds: 300), () {
            if (mounted && !_isFullScreen) {
              SystemChrome.setPreferredOrientations([
                DeviceOrientation.portraitUp,
                DeviceOrientation.landscapeLeft,
                DeviceOrientation.landscapeRight,
              ]);
            }
          });
        }
      } else {
        // Physical sensor rotation: orientations are already aligned.
        // Update immersive UI mode without overriding device orientation.
        await SystemChrome.setEnabledSystemUIMode(
          entering ? SystemUiMode.immersiveSticky : SystemUiMode.edgeToEdge,
        );
      }
    } catch (_) {
      if (mounted) setState(() => _isFullScreen = !entering);
    } finally {
      if (mounted) {
        _changingFullScreen = false;
      }
    }
  }

  @override
  void didUpdateWidget(covariant WatchScreen oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.videoId != widget.videoId) _load();
  }

  Future<void> _loadRecommendations(String filter) async {
    final video = _video;
    if (video == null) return;
    if (_recommendationsLoading && _recommendationFilter == filter) return;
    final generation = ++_recommendationGeneration;
    final videoGeneration = _generation;
    setState(() {
      _recommendationFilter = filter;
      _recommendationsLoading = true;
      _recommendationsError = null;
      _related = [];
    });
    try {
      final cached = _recommendationCache[filter];
      if (cached != null) {
        setState(() => _related = cached);
        return;
      }
      final List<VideoCard> items;
      if (filter == 'related') {
        items = await _content.relatedVideos(video);
      } else {
        final result = filter == 'all'
            ? await _content.feed(
                explore: false,
                pageSize: 20,
                recommendationBudget: const Duration(milliseconds: 1200),
              )
            : await _content.searchVideos(
                query: '',
                pageSize: 20,
                channelId: video.channelId,
                sort: 'newest',
              );
        items = result.items;
      }
      if (!mounted ||
          generation != _recommendationGeneration ||
          videoGeneration != _generation) {
        return;
      }
      setState(
        () => _related = items
            .where((item) => item.id != video.id)
            .take(10)
            .toList(),
      );
      _recommendationCache[filter] = _related;
      if (widget.playlistId == null &&
          filter == 'all' &&
          widget.playback.videoId == video.id) {
        for (final item in _related) {
          widget.playback.addToQueue(item);
        }
      }
    } catch (_) {
      if (mounted &&
          generation == _recommendationGeneration &&
          videoGeneration == _generation) {
        setState(
          () => _recommendationsError = AppStrings.t('common.networkError'),
        );
      }
    } finally {
      if (mounted &&
          generation == _recommendationGeneration &&
          videoGeneration == _generation) {
        setState(() => _recommendationsLoading = false);
      }
    }
  }

  Future<void> _load() async {
    _autoplayTimer?.cancel();
    _upNext = null;
    ++_recommendationGeneration;
    _recommendationFilter = 'all';
    _recommendationsLoading = false;
    _recommendationsError = null;
    _recommendationCache.clear();
    final generation = ++_generation;
    final videoId = widget.videoId;
    setState(() {
      _loading = true;
      _error = null;
      _video = null;
      _isOwner = false;
      _comments = [];
      ++_commentRequest;
      _related = [];
      _actionMessage = null;
      _sendingComment = false;
      _backgroundPlaybackEnabled = false;
    });
    try {
      // Start independent work together. Owner metadata must not gate playback
      // or suggestions; recommendations can start as soon as detail arrives.
      final results = await Future.wait<Object>([
        _content.detail(videoId).then((detail) {
          if (mounted && generation == _generation) {
            _video = detail;
            unawaited(_loadRecommendations('all'));
            unawaited(_loadOwner(detail, generation));
          }
          return detail;
        }),
        _content.playback(videoId),
        MediaEntitlements.load(widget.auth),
        widget.auth.authenticated
            ? AccountService(widget.auth)
                  .getPreferences()
                  .then((value) => value.defaultPlaybackQuality)
                  .catchError((Object _) => 'auto')
            : _prefs.readPlaybackQuality().catchError((Object _) => 'auto'),
      ]);
      final detail = results[0] as VideoDetail;
      final playback = results[1] as Playback;
      final entitlements = results[2] as MediaEntitlements;
      final preferredQuality = results[3] as String;
      if (!mounted || generation != _generation) return;
      setState(() {
        _video = detail;
        _renditions = [...playback.renditions]
          ..sort((a, b) => a.height.compareTo(b.height));
        _entitlements = entitlements;
        _commentPage = 0;
        _commentTotal = detail.stats.comments;
        _commentsLoading = false;
        _commentsError = null;
        _loading = false;
      });

      unawaited(_loadCommentPage(reset: true));
      PlaylistDetail? playlist;
      if (widget.playlistId != null) {
        try {
          playlist = await PlaylistService(widget.auth).get(widget.playlistId!);
        } catch (_) {
          playlist = null;
        }
      }
      if (widget.playlistId != null && playlist != null) {
        final queue = playlist.items
            .where((item) => item.available)
            .map(
              (item) => VideoCard(
                id: item.videoId,
                channelId: '',
                channelName: '',
                channelHandle: '',
                title: item.title ?? AppStrings.t('playlists.videoUnavailable'),
                thumbnailUrl: item.thumbnailUrl,
                duration: item.duration,
                visibility: 'public',
                publishedAt: null,
                views: 0,
              ),
            )
            .toList();
        final requestedIndex = widget.playlistIndex;
        final initialIndex =
            requestedIndex != null &&
                requestedIndex >= 0 &&
                requestedIndex < queue.length &&
                queue[requestedIndex].id == detail.id
            ? requestedIndex
            : queue.indexWhere((item) => item.id == detail.id);
        if (queue.isNotEmpty) {
          widget.playback.setQueue(
            queue,
            initialIndex: initialIndex < 0 ? 0 : initialIndex,
          );
          widget.playback.isShuffle = widget.playlistShuffle;
        }
      } else if (!widget.playback.queue.any((item) => item.id == detail.id)) {
        final currentCard = VideoCard(
          id: detail.id,
          channelId: detail.channelId,
          channelName: detail.channelName,
          channelHandle: detail.channelHandle,
          title: detail.title,
          thumbnailUrl: detail.thumbnailUrl,
          duration: detail.duration,
          visibility: detail.visibility,
          publishedAt: detail.publishedAt,
          views: detail.views,
        );
        widget.playback.setQueue([currentCard, ..._related], initialIndex: 0);
      }
      final resumeAt = widget.restartFromBeginning
          ? 0
          : playback.resumeAt > 0
          ? playback.resumeAt
          : detail.viewerState.resumeAt;
      if (!mounted || generation != _generation) return;
      final cap = int.tryParse(preferredQuality.replaceAll('p', ''));
      final candidates = cap == null
          ? _renditions
          : _renditions.where((item) => item.height <= cap).toList();
      final source = candidates.isNotEmpty
          ? candidates.last.url
          : _renditions.isNotEmpty
          ? _renditions.first.url
          : detail.videoUrl;
      if (widget.playback.videoId == widget.videoId && widget.playback.ready) {
        widget.playback.restore();
      } else {
        if (source.isEmpty) throw StateError('No authorized rendition');
        await widget.playback.start(
          auth: widget.auth,
          video: detail,
          videoRenditions: _renditions,
          mediaEntitlements: entitlements,
          sourceUrl: source,
          resumeAt: resumeAt,
        );
        if (!mounted || generation != _generation) return;
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (!mounted || generation != _generation) return;
          unawaited(
            widget.playback
                .initialize(source, resumeAt: resumeAt, playWhenReady: true)
                .catchError((_) {
                  if (mounted && generation == _generation) {
                    setState(
                      () => _actionMessage = AppStrings.t('watch.playerError'),
                    );
                  }
                }),
          );
        });
      }
      if (mounted && generation == _generation) {
        setState(
          () => _backgroundPlaybackEnabled =
              widget.playback.backgroundPlaybackEnabled,
        );
      }
    } on ApiFailure catch (error) {
      if (mounted && generation == _generation) {
        setState(() {
          _error = AppStrings.apiError(error, fallback: 'watch.loadError');
          _loading = false;
        });
      }
    } catch (_) {
      if (mounted && generation == _generation) {
        setState(() {
          _error = AppStrings.t('watch.loadError');
          _loading = false;
        });
      }
    }
  }

  Future<void> _loadOwner(VideoDetail detail, int generation) async {
    if (!widget.auth.authenticated) return;
    final session = widget.auth.sessionGeneration;
    try {
      final channel = await ChannelService(widget.auth).getMyChannel();
      if (!mounted ||
          generation != _generation ||
          session != widget.auth.sessionGeneration) {
        return;
      }
      setState(
        () => _isOwner = channel != null && channel.id == detail.channelId,
      );
    } catch (_) {
      // A failed ownership lookup never grants creator controls.
    }
  }

  void _handleVideoCompleted() {
    if (!mounted ||
        _upNext != null ||
        !widget.playback.autoplayNext ||
        widget.playback.videoId != widget.videoId ||
        ModalRoute.of(context)?.isCurrent != true) {
      return;
    }
    final next = widget.playback.nextVideoCard;
    if (next != null && next.id != widget.videoId) {
      final generation = _generation;
      final session = widget.auth.sessionGeneration;
      _autoplayIntentGeneration = widget.playback.playbackIntentGeneration;
      setState(() => _upNext = next);
      _autoplayTimer = Timer(const Duration(seconds: 2), () {
        _cancelAutoplay();
        if (mounted &&
            generation == _generation &&
            session == widget.auth.sessionGeneration &&
            widget.playback.videoId == widget.videoId &&
            widget.playback.autoplayNext &&
            ModalRoute.of(context)?.isCurrent == true &&
            widget.playback.queue.any((item) => item.id == next.id)) {
          final playlist = widget.playlistId;
          final nextIndex = widget.playback.queue.indexWhere(
            (item) => item.id == next.id,
          );
          context.pushReplacement(
            Uri(
              path: '/watch/${next.id}',
              queryParameters: {
                'autoplay': 'true',
                if (playlist != null) ...{
                  'playlist': playlist,
                  'index': '$nextIndex',
                  if (widget.playback.isShuffle) 'shuffle': 'true',
                },
              },
            ).toString(),
          );
        }
      });
    }
  }

  void _cancelAutoplay() {
    _autoplayTimer?.cancel();
    _autoplayTimer = null;
    if (mounted && _upNext != null) setState(() => _upNext = null);
  }

  void _onPlaybackChanged() {
    if (mounted &&
        _backgroundPlaybackEnabled !=
            widget.playback.backgroundPlaybackEnabled) {
      setState(
        () => _backgroundPlaybackEnabled =
            widget.playback.backgroundPlaybackEnabled,
      );
    }
    if (_upNext == null) return;
    final playback = widget.playback;
    if (!playback.autoplayNext ||
        playback.videoId != widget.videoId ||
        !playback.queue.any((item) => item.id == _upNext!.id) ||
        playback.playbackIntentGeneration != _autoplayIntentGeneration) {
      _cancelAutoplay();
    }
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state != AppLifecycleState.resumed) _cancelAutoplay();
  }

  void _openQueueSheet() {
    HapticFeedback.lightImpact();
    PlaylistQueueSheet.show(
      context,
      playback: widget.playback,
      onSelectVideo: (video) {
        if (video.id != widget.videoId) {
          context.pushReplacement('/watch/${video.id}');
        }
      },
    );
  }

  Future<bool> _prepareForPip() async {
    if (!mounted ||
        ModalRoute.of(context)?.isCurrent != true ||
        !widget.playback.entitlements.pictureInPicture ||
        !widget.auth.authenticated ||
        !widget.playback.ready ||
        widget.playback.videoId != widget.videoId) {
      return false;
    }
    // Keep the current player mounted, and show only its surface inside PiP.
    _pipRestoreTimer?.cancel();
    _restoringPip = true;
    AppPictureInPicture.active.value = true;
    if (!await AppPictureInPicture.prepareSurface(_pipSurfaceKey)) {
      AppPictureInPicture.active.value = false;
      return false;
    }
    return mounted && widget.playback.videoId == widget.videoId;
  }

  void _onPipModeChanged() {
    if (!mounted) return;
    setState(() {});
    if (!AppPictureInPicture.active.value) {
      _restoringPip = true;
      _pipRestoreTimer?.cancel();
      _pipRestoreTimer = Timer(const Duration(milliseconds: 500), () {
        if (mounted) _restoringPip = false;
      });
    }
  }

  Future<void> _enterPictureInPicture() async {
    if (!_entitlements.pictureInPicture) {
      if (!await requireMediaFeature(
            context,
            widget.auth,
            PlanMediaFeature.pip,
          ) ||
          !mounted) {
        return;
      }
      _entitlements = MediaEntitlements(
        pictureInPicture: true,
        backgroundPlayback: _entitlements.backgroundPlayback,
      );
      widget.playback.updateMediaEntitlements(_entitlements);
    }
    final player = widget.playback.player;
    if (_pipEntering ||
        player == null ||
        !widget.playback.ready ||
        !_entitlements.pictureInPicture) {
      return;
    }
    _pipEntering = true;
    final wasFullScreen = _isFullScreen;
    var started = false;
    try {
      if (Platform.isAndroid) {
        if (await AppPictureInPicture.available() && await _prepareForPip()) {
          started = await AppPictureInPicture.enter();
        }
      } else {
        started = await player.enterPictureInPicture();
      }
    } catch (_) {
      started = false;
    } finally {
      _pipEntering = false;
    }
    if (!mounted) return;
    if (!started) {
      AppPictureInPicture.active.value = false;
      if (!wasFullScreen && _isFullScreen) {
        await _toggleFullScreen(toFullScreen: false, userInitiated: false);
      }
      if (mounted) {
        setState(() => _actionMessage = AppStrings.t('watch.pipUnsupported'));
      }
    }
  }

  Future<bool> _requireAuth({required String action}) async {
    if (widget.auth.authenticated) return true;
    final proceed = await showModalBottomSheet<bool>(
      context: context,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) => SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(24, 20, 24, 24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Container(
                width: 40,
                height: 4,
                margin: const EdgeInsets.only(bottom: 20),
                decoration: BoxDecoration(
                  color: Colors.grey.withValues(alpha: 0.4),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
              const Icon(
                Icons.account_circle_outlined,
                size: 52,
                color: AppColors.primaryPink,
              ),
              const SizedBox(height: 14),
              Text(
                'Bạn muốn $action?',
                style: const TextStyle(
                  fontSize: 18,
                  fontWeight: FontWeight.bold,
                ),
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 8),
              Text(
                'Đăng nhập để tương tác với video, đăng ký kênh và lưu nội dung yêu thích của bạn.',
                textAlign: TextAlign.center,
                style: TextStyle(
                  fontSize: 14,
                  color: Theme.of(ctx).colorScheme.onSurfaceVariant,
                  height: 1.4,
                ),
              ),
              const SizedBox(height: 24),
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton(
                      onPressed: () => Navigator.pop(ctx, false),
                      child: const Text('Để sau'),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: FilledButton(
                      style: FilledButton.styleFrom(
                        backgroundColor: AppColors.primaryPink,
                      ),
                      onPressed: () => Navigator.pop(ctx, true),
                      child: const Text('Đăng nhập'),
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
    if (proceed == true && mounted) {
      context.push('/auth');
    }
    return false;
  }

  Future<void> _react(String type) async {
    final generation = _generation;
    HapticFeedback.lightImpact();
    if (!await _requireAuth(action: 'bày tỏ cảm xúc')) {
      if (mounted && !widget.auth.authenticated) {
        setState(() => _actionMessage = AppStrings.t('watch.loginInteract'));
      }
      return;
    }
    if (!mounted || generation != _generation) return;
    final video = _video;
    if (video == null) return;
    try {
      final result = await _content.react(
        video.id,
        video.viewerState.reaction == type ? null : type,
      );
      if (!mounted || generation != _generation) return;
      setState(() {
        _video = VideoDetail(
          id: video.id,
          channelId: video.channelId,
          channelName: video.channelName,
          channelHandle: video.channelHandle,
          title: video.title,
          thumbnailUrl: video.thumbnailUrl,
          duration: video.duration,
          visibility: video.visibility,
          publishedAt: video.publishedAt,
          views: video.views,
          description: video.description,
          videoUrl: video.videoUrl,
          tags: video.tags,
          chapters: video.chapters,
          stats: VideoStats(
            views: video.stats.views,
            likes: asInt(result['likes']),
            dislikes: asInt(result['dislikes']),
            comments: video.stats.comments,
            averageRating: video.stats.averageRating,
            ratingCount: video.stats.ratingCount,
            watchSeconds: video.stats.watchSeconds,
          ),
          viewerState: ViewerState(
            reaction: result['myReaction'] as String?,
            rating: video.viewerState.rating,
            resumeAt: video.viewerState.resumeAt,
          ),
          moderationStatus: video.moderationStatus,
          processingStatus: video.processingStatus,
          promotionEnabled: video.promotionEnabled,
          videoCards: video.videoCards,
        );
      });
    } on ApiFailure catch (error) {
      if (mounted && generation == _generation) {
        setState(() => _actionMessage = AppStrings.apiError(error));
      }
    }
  }

  Future<void> _rate() async {
    final generation = _generation;
    final videoId = widget.videoId;
    HapticFeedback.lightImpact();
    if (!await _requireAuth(action: 'đánh giá video này')) {
      if (mounted && !widget.auth.authenticated) {
        setState(() => _actionMessage = AppStrings.t('watch.loginRate'));
      }
      return;
    }
    if (!mounted || generation != _generation) return;
    final originalVideo = _video;
    if (originalVideo == null) return;
    final currentRating = originalVideo.viewerState.rating;
    final score = await showModalBottomSheet<int>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (_) => RatingSheet(currentRating: currentRating),
    );
    if (score == null || generation != _generation) return;
    try {
      final result = await _content.rate(videoId, score == 0 ? null : score);
      final old = originalVideo;
      if (!mounted || generation != _generation) return;
      setState(() {
        _video = VideoDetail(
          id: old.id,
          channelId: old.channelId,
          channelName: old.channelName,
          channelHandle: old.channelHandle,
          title: old.title,
          thumbnailUrl: old.thumbnailUrl,
          duration: old.duration,
          visibility: old.visibility,
          publishedAt: old.publishedAt,
          views: old.views,
          description: old.description,
          videoUrl: old.videoUrl,
          tags: old.tags,
          chapters: old.chapters,
          stats: VideoStats(
            views: old.stats.views,
            likes: old.stats.likes,
            dislikes: old.stats.dislikes,
            comments: old.stats.comments,
            averageRating: (result['average'] as num?)?.toDouble(),
            ratingCount: asInt(result['count']),
            watchSeconds: old.stats.watchSeconds,
          ),
          viewerState: ViewerState(
            reaction: old.viewerState.reaction,
            rating: result['myRating'] as int?,
            resumeAt: old.viewerState.resumeAt,
          ),
          moderationStatus: old.moderationStatus,
          processingStatus: old.processingStatus,
          promotionEnabled: old.promotionEnabled,
          videoCards: old.videoCards,
        );
        _actionMessage = score == 0
            ? 'Đã xóa đánh giá của bạn'
            : AppStrings.t('watch.ratingSaved');
      });
    } on ApiFailure catch (error) {
      if (mounted && generation == _generation) {
        setState(() => _actionMessage = AppStrings.apiError(error));
      }
    }
  }

  String _formatDuration(Duration value) {
    final seconds = value.inSeconds;
    final hours = seconds ~/ 3600;
    final minutes = (seconds % 3600) ~/ 60;
    final rest = seconds % 60;
    if (hours > 0) {
      return '$hours:${minutes.toString().padLeft(2, '0')}:${rest.toString().padLeft(2, '0')}';
    }
    return '$minutes:${rest.toString().padLeft(2, '0')}';
  }

  Future<void> _share() async {
    HapticFeedback.lightImpact();
    final video = _video;
    if (video == null) return;
    if (widget.auth.authenticated) unawaited(_content.share(video.id));
    final currentPos = widget.playback.position.inSeconds;
    if (currentPos > 5) {
      final withTimestamp = await showModalBottomSheet<bool>(
        context: context,
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
        ),
        builder: (ctx) => SafeArea(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(20, 16, 20, 20),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  'Chia sẻ: ${video.title}',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 16,
                    fontWeight: FontWeight.bold,
                  ),
                ),
                const SizedBox(height: 16),
                ListTile(
                  leading: const Icon(
                    Icons.timer_outlined,
                    color: AppColors.primaryPink,
                  ),
                  title: Text(
                    'Bắt đầu tại ${_formatDuration(widget.playback.position)}',
                  ),
                  subtitle: const Text(
                    'Người xem sẽ mở video ngay tại mốc thời gian này',
                  ),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(12),
                  ),
                  tileColor: Theme.of(ctx).colorScheme.surfaceContainerHighest,
                  onTap: () => Navigator.pop(ctx, true),
                ),
                const SizedBox(height: 8),
                ListTile(
                  leading: const Icon(Icons.play_circle_outline_rounded),
                  title: const Text('Bắt đầu từ đầu video'),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(12),
                  ),
                  tileColor: Theme.of(ctx).colorScheme.surfaceContainerHighest,
                  onTap: () => Navigator.pop(ctx, false),
                ),
              ],
            ),
          ),
        ),
      );
      if (withTimestamp == null) return;
      final url = withTimestamp
          ? 'https://hutube.app/watch/${video.id}?t=$currentPos'
          : 'https://hutube.app/watch/${video.id}';
      await Share.share('Xem "${video.title}" trên Dalitu:\n$url');
    } else {
      await Share.share(
        AppStrings.format('watch.shareText', {
          'title': video.title,
          'id': video.id,
        }),
      );
    }
  }

  Future<void> _download() async {
    final generation = _generation;
    final videoId = widget.videoId;
    HapticFeedback.lightImpact();
    if (!await _requireAuth(action: 'tải xuống video này')) {
      if (mounted && !widget.auth.authenticated) {
        setState(() => _actionMessage = AppStrings.t('watch.loginDownload'));
      }
      return;
    }
    if (!mounted || generation != _generation) return;
    final session = widget.auth.sessionGeneration;
    bool current() =>
        mounted &&
        generation == _generation &&
        session == widget.auth.sessionGeneration;
    try {
      final options = await _content.downloadOptions(videoId);
      final preference = await _prefs.readDownloadQuality();
      if (!mounted || !current()) return;
      final chosen =
          selectDownloadRendition(options, preference) ??
          await showModalBottomSheet<Rendition>(
            context: context,
            isScrollControlled: true,
            builder: (context) => ScrollableSheet(
              child: SafeArea(
                child: ListView(
                  shrinkWrap: true,
                  children: [
                    ListTile(
                      title: Text(AppStrings.t('watch.downloadQuality')),
                    ),
                    for (final item in options)
                      ListTile(
                        title: Text(item.quality),
                        subtitle: Text('${item.width}×${item.height}'),
                        onTap: () => Navigator.pop(context, item),
                      ),
                  ],
                ),
              ),
            ),
          );
      if (chosen == null || !current()) return;
      final record = await _content.createDownload(videoId, chosen.quality);
      if (!mounted ||
          generation != _generation ||
          session != widget.auth.sessionGeneration) {
        return;
      }
      await LocalDownloadManager.instance.enqueue(
        id: '${record['videoDownloadId'] ?? ''}',
        videoId: videoId,
        title:
            '${record['title'] ?? _video?.title ?? AppStrings.t('common.download')}',
        quality: '${record['quality'] ?? chosen.quality}',
        url: '${record['fileUrl'] ?? chosen.url}',
        fileSize: asInt(record['fileSize']),
        thumbnailUrl: _video?.thumbnailUrl,
        channelName: _video?.channelName ?? '',
        duration: _video?.duration ?? 0,
      );
      if (current()) {
        setState(() => _actionMessage = AppStrings.t('watch.downloadAdded'));
      }
    } on ApiFailure catch (error) {
      if (current()) {
        if (error.code == 'DOWNLOAD_NOT_INCLUDED') {
          if (!mounted) return;
          await showMediaPlanRequirement(
            context,
            widget.auth,
            PlanMediaFeature.download,
          );
        } else {
          setState(() => _actionMessage = AppStrings.apiError(error));
        }
      }
    } catch (_) {
      if (current()) {
        setState(() => _actionMessage = AppStrings.t('downloads.error'));
      }
    }
  }

  Future<void> _saveToPlaylist() async {
    final generation = _generation;
    final videoId = widget.videoId;
    HapticFeedback.lightImpact();
    if (!await _requireAuth(action: 'lưu video vào danh sách phát')) {
      if (mounted && !widget.auth.authenticated) {
        setState(() => _actionMessage = AppStrings.t('watch.loginPlaylist'));
      }
      return;
    }
    if (!mounted || generation != _generation) return;
    final session = widget.auth.sessionGeneration;

    try {
      final saved = await showSavePlaylistSheet(
        context,
        auth: widget.auth,
        videoId: videoId,
      );
      if (saved &&
          mounted &&
          generation == _generation &&
          session == widget.auth.sessionGeneration) {
        setState(() => _actionMessage = AppStrings.t('watch.savedToPlaylist'));
      }
    } on ApiFailure catch (error) {
      if (mounted && generation == _generation) {
        setState(() => _actionMessage = AppStrings.apiError(error));
      }
    }
  }

  Future<T?> _showAdaptivePlaybackModal<T>({
    required BuildContext context,
    required Widget Function(BuildContext) builder,
  }) {
    final size = MediaQuery.sizeOf(context);
    final isLandscape = _isFullScreen || size.width > size.height;

    if (isLandscape) {
      return showDialog<T>(
        context: context,
        useRootNavigator: true,
        barrierColor: Colors.black54,
        builder: (dialogCtx) => Dialog(
          backgroundColor: const Color(0xff212121),
          elevation: 10,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(18),
          ),
          insetPadding: const EdgeInsets.symmetric(
            horizontal: 24,
            vertical: 12,
          ),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 360, maxHeight: 310),
            child: ClipRRect(
              borderRadius: BorderRadius.circular(18),
              child: Material(
                color: const Color(0xff212121),
                child: builder(dialogCtx),
              ),
            ),
          ),
        ),
      );
    } else {
      return showModalBottomSheet<T>(
        context: context,
        useRootNavigator: true,
        backgroundColor: const Color(0xff212121),
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
        ),
        builder: (sheetCtx) => SafeArea(
          child: Material(
            color: const Color(0xff212121),
            child: builder(sheetCtx),
          ),
        ),
      );
    }
  }

  Future<void> _showPlaybackSettings() async {
    final player = widget.playback.player;
    if (player == null || !widget.playback.ready) return;
    final quality = widget.playback.selectedRendition;
    final qualityText = quality != null
        ? 'Tự động (${quality.quality})'
        : 'Tự động';
    final speedText =
        '${player.speed.toStringAsFixed(player.speed == 1 ? 0 : 2)}x';

    await _showAdaptivePlaybackModal<void>(
      context: context,
      builder: (modalContext) => ListView(
        shrinkWrap: true,
        padding: const EdgeInsets.only(bottom: 8),
        children: [
          Center(
            child: Container(
              width: 36,
              height: 4,
              margin: const EdgeInsets.only(top: 10, bottom: 6),
              decoration: BoxDecoration(
                color: Colors.white24,
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          _PlaybackSettingTile(
            dense: true,
            icon: Icons.tune_rounded,
            title: 'Chất lượng',
            value: qualityText,
            onTap: () {
              Navigator.pop(modalContext);
              _showQualityChoices();
            },
          ),
          _PlaybackSettingTile(
            dense: true,
            icon: Icons.slow_motion_video_rounded,
            title: 'Tốc độ phát',
            value: speedText,
            onTap: () {
              Navigator.pop(modalContext);
              _showSpeedChoices();
            },
          ),
          _PlaybackSettingTile(
            dense: true,
            icon: Icons.closed_caption_rounded,
            title: 'Phụ đề',
            onTap: () {
              Navigator.pop(modalContext);
              _showSubtitleChoices();
            },
          ),
          _PlaybackSettingTile(
            dense: true,
            icon: Icons.lock_outline_rounded,
            title: 'Khóa màn hình',
            onTap: () {
              Navigator.pop(modalContext);
              widget.playback.player?.lockOverlay();
            },
          ),
          _PlaybackSettingTile(
            dense: true,
            icon: Icons.headphones_rounded,
            title: AppStrings.t('watch.background'),
            onTap: () {
              Navigator.pop(modalContext);
              unawaited(_toggleBackgroundPlayback());
            },
          ),
          _PlaybackSettingTile(
            dense: true,
            icon: Icons.settings_outlined,
            title: 'Tùy chọn khác',
            trailing: const Icon(
              Icons.chevron_right_rounded,
              color: Colors.white70,
              size: 20,
            ),
            onTap: () {
              Navigator.pop(modalContext);
              _showMoreOptions();
            },
          ),
        ],
      ),
    );
  }

  Future<void> _showQualityChoices() async {
    await _showPlaybackChoiceSheet(
      title: 'Chất lượng',
      choices: _renditions.isEmpty
          ? const [(value: null, label: 'Chất lượng gốc', detail: '')]
          : [
              for (final rendition in _renditions)
                (
                  value: rendition,
                  label: rendition.quality,
                  detail: '${rendition.width} × ${rendition.height}',
                ),
            ],
      isSelected: (value) =>
          value is Rendition &&
          widget.playback.selectedRendition?.url == value.url,
      onSelected: (value) async {
        if (value is! Rendition) return;
        try {
          await widget.playback.changeQuality(value);
        } catch (_) {
          if (mounted) {
            setState(() => _actionMessage = AppStrings.t('watch.playerError'));
          }
        }
      },
    );
  }

  Future<void> _showSpeedChoices() async {
    final player = widget.playback.player;
    if (player == null) return;
    const speeds = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0];
    await _showPlaybackChoiceSheet<double>(
      title: 'Tốc độ phát',
      choices: [
        for (final speed in speeds)
          (
            value: speed,
            label: speed == 1 ? 'Bình thường' : '${speed}x',
            detail: '',
          ),
      ],
      isSelected: (value) => (player.speed - value).abs() < .01,
      onSelected: widget.playback.setSpeed,
    );
  }

  Future<void> _showSubtitleChoices() async {
    final player = widget.playback.player;
    if (player == null) return;
    List<NativeVideoPlayerSubtitleTrack> tracks = const [];
    try {
      tracks = await player.getAvailableSubtitleTracks();
    } catch (_) {}
    await _showPlaybackChoiceSheet<NativeVideoPlayerSubtitleTrack>(
      title: 'Phụ đề',
      choices: [
        (value: NativeVideoPlayerSubtitleTrack.off(), label: 'Tắt', detail: ''),
        for (final track in tracks)
          (value: track, label: track.displayName, detail: track.language),
      ],
      isSelected: (track) => track.isOff
          ? !tracks.any((item) => item.isSelected)
          : tracks.any(
              (item) =>
                  item.index == track.index &&
                  item.source == track.source &&
                  item.isSelected,
            ),
      onSelected: player.setSubtitleTrack,
    );
  }

  Future<void> _toggleBackgroundPlayback() async {
    if (!_entitlements.backgroundPlayback) {
      if (!await requireMediaFeature(
            context,
            widget.auth,
            PlanMediaFeature.background,
          ) ||
          !mounted) {
        return;
      }
      _entitlements = MediaEntitlements(
        pictureInPicture: _entitlements.pictureInPicture,
        backgroundPlayback: true,
      );
      widget.playback.updateMediaEntitlements(_entitlements);
    }
    final enabled = !widget.playback.backgroundPlaybackRequested;
    final changed = await widget.playback.setBackgroundPlaybackEnabled(enabled);
    if (!mounted) return;
    setState(() {
      if (changed) _backgroundPlaybackEnabled = enabled;
      _actionMessage = changed
          ? AppStrings.t(
              enabled ? 'watch.backgroundEnabled' : 'watch.backgroundDisabled',
            )
          : AppStrings.t(
              widget.playback.waitingForExternalAudio
                  ? 'watch.backgroundNeedsAudio'
                  : 'watch.backgroundUnavailable',
            );
    });
  }

  Future<void> _showMoreOptions() async {
    await _showAdaptivePlaybackModal<void>(
      context: context,
      builder: (modalContext) => StatefulBuilder(
        builder: (context, setSheetState) => ListView(
          shrinkWrap: true,
          padding: const EdgeInsets.only(bottom: 8),
          children: [
            Center(
              child: Container(
                width: 36,
                height: 4,
                margin: const EdgeInsets.only(top: 10, bottom: 6),
                decoration: BoxDecoration(
                  color: Colors.white24,
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const Padding(
              padding: EdgeInsets.symmetric(horizontal: 16, vertical: 6),
              child: Text(
                'Tùy chọn phát',
                style: TextStyle(
                  color: Colors.white,
                  fontWeight: FontWeight.w800,
                  fontSize: 15,
                ),
              ),
            ),

            SwitchListTile(
              dense: true,
              visualDensity: const VisualDensity(horizontal: 0, vertical: -2),
              contentPadding: const EdgeInsets.symmetric(horizontal: 16),
              secondary: const Icon(
                Icons.picture_in_picture_alt_rounded,
                color: Colors.white70,
                size: 20,
              ),
              subtitle: Text(
                AppStrings.t('media.pipAutomatic'),
                style: const TextStyle(color: Colors.white70),
              ),
              title: Text(
                AppStrings.t('playback.pip'),
                style: TextStyle(color: Colors.white, fontSize: 14),
              ),
              value: _pipEnabled,
              onChanged: (value) {
                unawaited(() async {
                  if (value &&
                      !await requireMediaFeature(
                        this.context,
                        widget.auth,
                        PlanMediaFeature.pip,
                      )) {
                    return;
                  }
                  if (!mounted || !context.mounted) return;
                  try {
                    if (value) {
                      _entitlements = MediaEntitlements(
                        pictureInPicture: true,
                        backgroundPlayback: _entitlements.backgroundPlayback,
                      );
                      widget.playback.updateMediaEntitlements(_entitlements);
                    }
                    await _prefs.writePipEnabled(value);
                    if (mounted && context.mounted) {
                      setSheetState(() => _pipEnabled = value);
                      setState(() => _pipEnabled = value);
                    }
                  } catch (_) {
                    if (mounted) {
                      ScaffoldMessenger.of(this.context).showSnackBar(
                        SnackBar(
                          content: Text(AppStrings.t('prefs.saveError')),
                        ),
                      );
                    }
                  }
                }());
              },
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _showPlaybackChoiceSheet<T>({
    required String title,
    required List<({T value, String label, String detail})> choices,
    required bool Function(T value) isSelected,
    required Future<void> Function(T value) onSelected,
  }) async {
    await _showAdaptivePlaybackModal<void>(
      context: context,
      builder: (modalContext) => ListView(
        shrinkWrap: true,
        padding: const EdgeInsets.only(bottom: 8),
        children: [
          Center(
            child: Container(
              width: 36,
              height: 4,
              margin: const EdgeInsets.only(top: 10, bottom: 6),
              decoration: BoxDecoration(
                color: Colors.white24,
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
            child: Text(
              title,
              style: const TextStyle(
                color: Colors.white,
                fontWeight: FontWeight.w800,
                fontSize: 15,
              ),
            ),
          ),
          for (final choice in choices)
            ListTile(
              dense: true,
              visualDensity: const VisualDensity(horizontal: 0, vertical: -2),
              contentPadding: const EdgeInsets.symmetric(horizontal: 16),
              title: Text(
                choice.label,
                style: const TextStyle(
                  color: Colors.white,
                  fontWeight: FontWeight.w500,
                  fontSize: 14,
                ),
              ),
              subtitle: choice.detail.isEmpty
                  ? null
                  : Text(
                      choice.detail,
                      style: const TextStyle(
                        color: Colors.white60,
                        fontSize: 12,
                      ),
                    ),
              trailing: isSelected(choice.value)
                  ? const Icon(
                      Icons.check_rounded,
                      color: AppColors.primaryPink,
                      size: 20,
                    )
                  : null,
              onTap: () {
                Navigator.pop(modalContext);
                unawaited(onSelected(choice.value));
              },
            ),
        ],
      ),
    );
  }

  void _onCommentUpdated(CommentItem updated) {
    _updateComments(() {
      if (updated.status == 'deleted') {
        _comments.removeWhere((item) => item.id == updated.id);
        if (_commentTotal > 0) _commentTotal--;
      } else {
        _comments = _comments
            .map((item) => item.id == updated.id ? updated : item)
            .toList();
      }
    });
  }

  void _updateComments(VoidCallback update) {
    if (!mounted) return;
    setState(update);
    _commentChanges.value++;
  }

  Future<void> _loadCommentPage({bool reset = false}) async {
    if (_commentsLoading && !reset) return;
    final request = ++_commentRequest;
    final generation = _generation;
    final page = reset ? 1 : _commentPage + 1;
    _updateComments(() {
      _commentsLoading = true;
      _commentsError = null;
    });
    try {
      final result = await _content.comments(
        widget.videoId,
        page: page,
        sort: _commentSort,
      );
      if (!mounted || generation != _generation || request != _commentRequest) {
        return;
      }
      _updateComments(() {
        _comments = reset
            ? result.items
            : [
                ..._comments,
                ...result.items.where(
                  (item) => !_comments.any((old) => old.id == item.id),
                ),
              ];
        _commentPage = result.page;
        _commentTotal = result.total;
        _commentRetryReset = false;
        _commentsLoading = false;
      });
    } catch (error) {
      if (!mounted || generation != _generation || request != _commentRequest) {
        return;
      }
      _updateComments(() {
        _commentsLoading = false;
        _commentRetryReset = reset;
        _commentsError = error is ApiFailure && error.status >= 500
            ? AppStrings.t('watch.commentsServerError')
            : error is ApiFailure
            ? AppStrings.apiError(error)
            : AppStrings.t('watch.commentsLoadError');
      });
    }
  }

  Widget _buildCommentPreview() {
    final preview = _comments.firstOrNull;
    return Semantics(
      button: true,
      child: Material(
        color: AppColors.surfaceAltFor(context),
        borderRadius: BorderRadius.circular(16),
        child: InkWell(
          key: const ValueKey('watch-comments-preview'),
          borderRadius: BorderRadius.circular(16),
          onTap: _openComments,
          child: Padding(
            padding: const EdgeInsets.all(14),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        AppStrings.format('watch.comments', {
                          'count': AppStrings.number(_commentTotal),
                        }),
                        style: const TextStyle(fontWeight: FontWeight.w800),
                      ),
                    ),
                    const Icon(Icons.unfold_more_rounded, size: 20),
                  ],
                ),
                const SizedBox(height: 8),
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    HuTubeAvatar(
                      label: preview?.displayName ?? 'H',
                      radius: 14,
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Text(
                        preview?.content ?? AppStrings.t('watch.commentHint'),
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: Theme.of(context).textTheme.bodyMedium,
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Future<void> _openComments() async {
    final viewport = MediaQuery.sizeOf(context);
    final compact = viewport.height < 500;
    final top = MediaQuery.paddingOf(context).top;
    final playerHeight = (viewport.width * 9 / 16).clamp(
      0.0,
      (viewport.height - top) * .45,
    );
    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      barrierColor: Colors.transparent,
      backgroundColor: Colors.transparent,
      elevation: 0,
      showDragHandle: false,
      enableDrag: false,
      constraints: BoxConstraints(maxHeight: viewport.height - top),
      builder: (sheetContext) => DraggableScrollableSheet(
        controller: _commentSheetController,
        expand: false,
        initialChildSize:
            ((viewport.height - top - playerHeight) / (viewport.height - top))
                .clamp(.45, 1.0),
        minChildSize: .4,
        maxChildSize: 1,
        builder: (context, scrollController) => AnimatedBuilder(
          animation: Listenable.merge([_commentChanges, widget.auth]),
          builder: (context, _) => Material(
            key: const ValueKey('watch-comments-panel'),
            color: Theme.of(context).colorScheme.surface,
            borderRadius: const BorderRadius.vertical(top: Radius.circular(20)),
            clipBehavior: Clip.antiAlias,
            child: SafeArea(
              top: false,
              child: Padding(
                padding: EdgeInsets.only(
                  bottom: MediaQuery.viewInsetsOf(context).bottom,
                ),
                child: Column(
                  children: [
                    Padding(
                      padding: EdgeInsets.symmetric(vertical: compact ? 4 : 8),
                      child: Center(
                        child: Container(
                          width: 36,
                          height: 4,
                          decoration: BoxDecoration(
                            color: Theme.of(context)
                                .colorScheme
                                .onSurfaceVariant
                                .withValues(alpha: .4),
                            borderRadius: BorderRadius.circular(4),
                          ),
                        ),
                      ),
                    ),
                    Padding(
                      padding: const EdgeInsets.fromLTRB(16, 4, 4, 0),
                      child: Row(
                        children: [
                          Expanded(
                            child: Text(
                              AppStrings.format('watch.comments', {
                                'count': AppStrings.number(_commentTotal),
                              }),
                              style: Theme.of(context).textTheme.titleMedium
                                  ?.copyWith(fontWeight: FontWeight.w800),
                            ),
                          ),
                          IconButton(
                            key: const ValueKey('watch-comments-close'),
                            tooltip: AppStrings.t('common.close'),
                            onPressed: () => Navigator.pop(sheetContext),
                            icon: const Icon(Icons.close),
                          ),
                        ],
                      ),
                    ),
                    Expanded(
                      child: ListView(
                        key: const ValueKey('watch-comments-list'),
                        controller: scrollController,
                        padding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
                        children: [
                          Wrap(
                            spacing: 8,
                            children: [
                              for (final sort in ['top', 'newest'])
                                ChoiceChip(
                                  label: Text(
                                    AppStrings.t(
                                      sort == 'top'
                                          ? 'creator.commentSortMostLiked'
                                          : 'creator.commentSortNewest',
                                    ),
                                  ),
                                  selected: _commentSort == sort,
                                  onSelected: (_) {
                                    if (_commentSort == sort) return;
                                    _updateComments(() => _commentSort = sort);
                                    _loadCommentPage(reset: true);
                                  },
                                ),
                            ],
                          ),
                          if (_commentsError != null)
                            Text(
                              _commentsError!,
                              style: TextStyle(
                                color: Theme.of(context).colorScheme.error,
                              ),
                            ),
                          if (_commentsLoading) const LinearProgressIndicator(),
                          for (final item in _comments)
                            _CommentTile(
                              key: ValueKey(item.id),
                              item: item,
                              content: _content,
                              signedIn: widget.auth.authenticated,
                              auth: widget.auth,
                              channelName: _video?.channelName ?? '',
                              isOwner: _isOwner,
                              isPinned: item.isPinned,
                              isHearted: item.hasCreatorHeart,
                              onTogglePin: () => _togglePinComment(item),
                              onToggleHeart: () => _toggleHeartComment(item),
                              onToggleHide: () => _toggleHideComment(item),
                              onUpdated: _onCommentUpdated,
                              onSignIn: () {
                                Navigator.pop(sheetContext);
                                _requireAuth(
                                  action: AppStrings.t('watch.commentHint'),
                                );
                              },
                            ),
                          if (_comments.isEmpty &&
                              !_commentsLoading &&
                              _commentsError == null)
                            Padding(
                              padding: const EdgeInsets.symmetric(vertical: 24),
                              child: Text(
                                AppStrings.t('watch.noComments'),
                                textAlign: TextAlign.center,
                              ),
                            ),
                          if (_comments.length < _commentTotal ||
                              _commentsError != null)
                            TextButton(
                              key: const ValueKey('watch-comments-retry-more'),
                              onPressed: _commentsLoading
                                  ? null
                                  : () => _loadCommentPage(
                                      reset:
                                          _commentRetryReset ||
                                          _comments.isEmpty,
                                    ),
                              child: Text(
                                AppStrings.t(
                                  _commentsError != null
                                      ? 'common.retry'
                                      : 'notifications.loadMore',
                                ),
                              ),
                            ),
                        ],
                      ),
                    ),
                    Container(
                      padding: EdgeInsets.fromLTRB(
                        16,
                        compact ? 6 : 12,
                        16,
                        compact ? 6 : 12,
                      ),
                      decoration: BoxDecoration(
                        color: Theme.of(context).colorScheme.surface,
                        border: Border(
                          top: BorderSide(
                            color: Theme.of(context).dividerColor,
                          ),
                        ),
                      ),
                      child: widget.auth.authenticated
                          ? Row(
                              crossAxisAlignment: CrossAxisAlignment.end,
                              children: [
                                Expanded(
                                  child: TextField(
                                    key: const ValueKey('watch-comment-input'),
                                    onTap: () {
                                      if (_commentSheetController.isAttached) {
                                        _commentSheetController.animateTo(
                                          1,
                                          duration: const Duration(
                                            milliseconds: 180,
                                          ),
                                          curve: Curves.easeOut,
                                        );
                                      }
                                    },
                                    controller: _comment,
                                    focusNode: _commentFocus,
                                    minLines: 1,
                                    maxLines: 3,
                                    decoration: InputDecoration(
                                      hintText: AppStrings.t(
                                        'watch.commentHint',
                                      ),
                                      filled: true,
                                      fillColor: Theme.of(
                                        context,
                                      ).colorScheme.surfaceContainerLow,
                                      border: OutlineInputBorder(
                                        borderRadius: BorderRadius.circular(20),
                                        borderSide: BorderSide.none,
                                      ),
                                      enabledBorder: OutlineInputBorder(
                                        borderRadius: BorderRadius.circular(20),
                                        borderSide: BorderSide.none,
                                      ),
                                    ),
                                  ),
                                ),
                                const SizedBox(width: 8),
                                ValueListenableBuilder<TextEditingValue>(
                                  valueListenable: _comment,
                                  builder: (context, value, _) =>
                                      IconButton.filled(
                                        tooltip: AppStrings.t('common.send'),
                                        onPressed:
                                            _sendingComment ||
                                                value.text.trim().isEmpty
                                            ? null
                                            : _sendComment,
                                        icon: _sendingComment
                                            ? const SizedBox.square(
                                                dimension: 20,
                                                child:
                                                    CircularProgressIndicator(
                                                      strokeWidth: 2,
                                                    ),
                                              )
                                            : const Icon(
                                                Icons.arrow_upward_rounded,
                                              ),
                                      ),
                                ),
                              ],
                            )
                          : TextButton.icon(
                              onPressed: () {
                                Navigator.pop(sheetContext);
                                _requireAuth(
                                  action: AppStrings.t('watch.commentHint'),
                                );
                              },
                              icon: const Icon(Icons.login),
                              label: Text(AppStrings.t('common.signIn')),
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
  }

  Future<void> _sendComment() async {
    final generation = _generation;
    final videoId = widget.videoId;
    final text = _comment.text.trim();
    if (text.isEmpty || _sendingComment) return;
    if (!await _requireAuth(action: 'bình luận về video này')) return;
    HapticFeedback.lightImpact();
    _updateComments(() => _sendingComment = true);
    try {
      final comment = await _content.createComment(videoId, text);
      if (mounted && generation == _generation) {
        _updateComments(() {
          _comments = [comment, ..._comments];
          _commentTotal++;
          _comment.clear();
          _sendingComment = false;
        });
      }
    } catch (error) {
      if (mounted && generation == _generation) {
        _updateComments(() {
          _commentsError = error is ApiFailure
              ? AppStrings.apiError(error)
              : AppStrings.t('common.networkError');
          _sendingComment = false;
        });
      }
    }
  }

  Future<void> _togglePinComment(CommentItem item) =>
      _setCreatorCommentState(item, pin: true);
  Future<void> _toggleHeartComment(CommentItem item) =>
      _setCreatorCommentState(item, pin: false);

  Future<void> _setCreatorCommentState(
    CommentItem item, {
    required bool pin,
  }) async {
    final generation = _generation;
    final enabled = pin ? !item.isPinned : !item.hasCreatorHeart;
    try {
      final updated = await _content.setCommentCreatorState(
        item.id,
        enabled: enabled,
        pin: pin,
      );
      if (!mounted || generation != _generation) return;
      HapticFeedback.lightImpact();
      _updateComments(() {
        _commentsError = null;
        _comments = _comments
            .map(
              (c) => c.id == updated.id
                  ? updated
                  : pin && updated.isPinned
                  ? c.copyWith(isPinned: false)
                  : c,
            )
            .toList();
        if (pin && updated.isPinned) {
          _comments = [updated, ..._comments.where((c) => c.id != updated.id)];
        }
      });
    } catch (error) {
      if (!mounted || generation != _generation) return;
      _updateComments(
        () => _commentsError = error is ApiFailure
            ? AppStrings.apiError(error)
            : AppStrings.t('common.networkError'),
      );
    }
  }

  Future<void> _toggleHideComment(CommentItem item) async {
    final generation = _generation;
    HapticFeedback.lightImpact();
    final isHidden = item.status == 'hidden';
    try {
      final updated = await _content.setCommentVisibility(item.id, !isHidden);
      if (mounted && generation == _generation) {
        _updateComments(() {
          _comments = _comments
              .map((c) => c.id == item.id ? updated : c)
              .toList();
        });
      }
    } on ApiFailure catch (e) {
      if (mounted && generation == _generation) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(AppStrings.apiError(e))));
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_loading) {
      return ListView(
        padding: const EdgeInsets.fromLTRB(0, 0, 0, 32),
        children: const [
          _WatchSkeletonPlayer(),
          Padding(padding: EdgeInsets.all(20), child: _WatchSkeletonCopy()),
        ],
      );
    }
    if (_error != null || _video == null) {
      return HuTubeStateView(
        icon: Icons.play_disabled_rounded,
        title: _error ?? AppStrings.t('watch.videoUnavailable'),
        message: AppStrings.t('watch.sourceUnavailable'),
        actionLabel: AppStrings.t('common.retry'),
        onAction: _load,
      );
    }
    final video = _video!;
    final renderFullscreen = _isFullScreen || AppPictureInPicture.active.value;
    final videoStage = _CustomVideoStage(
      key: _videoStageKey,
      surfaceKey: _pipSurfaceKey,
      session: widget.playback,
      isFullScreen: renderFullscreen,
      controlsLocked: _controlsLocked,
      onUnlockControls: () => setState(() => _controlsLocked = false),
      zoomToFill: _zoomToFill,
      onToggleFullScreen: () => _toggleFullScreen(),
      onToggleZoom: (value) {
        setState(() => _zoomToFill = value);
        _prefs.writeZoomToFill(value);
      },
      onMinimize: () {
        if (_isFullScreen) {
          _toggleFullScreen(toFullScreen: false);
        } else {
          widget.playback.minimize();
          if (context.canPop()) {
            context.pop();
          } else {
            context.go('/home');
          }
        }
      },
      onSettings: _showPlaybackSettings,
      onPictureInPicture: _enterPictureInPicture,
      showPictureInPicture: true,
      systemPip: AppPictureInPicture.active.value,
    );

    return PopScope(
      canPop: !_isFullScreen,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop && _isFullScreen) {
          _toggleFullScreen(toFullScreen: false);
        } else if (didPop) {
          if (widget.playback.videoId == widget.videoId &&
              widget.playback.ready) {
            widget.playback.minimize();
          }
        }
      },
      child: Scaffold(
        backgroundColor: renderFullscreen
            ? Colors.black
            : Theme.of(context).scaffoldBackgroundColor,
        body: Stack(
          fit: StackFit.expand,
          children: [
            renderFullscreen
                ? ColoredBox(
                    color: Colors.black,
                    child: SizedBox.expand(child: videoStage),
                  )
                : SafeArea(
                    top: true,
                    bottom: false,
                    child: LayoutBuilder(
                      builder: (context, constraints) => Column(
                        children: [
                          SizedBox(
                            key: const ValueKey('watch-player'),
                            width: double.infinity,
                            height: (constraints.maxWidth * 9 / 16).clamp(
                              0.0,
                              constraints.maxHeight * .45,
                            ),
                            child: videoStage,
                          ),
                          Expanded(
                            child: ListView(
                              key: const PageStorageKey('watch-details'),
                              padding: const EdgeInsets.fromLTRB(0, 0, 0, 32),
                              children: [
                                Padding(
                                  padding: const EdgeInsets.fromLTRB(
                                    20,
                                    18,
                                    20,
                                    0,
                                  ),
                                  child: Text(
                                    video.title,
                                    style: Theme.of(context)
                                        .textTheme
                                        .headlineSmall
                                        ?.copyWith(
                                          fontWeight: FontWeight.w900,
                                          letterSpacing: -.45,
                                        ),
                                  ),
                                ),
                                Padding(
                                  padding: const EdgeInsets.fromLTRB(
                                    20,
                                    7,
                                    20,
                                    0,
                                  ),
                                  child: Text(
                                    AppStrings.format('watch.viewsAndChannel', {
                                      'views': AppStrings.number(
                                        video.stats.views,
                                      ),
                                      'channel': video.channelName,
                                    }),
                                    style: Theme.of(
                                      context,
                                    ).textTheme.bodySmall,
                                  ),
                                ),
                                const SizedBox(height: 14),
                                SingleChildScrollView(
                                  scrollDirection: Axis.horizontal,
                                  padding: const EdgeInsets.symmetric(
                                    horizontal: 20,
                                  ),
                                  child: Row(
                                    children: [
                                      _ActionChip(
                                        icon: Icons.thumb_up_outlined,
                                        label: AppStrings.compactLikeCount(
                                          video.stats.likes,
                                        ),
                                        active:
                                            video.viewerState.reaction ==
                                            'like',
                                        onTap: () => _react('like'),
                                      ),
                                      _ActionChip(
                                        icon: Icons.thumb_down_outlined,
                                        semanticLabel:
                                            '${AppStrings.t('watch.dislike')}: ${AppStrings.number(video.stats.dislikes)}',
                                        label: AppStrings.compactLikeCount(
                                          video.stats.dislikes,
                                        ),
                                        active:
                                            video.viewerState.reaction ==
                                            'dislike',
                                        onTap: () => _react('dislike'),
                                      ),
                                      _ActionChip(
                                        icon: Icons.star_border_rounded,
                                        label: video.viewerState.rating != null
                                            ? '${video.viewerState.rating} ★'
                                            : AppStrings.t('watch.rateAction'),
                                        active:
                                            video.viewerState.rating != null,
                                        onTap: _rate,
                                      ),
                                      _ActionChip(
                                        iconAsset: AppIcons.share,
                                        label: AppStrings.t(
                                          'watch.shareAction',
                                        ),
                                        onTap: _share,
                                      ),
                                      _ActionChip(
                                        iconAsset: AppIcons.download,
                                        label: AppStrings.t(
                                          'watch.downloadAction',
                                        ),
                                        onTap: _download,
                                      ),
                                      _ActionChip(
                                        iconAsset: AppIcons.playlist,
                                        label: AppStrings.t(
                                          'watch.savePlaylist',
                                        ),
                                        onTap: _saveToPlaylist,
                                      ),
                                      _ActionChip(
                                        iconAsset: AppIcons.queue,
                                        label: AppStrings.t('watch.queue'),
                                        active:
                                            widget.playback.queue.isNotEmpty,
                                        onTap: _openQueueSheet,
                                      ),
                                      _ActionChip(
                                        iconAsset: AppIcons.report,
                                        label: AppStrings.t('watch.report'),
                                        onTap: () => showContentReportDialog(
                                          context,
                                          auth: widget.auth,
                                          targetType: 'video',
                                          targetId: video.id,
                                        ),
                                      ),
                                      _ActionChip(
                                        icon: Icons
                                            .picture_in_picture_alt_outlined,
                                        label: AppStrings.t('watch.pipAction'),
                                        onTap: _enterPictureInPicture,
                                      ),
                                      _ActionChip(
                                        icon: Icons.headphones_outlined,
                                        label:
                                            widget
                                                .playback
                                                .waitingForExternalAudio
                                            ? AppStrings.t(
                                                'watch.backgroundWaitingAudio',
                                              )
                                            : _backgroundPlaybackEnabled
                                            ? AppStrings.t(
                                                'watch.backgroundEnabledShort',
                                              )
                                            : AppStrings.t('watch.background'),
                                        active: _backgroundPlaybackEnabled,
                                        onTap: _toggleBackgroundPlayback,
                                      ),
                                    ],
                                  ),
                                ),
                                if (_actionMessage != null)
                                  Padding(
                                    padding: const EdgeInsets.fromLTRB(
                                      20,
                                      12,
                                      20,
                                      0,
                                    ),
                                    child: Text(
                                      _actionMessage!,
                                      style: const TextStyle(
                                        color: AppColors.primaryPink,
                                      ),
                                    ),
                                  ),
                                const SizedBox(height: 18),
                                Padding(
                                  padding: const EdgeInsets.symmetric(
                                    horizontal: 20,
                                  ),
                                  child: _ChannelSummary(
                                    video: video,
                                    auth: widget.auth,
                                  ),
                                ),
                                if ((video.description ?? '').isNotEmpty ||
                                    video.tags.isNotEmpty) ...[
                                  const SizedBox(height: 14),
                                  Padding(
                                    padding: const EdgeInsets.symmetric(
                                      horizontal: 20,
                                    ),
                                    child: HuTubeSurface(
                                      color: AppColors.surfaceAltFor(context),
                                      child: Column(
                                        crossAxisAlignment:
                                            CrossAxisAlignment.stretch,
                                        children: [
                                          if ((video.description ?? '')
                                              .isNotEmpty)
                                            VideoDescription(
                                              key: ValueKey(video.id),
                                              text: video.description!,
                                              duration: video.duration,
                                              onSeek: widget.playback.seekTo,
                                            ),
                                          if (video.tags.isNotEmpty)
                                            Padding(
                                              padding: const EdgeInsets.only(
                                                top: 10,
                                              ),
                                              child: Wrap(
                                                spacing: 6,
                                                children: video.tags
                                                    .map(
                                                      (tag) => ActionChip(
                                                        label: Text('#$tag'),
                                                        onPressed: () =>
                                                            context.push(
                                                              Uri(
                                                                path: '/search',
                                                                queryParameters:
                                                                    {
                                                                      'q': tag,
                                                                      'tag':
                                                                          tag,
                                                                    },
                                                              ).toString(),
                                                            ),
                                                      ),
                                                    )
                                                    .toList(),
                                              ),
                                            ),
                                        ],
                                      ),
                                    ),
                                  ),
                                ],
                                if (video.chapters.isNotEmpty) ...[
                                  const SizedBox(height: 16),
                                  Text(
                                    AppStrings.t('watch.chapters'),
                                    style: Theme.of(context)
                                        .textTheme
                                        .titleMedium
                                        ?.copyWith(fontWeight: FontWeight.w800),
                                  ),
                                  ...video.chapters.map(
                                    (chapter) => ListTile(
                                      contentPadding: EdgeInsets.zero,
                                      leading: const Icon(
                                        Icons.play_circle_outline_rounded,
                                      ),
                                      title: Text(chapter.title),
                                      subtitle: Text(
                                        '${chapter.startSeconds ~/ 60}:${(chapter.startSeconds % 60).toString().padLeft(2, '0')}',
                                      ),
                                      onTap: () => widget.playback.seekTo(
                                        Duration(seconds: chapter.startSeconds),
                                      ),
                                    ),
                                  ),
                                ],
                                const SizedBox(height: 16),
                                Padding(
                                  padding: const EdgeInsets.symmetric(
                                    horizontal: 16,
                                  ),
                                  child: _buildCommentPreview(),
                                ),
                                const SizedBox(height: 22),
                                Padding(
                                  padding: const EdgeInsets.symmetric(
                                    horizontal: 20,
                                  ),
                                  child: Column(
                                    crossAxisAlignment:
                                        CrossAxisAlignment.stretch,
                                    children: [
                                      Wrap(
                                        alignment: WrapAlignment.start,
                                        spacing: 8,
                                        children: [
                                          for (final filter in [
                                            'all',
                                            'related',
                                            'channel',
                                          ])
                                            ChoiceChip(
                                              label: Text(
                                                AppStrings.t('parity.$filter'),
                                              ),
                                              selected:
                                                  _recommendationFilter ==
                                                  filter,
                                              onSelected: (_) =>
                                                  _loadRecommendations(filter),
                                            ),
                                        ],
                                      ),
                                      if (_recommendationsLoading)
                                        const LinearProgressIndicator(),
                                      if (_recommendationsError != null)
                                        TextButton(
                                          onPressed: () => _loadRecommendations(
                                            _recommendationFilter,
                                          ),
                                          child: Text(_recommendationsError!),
                                        ),
                                      if (!_recommendationsLoading &&
                                          _related.isEmpty &&
                                          _recommendationsError == null)
                                        Text(AppStrings.t('parity.noResults')),
                                      ..._related.map(
                                        (item) => VideoCardTile(
                                          video: item,
                                          replaceRoute: true,
                                          auth: widget.auth,
                                        ),
                                      ),
                                    ],
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
            if (_upNext case final next?)
              Positioned(
                left: 16,
                right: 16,
                bottom: 16,
                child: SafeArea(
                  top: false,
                  child: AutoplayNextNotice(
                    key: ValueKey('autoplay-${next.id}'),
                    video: next,
                    onCancel: _cancelAutoplay,
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _ActionChip extends StatelessWidget {
  const _ActionChip({
    this.icon,
    this.iconAsset,
    this.semanticLabel,
    required this.label,
    required this.onTap,
    this.active = false,
  });
  final IconData? icon;
  final String? iconAsset;
  final String label;
  final String? semanticLabel;
  final VoidCallback onTap;
  final bool active;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    final foreground = active ? colors.primary : colors.onSurface;
    return Padding(
      padding: const EdgeInsets.only(right: 8),
      child: Tooltip(
        message: semanticLabel ?? label,
        child: ActionChip(
          avatar: iconAsset != null
              ? AppIcons.asset(iconAsset!, size: 18, color: foreground)
              : (icon == null ? null : Icon(icon, size: 18, color: foreground)),
          label: Text(label),
          labelStyle: TextStyle(color: foreground, fontWeight: FontWeight.w700),
          backgroundColor: active
              ? colors.primary.withValues(alpha: .12)
              : colors.surface,
          side: BorderSide(
            color: active
                ? colors.primary.withValues(alpha: .5)
                : AppColors.borderFor(context),
          ),
          shape: const StadiumBorder(),
          onPressed: onTap,
        ),
      ),
    );
  }
}

class _PlaybackSettingTile extends StatelessWidget {
  const _PlaybackSettingTile({
    required this.icon,
    required this.title,
    required this.onTap,
    this.value,
    this.trailing,
    this.dense = true,
  });

  final IconData icon;
  final String title;
  final String? value;
  final Widget? trailing;
  final VoidCallback onTap;
  final bool dense;

  @override
  Widget build(BuildContext context) => ListTile(
    dense: dense,
    visualDensity: dense
        ? const VisualDensity(horizontal: 0, vertical: -2.5)
        : VisualDensity.standard,
    contentPadding: EdgeInsets.symmetric(
      horizontal: 16,
      vertical: dense ? 0 : 2,
    ),
    minLeadingWidth: 26,
    leading: Icon(icon, color: Colors.white, size: dense ? 20 : 22),
    title: Text(
      title,
      style: TextStyle(
        color: Colors.white,
        fontSize: dense ? 13.5 : 14.5,
        fontWeight: FontWeight.w500,
      ),
    ),
    trailing:
        trailing ??
        (value == null
            ? null
            : Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    value!,
                    style: TextStyle(
                      color: Colors.white60,
                      fontSize: dense ? 12.5 : 13.5,
                    ),
                  ),
                  const SizedBox(width: 4),
                  const Icon(
                    Icons.chevron_right_rounded,
                    color: Colors.white60,
                    size: 18,
                  ),
                ],
              )),
    onTap: onTap,
  );
}

class _CustomVideoStage extends StatefulWidget {
  const _CustomVideoStage({
    super.key,
    required this.session,
    required this.onMinimize,
    required this.onSettings,
    required this.onPictureInPicture,
    required this.showPictureInPicture,
    required this.surfaceKey,
    this.isFullScreen = false,
    this.systemPip = false,
    this.controlsLocked = false,
    this.onUnlockControls,
    this.zoomToFill = false,
    this.onToggleFullScreen,
    this.onToggleZoom,
  });

  final PlaybackSession session;
  final VoidCallback onMinimize;
  final VoidCallback onSettings;
  final VoidCallback onPictureInPicture;
  final bool showPictureInPicture;
  final GlobalKey surfaceKey;
  final bool isFullScreen;
  final bool systemPip;
  final bool controlsLocked;
  final VoidCallback? onUnlockControls;
  final bool zoomToFill;
  final VoidCallback? onToggleFullScreen;
  final ValueChanged<bool>? onToggleZoom;

  @override
  State<_CustomVideoStage> createState() => _CustomVideoStageState();
}

class _CustomVideoStageState extends State<_CustomVideoStage> {
  NativeVideoPlayer? _nativeSurface;
  bool _controlsVisible = true;
  Timer? _controlsTimer;
  bool? _seekForward;
  int _seekStep = 10;
  int _accumulatedSeekSeconds = 0;
  Timer? _pulseTimer;
  Timer? _singleTapTimer;
  DateTime? _lastTapTime;
  bool? _lastTapForward;
  double? _dragPosition;
  String? _lastLoggedLayout;
  String? _zoomFeedbackText;
  Timer? _zoomFeedbackTimer;
  bool _isPinching = false;
  double _pinchScaleDelta = 1.0;

  @override
  void initState() {
    super.initState();
    _loadSeekStep();
    _scheduleHide();
  }

  Future<void> _loadSeekStep() async {
    try {
      final step = await const AppPreferencesStore().readDoubleTapSeek();
      if (mounted) setState(() => _seekStep = step);
    } catch (_) {}
  }

  void _triggerZoomFeedback(bool zoomToFill) {
    HapticFeedback.selectionClick();
    _zoomFeedbackTimer?.cancel();
    setState(() {
      _zoomFeedbackText = zoomToFill
          ? 'Đã phóng to để lấp đầy màn hình'
          : 'Nguyên bản';
    });
    _zoomFeedbackTimer = Timer(const Duration(milliseconds: 1500), () {
      if (mounted) setState(() => _zoomFeedbackText = null);
    });
  }

  @override
  void dispose() {
    _controlsTimer?.cancel();
    _pulseTimer?.cancel();
    _singleTapTimer?.cancel();
    _zoomFeedbackTimer?.cancel();
    super.dispose();
  }

  void _scheduleHide() {
    _controlsTimer?.cancel();
    if (!widget.session.isPlaying) return;
    _controlsTimer = Timer(const Duration(seconds: 4), () {
      if (mounted) setState(() => _controlsVisible = false);
    });
  }

  void _toggleControls() {
    setState(() => _controlsVisible = !_controlsVisible);
    if (_controlsVisible) _scheduleHide();
  }

  void _handleSideTap(bool forward) {
    final now = DateTime.now();
    final isSameSide = _lastTapForward == forward;
    final isQuickConsecutiveTap =
        isSameSide &&
        _lastTapTime != null &&
        now.difference(_lastTapTime!).inMilliseconds <
            (_seekForward != null ? 650 : 280);

    _lastTapTime = now;
    _lastTapForward = forward;

    if (isQuickConsecutiveTap || _seekForward == forward) {
      _singleTapTimer?.cancel();
      _singleTapTimer = null;

      _accumulatedSeekSeconds += _seekStep;
      unawaited(widget.session.seekBy(forward ? _seekStep : -_seekStep));

      setState(() {
        _seekForward = forward;
        _controlsVisible = false;
      });

      _pulseTimer?.cancel();
      _pulseTimer = Timer(const Duration(milliseconds: 800), () {
        if (mounted) {
          setState(() {
            _seekForward = null;
            _accumulatedSeekSeconds = 0;
            _lastTapTime = null;
            _lastTapForward = null;
          });
        }
      });
    } else {
      _singleTapTimer?.cancel();
      _singleTapTimer = Timer(const Duration(milliseconds: 280), () {
        if (mounted && _seekForward == null) {
          _lastTapTime = null;
          _lastTapForward = null;
          _toggleControls();
        }
      });
    }
  }

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: widget.session,
    builder: (context, _) {
      final player = widget.session.player;
      if (player == null) return const ColoredBox(color: AppColors.ink);
      // Only controls need rebuilding on progress ticks. Keep the native
      // widget identical until its controller changes; inherited layout/theme
      // changes still reach the mounted native widget normally.
      if (_nativeSurface?.controller != player) {
        _nativeSurface = NativeVideoPlayer(controller: player);
      }
      final total = widget.session.duration.inMilliseconds;
      final current =
          (_dragPosition ?? widget.session.position.inMilliseconds.toDouble())
              .clamp(0.0, total > 0 ? total.toDouble() : 1.0);
      return LayoutBuilder(
        builder: (context, constraints) {
          final width = constraints.maxWidth;
          final height = constraints.maxHeight;
          final videoSize = player.videoSize;
          final double videoAspect =
              (videoSize != null && videoSize.width > 0 && videoSize.height > 0)
              ? videoSize.aspectRatio
              : (16 / 9);

          final double scale;
          if (!widget.systemPip &&
              widget.zoomToFill &&
              widget.isFullScreen &&
              height > 0) {
            final double containerAspect = width / height;
            scale = containerAspect > videoAspect
                ? (containerAspect / videoAspect)
                : (videoAspect / containerAspect);
          } else {
            scale = 1.0;
          }

          final layoutSignature =
              'fullscreen=${widget.isFullScreen} '
              'constraints=${width.toStringAsFixed(1)}x${height.toStringAsFixed(1)} '
              'video=${videoSize?.width}x${videoSize?.height}';
          if (kDebugMode && layoutSignature != _lastLoggedLayout) {
            _lastLoggedLayout = layoutSignature;
            debugPrint('[Dalitu player] $layoutSignature');
          }

          return GestureDetector(
            behavior: HitTestBehavior.translucent,
            onScaleStart: (details) {
              if (details.pointerCount >= 2) {
                _isPinching = true;
                _pinchScaleDelta = 1.0;
              }
            },
            onScaleUpdate: (details) {
              if (details.pointerCount >= 2) {
                _isPinching = true;
                _pinchScaleDelta = details.scale;
              }
            },
            onScaleEnd: (details) {
              if (_isPinching) {
                _isPinching = false;
                if (widget.isFullScreen) {
                  if (_pinchScaleDelta > 1.06) {
                    if (!widget.zoomToFill) {
                      _triggerZoomFeedback(true);
                      widget.onToggleZoom?.call(true);
                    } else {
                      _triggerZoomFeedback(true);
                    }
                  } else if (_pinchScaleDelta < 0.94) {
                    if (widget.zoomToFill) {
                      _triggerZoomFeedback(false);
                      widget.onToggleZoom?.call(false);
                    } else if (_pinchScaleDelta < 0.80) {
                      widget.onMinimize();
                    } else {
                      _triggerZoomFeedback(false);
                    }
                  }
                } else {
                  if (_pinchScaleDelta > 1.15) {
                    widget.onToggleFullScreen?.call();
                  }
                }
              }
            },
            child: Stack(
              fit: StackFit.expand,
              children: [
                ClipRect(
                  child: Center(
                    child: AnimatedScale(
                      scale: scale,
                      duration: const Duration(milliseconds: 240),
                      curve: Curves.easeOutCubic,
                      child: AspectRatio(
                        aspectRatio: videoAspect,
                        child: RepaintBoundary(
                          key: widget.surfaceKey,
                          child: PipVideoSurface(child: _nativeSurface!),
                        ),
                      ),
                    ),
                  ),
                ),
                Positioned.fill(
                  child: Row(
                    children: [
                      Expanded(
                        child: GestureDetector(
                          behavior: HitTestBehavior.opaque,
                          onTap: () => _handleSideTap(false),
                          onVerticalDragEnd: (details) {
                            if (_isPinching) return;
                            if ((details.primaryVelocity ?? 0) > 240) {
                              widget.onMinimize();
                            }
                          },
                          child: const SizedBox.expand(),
                        ),
                      ),
                      Expanded(
                        child: GestureDetector(
                          behavior: HitTestBehavior.opaque,
                          onTap: () => _handleSideTap(true),
                          onVerticalDragEnd: (details) {
                            if (_isPinching) return;
                            if ((details.primaryVelocity ?? 0) > 240) {
                              widget.onMinimize();
                            }
                          },
                          child: const SizedBox.expand(),
                        ),
                      ),
                    ],
                  ),
                ),
                if (!widget.session.ready)
                  const Positioned.fill(
                    child: ColoredBox(
                      color: Color(0x9917111F),
                      child: Center(
                        child: CircularProgressIndicator(
                          color: AppColors.primaryPink,
                        ),
                      ),
                    ),
                  ),
                if (_seekForward == true)
                  Positioned(
                    right: 0,
                    top: 0,
                    bottom: 0,
                    width: width * 0.42,
                    child: Container(
                      decoration: BoxDecoration(
                        color: Colors.white.withValues(alpha: 0.18),
                        borderRadius: const BorderRadius.horizontal(
                          left: Radius.elliptical(90, 200),
                        ),
                      ),
                      alignment: Alignment.center,
                      child: Column(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          const Icon(
                            Icons.fast_forward_rounded,
                            color: Colors.white,
                            size: 38,
                          ),
                          const SizedBox(height: 4),
                          Text(
                            '+$_accumulatedSeekSeconds giây',
                            style: const TextStyle(
                              color: Colors.white,
                              fontSize: 12,
                              fontWeight: FontWeight.w800,
                              shadows: [
                                Shadow(blurRadius: 4, color: Colors.black87),
                              ],
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                if (_seekForward == false)
                  Positioned(
                    left: 0,
                    top: 0,
                    bottom: 0,
                    width: width * 0.42,
                    child: Container(
                      decoration: BoxDecoration(
                        color: Colors.white.withValues(alpha: 0.18),
                        borderRadius: const BorderRadius.horizontal(
                          right: Radius.elliptical(90, 200),
                        ),
                      ),
                      alignment: Alignment.center,
                      child: Column(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          const Icon(
                            Icons.fast_rewind_rounded,
                            color: Colors.white,
                            size: 38,
                          ),
                          const SizedBox(height: 4),
                          Text(
                            '-$_accumulatedSeekSeconds giây',
                            style: const TextStyle(
                              color: Colors.white,
                              fontSize: 12,
                              fontWeight: FontWeight.w800,
                              shadows: [
                                Shadow(blurRadius: 4, color: Colors.black87),
                              ],
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                if (_zoomFeedbackText != null)
                  Positioned(
                    top: widget.isFullScreen ? 28 : 16,
                    left: 0,
                    right: 0,
                    child: IgnorePointer(
                      child: Center(
                        child: Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 16,
                            vertical: 8,
                          ),
                          decoration: BoxDecoration(
                            color: Colors.black.withValues(alpha: 0.85),
                            borderRadius: BorderRadius.circular(6),
                          ),
                          child: Text(
                            _zoomFeedbackText!,
                            style: const TextStyle(
                              color: Colors.white,
                              fontSize: 13.5,
                              fontWeight: FontWeight.w600,
                              letterSpacing: 0.2,
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
                Positioned.fill(
                  child: AnimatedOpacity(
                    opacity: _controlsVisible && !widget.systemPip ? 1.0 : 0.0,
                    duration: widget.systemPip
                        ? Duration.zero
                        : const Duration(milliseconds: 180),
                    child: IgnorePointer(
                      ignoring:
                          !_controlsVisible ||
                          widget.controlsLocked ||
                          widget.systemPip,
                      child: DecoratedBox(
                        decoration: const BoxDecoration(
                          gradient: LinearGradient(
                            begin: Alignment.topCenter,
                            end: Alignment.bottomCenter,
                            colors: [
                              Color(0xB8000000),
                              Colors.transparent,
                              Color(0xD9000000),
                            ],
                            stops: [0, .48, 1],
                          ),
                        ),
                        child: SafeArea(
                          top: widget.isFullScreen,
                          bottom: widget.isFullScreen,
                          left: widget.isFullScreen,
                          right: widget.isFullScreen,
                          child: Column(
                            children: [
                              Row(
                                children: [
                                  IconButton(
                                    tooltip: widget.isFullScreen
                                        ? AppStrings.t('watch.exitFullscreen')
                                        : AppStrings.t('watch.minimize'),
                                    onPressed: widget.onMinimize,
                                    color: Colors.white,
                                    icon: Icon(
                                      widget.isFullScreen
                                          ? Icons.arrow_back_rounded
                                          : Icons.keyboard_arrow_down_rounded,
                                    ),
                                  ),
                                  const SizedBox(width: 4),
                                  Expanded(
                                    child: Text(
                                      widget.session.title,
                                      maxLines: 1,
                                      overflow: TextOverflow.ellipsis,
                                      style: const TextStyle(
                                        color: Colors.white,
                                        fontWeight: FontWeight.w800,
                                        fontSize: 13,
                                      ),
                                    ),
                                  ),
                                  IconButton(
                                    tooltip: AppStrings.t(
                                      'watch.qualityAndSpeed',
                                    ),
                                    onPressed: () {
                                      _controlsTimer?.cancel();
                                      widget.onSettings();
                                    },
                                    color: Colors.white,
                                    icon: const Icon(
                                      Icons.settings_rounded,
                                      size: 20,
                                    ),
                                  ),
                                  if (widget.showPictureInPicture)
                                    IconButton(
                                      tooltip: AppStrings.t(
                                        'watch.pictureInPicture',
                                      ),
                                      onPressed: widget.onPictureInPicture,
                                      color: Colors.white,
                                      icon: const Icon(
                                        Icons.picture_in_picture_alt_rounded,
                                        size: 19,
                                      ),
                                    ),
                                ],
                              ),
                              Expanded(
                                child: Row(
                                  children: [
                                    Expanded(
                                      child: GestureDetector(
                                        behavior: HitTestBehavior.opaque,
                                        onTap: () => _handleSideTap(false),
                                        onVerticalDragEnd: (details) {
                                          if (_isPinching) return;
                                          if ((details.primaryVelocity ?? 0) >
                                              240) {
                                            widget.onMinimize();
                                          }
                                        },
                                        child: const SizedBox.expand(),
                                      ),
                                    ),
                                    if (!widget.session.isPlaying)
                                      IconButton.filled(
                                        tooltip: AppStrings.t(
                                          'watch.playVideo',
                                        ),
                                        onPressed: () {
                                          unawaited(
                                            widget.session.togglePlayback(),
                                          );
                                          _scheduleHide();
                                        },
                                        style: IconButton.styleFrom(
                                          backgroundColor: Colors.black
                                              .withValues(alpha: .58),
                                          foregroundColor: Colors.white,
                                          minimumSize: const Size(58, 58),
                                        ),
                                        icon: const Icon(
                                          Icons.play_arrow_rounded,
                                          size: 36,
                                        ),
                                      )
                                    else
                                      IconButton.filled(
                                        tooltip: AppStrings.t(
                                          'watch.pauseVideo',
                                        ),
                                        onPressed: () {
                                          unawaited(
                                            widget.session.togglePlayback(),
                                          );
                                          _scheduleHide();
                                        },
                                        style: IconButton.styleFrom(
                                          backgroundColor: Colors.black
                                              .withValues(alpha: .58),
                                          foregroundColor: Colors.white,
                                          minimumSize: const Size(52, 52),
                                        ),
                                        icon: const Icon(
                                          Icons.pause_rounded,
                                          size: 30,
                                        ),
                                      ),
                                    Expanded(
                                      child: GestureDetector(
                                        behavior: HitTestBehavior.opaque,
                                        onTap: () => _handleSideTap(true),
                                        onVerticalDragEnd: (details) {
                                          if (_isPinching) return;
                                          if ((details.primaryVelocity ?? 0) >
                                              240) {
                                            widget.onMinimize();
                                          }
                                        },
                                        child: const SizedBox.expand(),
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                              Padding(
                                padding: const EdgeInsets.fromLTRB(
                                  10,
                                  0,
                                  10,
                                  5,
                                ),
                                child: Row(
                                  children: [
                                    Text(
                                      _time(widget.session.position),
                                      style: const TextStyle(
                                        color: Colors.white,
                                        fontSize: 11,
                                        fontWeight: FontWeight.w700,
                                      ),
                                    ),
                                    Expanded(
                                      child: SliderTheme(
                                        data: SliderTheme.of(context).copyWith(
                                          trackHeight: 2.5,
                                          thumbShape:
                                              const RoundSliderThumbShape(
                                                enabledThumbRadius: 5,
                                              ),
                                          overlayShape:
                                              const RoundSliderOverlayShape(
                                                overlayRadius: 13,
                                              ),
                                        ),
                                        child: Slider(
                                          min: 0,
                                          max: total > 0 ? total.toDouble() : 1,
                                          value: current,
                                          activeColor: AppColors.primaryPink,
                                          inactiveColor: Colors.white38,
                                          onChangeStart: (val) {
                                            _controlsTimer?.cancel();
                                            setState(() => _dragPosition = val);
                                          },
                                          onChanged: total <= 0
                                              ? null
                                              : (value) {
                                                  setState(
                                                    () => _dragPosition = value,
                                                  );
                                                },
                                          onChangeEnd: (val) {
                                            unawaited(
                                              widget.session.seekTo(
                                                Duration(
                                                  milliseconds: val.round(),
                                                ),
                                              ),
                                            );
                                            setState(
                                              () => _dragPosition = null,
                                            );
                                            _scheduleHide();
                                          },
                                        ),
                                      ),
                                    ),
                                    Text(
                                      _time(widget.session.duration),
                                      style: const TextStyle(
                                        color: Colors.white,
                                        fontSize: 11,
                                        fontWeight: FontWeight.w700,
                                      ),
                                    ),
                                    if (widget.onToggleFullScreen != null) ...[
                                      const SizedBox(width: 4),
                                      IconButton(
                                        tooltip: widget.isFullScreen
                                            ? AppStrings.t(
                                                'watch.exitFullscreen',
                                              )
                                            : AppStrings.t('watch.fullscreen'),
                                        onPressed: widget.onToggleFullScreen,
                                        iconSize: 22,
                                        padding: const EdgeInsets.all(4),
                                        constraints: const BoxConstraints(),
                                        color: Colors.white,
                                        icon: Icon(
                                          widget.isFullScreen
                                              ? Icons.fullscreen_exit_rounded
                                              : Icons.fullscreen_rounded,
                                        ),
                                      ),
                                    ],
                                  ],
                                ),
                              ),
                            ],
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
                if (widget.controlsLocked && !widget.systemPip)
                  Positioned(
                    top: widget.isFullScreen ? 12 : 4,
                    left: 8,
                    child: SafeArea(
                      child: IconButton.filledTonal(
                        tooltip: 'Mở khóa màn hình',
                        onPressed: widget.onUnlockControls,
                        style: IconButton.styleFrom(
                          backgroundColor: Colors.black.withValues(alpha: .62),
                          foregroundColor: Colors.white,
                        ),
                        icon: const Icon(Icons.lock_open_rounded),
                      ),
                    ),
                  ),
              ],
            ),
          );
        },
      );
    },
  );

  String _time(Duration value) {
    final seconds = value.inSeconds;
    final hours = seconds ~/ 3600;
    final minutes = (seconds % 3600) ~/ 60;
    final rest = seconds % 60;
    if (hours > 0) {
      return '$hours:${minutes.toString().padLeft(2, '0')}:${rest.toString().padLeft(2, '0')}';
    }
    return '$minutes:${rest.toString().padLeft(2, '0')}';
  }
}

class _ChannelSummary extends StatefulWidget {
  const _ChannelSummary({required this.video, required this.auth});
  final VideoDetail video;
  final AuthController auth;

  @override
  State<_ChannelSummary> createState() => _ChannelSummaryState();
}

class _ChannelSummaryState extends State<_ChannelSummary> {
  late final ChannelService _channels = ChannelService(widget.auth);
  bool _subscribed = false;
  bool _notificationsEnabled = false;
  bool _busy = false;
  bool _isOwner = false;
  bool _ownershipReady = false;
  int _request = 0;
  late int _session;
  late bool _authenticated;

  @override
  void initState() {
    super.initState();
    _session = widget.auth.sessionGeneration;
    _authenticated = widget.auth.authenticated;
    widget.auth.addListener(_identityChanged);
    _loadSubscription();
  }

  void _identityChanged() {
    if (_session == widget.auth.sessionGeneration &&
        _authenticated == widget.auth.authenticated) {
      return;
    }
    _session = widget.auth.sessionGeneration;
    _authenticated = widget.auth.authenticated;
    _loadSubscription();
  }

  @override
  void didUpdateWidget(covariant _ChannelSummary oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.video.channelId != widget.video.channelId) {
      _loadSubscription();
    }
  }

  @override
  void dispose() {
    ++_request;
    widget.auth.removeListener(_identityChanged);
    super.dispose();
  }

  Future<void> _loadSubscription() async {
    final request = ++_request;
    setState(() {
      _subscribed = false;
      _notificationsEnabled = false;
      _isOwner = false;
      _busy = false;
      _ownershipReady = !widget.auth.authenticated;
    });
    if (!widget.auth.authenticated) return;
    try {
      final own = await _channels.getMyChannel();
      if (!mounted || request != _request) return;
      setState(() {
        _isOwner = own?.id == widget.video.channelId;
        _ownershipReady = true;
      });
      if (_isOwner) return;
      final status = await _channels.getSubscriptionStatus(
        widget.video.channelId,
      );
      if (mounted && request == _request) {
        setState(() {
          _subscribed = status?['status'] == 'active';
          _notificationsEnabled =
              _subscribed && status?['notificationsEnabled'] == true;
        });
      }
    } catch (_) {}
  }

  Future<void> _toggleSubscription() async {
    if (!_ownershipReady || _isOwner) return;
    if (!widget.auth.authenticated) {
      final proceed = await showModalBottomSheet<bool>(
        context: context,
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
        ),
        builder: (ctx) => SafeArea(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(24, 20, 24, 24),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  width: 40,
                  height: 4,
                  margin: const EdgeInsets.only(bottom: 20),
                  decoration: BoxDecoration(
                    color: Colors.grey.withValues(alpha: 0.4),
                    borderRadius: BorderRadius.circular(2),
                  ),
                ),
                const Icon(
                  Icons.subscriptions_outlined,
                  size: 52,
                  color: AppColors.primaryPink,
                ),
                const SizedBox(height: 14),
                const Text(
                  'Bạn muốn đăng ký kênh này?',
                  style: TextStyle(fontSize: 18, fontWeight: FontWeight.bold),
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: 8),
                Text(
                  'Đăng nhập để đăng ký kênh và nhận thông báo về video mới nhất.',
                  textAlign: TextAlign.center,
                  style: TextStyle(
                    fontSize: 14,
                    color: Theme.of(ctx).colorScheme.onSurfaceVariant,
                    height: 1.4,
                  ),
                ),
                const SizedBox(height: 24),
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton(
                        onPressed: () => Navigator.pop(ctx, false),
                        child: const Text('Để sau'),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: FilledButton(
                        style: FilledButton.styleFrom(
                          backgroundColor: AppColors.primaryPink,
                        ),
                        onPressed: () => Navigator.pop(ctx, true),
                        child: const Text('Đăng nhập'),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      );
      if (proceed == true && mounted) {
        context.push('/auth');
      }
      return;
    }
    if (_busy) return;
    final request = _request;
    setState(() => _busy = true);
    try {
      if (_subscribed) {
        await _channels.unsubscribe(widget.video.channelId);
      } else {
        final result = await _channels.subscribe(widget.video.channelId);
        if (mounted && request == _request) {
          _notificationsEnabled = result['notificationsEnabled'] == true;
        }
      }
      if (mounted && request == _request) {
        setState(() => _subscribed = !_subscribed);
      }
    } on ApiFailure catch (error) {
      if (mounted && request == _request) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(AppStrings.apiError(error))));
      }
    } finally {
      if (mounted && request == _request) setState(() => _busy = false);
    }
  }

  Future<void> _toggleNotifications() async {
    if (_busy || !_subscribed || _isOwner) return;
    final request = _request;
    setState(() => _busy = true);
    try {
      final result = await _channels.updateSubscriptionNotifications(
        widget.video.channelId,
        !_notificationsEnabled,
      );
      if (mounted && request == _request) {
        setState(
          () => _notificationsEnabled = result['notificationsEnabled'] == true,
        );
      }
    } on ApiFailure catch (e) {
      if (mounted && request == _request) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(AppStrings.apiError(e))));
      }
    } finally {
      if (mounted && request == _request) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final identity = Row(
        children: [
          HuTubeAvatar(label: widget.video.channelName, radius: 22),
          const SizedBox(width: 11),
          Expanded(
            child: InkWell(
              onTap: () =>
                  context.push('/channels/${widget.video.channelHandle}'),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    widget.video.channelName,
                    style: const TextStyle(fontWeight: FontWeight.w900),
                  ),
                  const SizedBox(height: 3),
                  Text(
                    '@${widget.video.channelHandle}',
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
                ],
              ),
            ),
          ),
        ],
      );
      final actions = Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          OutlinedButton(
            onPressed: !_ownershipReady || _busy
                ? null
                : _isOwner
                ? () => context.push('/channels/${widget.video.channelHandle}')
                : _toggleSubscription,
            style: OutlinedButton.styleFrom(
              minimumSize: const Size(0, 48),
              padding: const EdgeInsets.symmetric(horizontal: 14),
              backgroundColor: _subscribed
                  ? AppColors.surfaceAltFor(context)
                  : null,
            ),
            child: Text(
              _busy
                  ? '…'
                  : (_isOwner
                        ? AppStrings.t('channel.yourChannel')
                        : _subscribed
                        ? AppStrings.t('watch.following')
                        : AppStrings.t('watch.follow')),
              style: const TextStyle(fontWeight: FontWeight.w800),
            ),
          ),
          if (_subscribed && !_isOwner)
            IconButton(
              key: const ValueKey('watch-subscription-notifications'),
              tooltip: AppStrings.t(
                _notificationsEnabled
                    ? 'channel.notificationsOff'
                    : 'channel.notificationsOn',
              ),
              onPressed: _busy ? null : _toggleNotifications,
              icon: Icon(
                _notificationsEnabled
                    ? Icons.notifications_active_rounded
                    : Icons.notifications_none_rounded,
              ),
            ),
        ],
      );
      if (constraints.maxWidth < 350 ||
          MediaQuery.textScalerOf(context).scale(14) > 19) {
        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [identity, const SizedBox(height: 12), actions],
        );
      }
      return Row(
        children: [
          Expanded(child: identity),
          const SizedBox(width: 8),
          actions,
        ],
      );
    },
  );
}

class _WatchSkeletonPlayer extends StatelessWidget {
  const _WatchSkeletonPlayer();

  @override
  Widget build(BuildContext context) => AspectRatio(
    aspectRatio: 16 / 9,
    child: DecoratedBox(
      decoration: const BoxDecoration(color: AppColors.ink),
      child: Center(
        child: CircularProgressIndicator(
          color: AppColors.primary,
          backgroundColor: Colors.white12,
        ),
      ),
    ),
  );
}

class _WatchSkeletonCopy extends StatelessWidget {
  const _WatchSkeletonCopy();

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Container(width: 250, height: 22, color: AppColors.borderSubtle),
      const SizedBox(height: 10),
      Container(width: 180, height: 13, color: AppColors.borderSubtle),
      const SizedBox(height: 22),
      Container(height: 66, color: AppColors.borderSubtle),
    ],
  );
}

class _CommentTile extends StatefulWidget {
  const _CommentTile({
    super.key,
    required this.item,
    required this.content,
    required this.signedIn,
    required this.auth,
    required this.channelName,
    required this.isOwner,
    required this.isPinned,
    required this.isHearted,
    required this.onTogglePin,
    required this.onToggleHeart,
    required this.onToggleHide,
    this.onUpdated,
    this.onSignIn,
    this.depth = 0,
    this.showReplies = true,
    this.onReplyRequest,
  });
  final CommentItem item;
  final ContentService content;
  final bool signedIn;
  final AuthController auth;
  final String channelName;
  final bool isOwner;
  final bool isPinned;
  final bool isHearted;
  final VoidCallback onTogglePin;
  final VoidCallback onToggleHeart;
  final VoidCallback onToggleHide;
  final ValueChanged<CommentItem>? onUpdated;
  final VoidCallback? onSignIn;
  final int depth;
  final bool showReplies;
  final VoidCallback? onReplyRequest;

  @override
  State<_CommentTile> createState() => _CommentTileState();
}

class _CommentTileState extends State<_CommentTile> {
  int _generation = 0;
  @override
  void didUpdateWidget(covariant _CommentTile oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.item.id != widget.item.id) {
      ++_generation;
      _replyDraft = '';
      _reacting = false;
    }
    if (!identical(oldWidget.item, widget.item)) _item = widget.item;
  }

  late CommentItem _item;
  String _replyDraft = '';
  bool _threadOpen = false;
  bool _reacting = false;

  @override
  void initState() {
    super.initState();
    _item = widget.item;
  }

  Future<void> _react(String type) async {
    if (_reacting) return;
    if (!widget.signedIn) {
      widget.onSignIn?.call();
      return;
    }
    final generation = _generation;
    setState(() => _reacting = true);
    try {
      final response = await widget.content.reactComment(
        _item.id,
        _item.myReaction == type ? null : type,
      );
      if (!mounted || generation != _generation) return;
      setState(() {
        _item = _item.copyWith(
          likes: asInt(response['likes']),
          dislikes: asInt(response['dislikes']),
          myReaction: response['myReaction'] as String?,
          clearReaction: response['myReaction'] == null,
        );
      });
      widget.onUpdated?.call(_item);
    } catch (error) {
      if (mounted && generation == _generation) _showError(error);
    } finally {
      if (mounted && generation == _generation) {
        setState(() => _reacting = false);
      }
    }
  }

  void _showError(Object error) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          error is ApiFailure
              ? AppStrings.apiError(error)
              : AppStrings.t('common.networkError'),
        ),
      ),
    );
  }

  Future<void> _reply() async {
    if (!widget.signedIn) {
      widget.onSignIn?.call();
      return;
    }
    if (widget.onReplyRequest != null) {
      widget.onReplyRequest!();
      return;
    }
    await _openThread(compose: true);
  }

  Future<void> _threadCreatorState(
    CommentItem item,
    ValueChanged<CommentItem> onUpdated, {
    bool pin = false,
    bool hide = false,
  }) async {
    final generation = _generation;
    try {
      final updated = hide
          ? await widget.content.setCommentVisibility(
              item.id,
              item.status != 'hidden',
            )
          : await widget.content.setCommentCreatorState(
              item.id,
              enabled: pin ? !item.isPinned : !item.hasCreatorHeart,
              pin: pin,
            );
      if (mounted && generation == _generation) onUpdated(updated);
    } catch (error) {
      if (mounted && generation == _generation) _showError(error);
    }
  }

  Future<void> _openThread({bool compose = false}) async {
    if (_threadOpen) return;
    final generation = _generation;
    _threadOpen = true;
    HapticFeedback.lightImpact();
    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      backgroundColor: Theme.of(context).colorScheme.surface,
      barrierColor: Colors.transparent,
      showDragHandle: false,
      builder: (_) => CommentThreadSheet(
        auth: widget.auth,
        content: widget.content,
        parent: _item,
        focusComposer: compose,
        initialDraft: _replyDraft,
        onDraftChanged: (value) => _replyDraft = value,
        onParentChanged: (item) {
          if (!mounted || generation != _generation) return;
          setState(() => _item = item);
          widget.onUpdated?.call(item);
        },
        tileBuilder: (item, onUpdated, onReply) => _CommentTile(
          key: ValueKey(
            item.id == _item.id ? 'thread-parent-${item.id}' : item.id,
          ),
          item: item,
          content: widget.content,
          signedIn: widget.signedIn,
          auth: widget.auth,
          channelName: widget.channelName,
          isOwner: widget.isOwner,
          isPinned: item.isPinned,
          isHearted: item.hasCreatorHeart,
          showReplies: item.id != _item.id,
          onReplyRequest: onReply,
          depth: item.id == _item.id ? widget.depth : widget.depth + 1,
          onUpdated: onUpdated,
          onSignIn: widget.onSignIn,
          onTogglePin: () => _threadCreatorState(item, onUpdated, pin: true),
          onToggleHeart: () => _threadCreatorState(item, onUpdated),
          onToggleHide: () => _threadCreatorState(item, onUpdated, hide: true),
        ),
      ),
    );
    _threadOpen = false;
  }

  Future<void> _edit() async {
    final generation = _generation;
    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => CommentEditorSheet(
        initialValue: _item.content,
        onSubmit: (text) async {
          final updated = await widget.content.updateComment(_item.id, text);
          if (!mounted || generation != _generation) {
            throw ApiFailure(
              401,
              'SESSION_EXPIRED',
              AppStrings.t('auth.sessionExpired'),
            );
          }
          setState(() => _item = updated);
          widget.onUpdated?.call(_item);
        },
      ),
    );
  }

  Future<void> _delete() async {
    if (_item.status == 'deleted' ||
        _item.userId == null ||
        _item.userId != widget.auth.user?['userId']) {
      return;
    }
    final generation = _generation;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => ConfirmationDialog(
        title: AppStrings.t('watch.deleteCommentTitle'),
        message: AppStrings.t('watch.deleteCommentDescription'),
        confirmLabel: AppStrings.t('common.delete'),
        destructive: true,
        icon: Icons.delete_outline_rounded,
      ),
    );
    if (confirmed != true || generation != _generation) return;
    try {
      await widget.content.deleteComment(_item.id);
      if (mounted && generation == _generation) {
        setState(
          () => _item = CommentItem(
            id: _item.id,
            videoId: _item.videoId,
            displayName: _item.displayName,
            content: AppStrings.t('watch.commentDeleted'),
            createdAt: _item.createdAt,
            likes: _item.likes,
            dislikes: _item.dislikes,
            myReaction: null,
            replyCount: 0,
            userId: _item.userId,
            status: 'deleted',
          ),
        );
        widget.onUpdated?.call(_item);
      }
    } on ApiFailure catch (error) {
      if (mounted && generation == _generation) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(AppStrings.apiError(error))));
      }
    }
  }

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 12),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (widget.isPinned || _item.isPinned)
          Padding(
            padding: const EdgeInsets.only(left: 43, bottom: 4),
            child: Row(
              children: [
                const Icon(
                  Icons.push_pin_rounded,
                  size: 14,
                  color: AppColors.primaryPink,
                ),
                const SizedBox(width: 4),
                Text(
                  AppStrings.format('watch.pinnedBy', {
                    'channel': widget.channelName.isEmpty
                        ? AppStrings.t('channel.owner')
                        : widget.channelName,
                  }),
                  style: const TextStyle(
                    fontSize: 12,
                    fontWeight: FontWeight.w600,
                    color: AppColors.primaryPink,
                  ),
                ),
              ],
            ),
          ),
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            CircleAvatar(
              radius: 18,
              backgroundColor: Theme.of(context).colorScheme.primaryContainer,
              foregroundColor: Theme.of(context).colorScheme.onPrimaryContainer,
              child: Text(
                _item.displayName.isEmpty
                    ? 'H'
                    : _item.displayName.substring(0, 1),
              ),
            ),
            const SizedBox(width: 9),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Wrap(
                    spacing: 8,
                    runSpacing: 4,
                    children: [
                      Text(
                        _item.displayName,
                        style: const TextStyle(fontWeight: FontWeight.w700),
                      ),
                      if (_item.createdAt != null)
                        Text(
                          AppStrings.relativeDate(_item.createdAt!),
                          style: Theme.of(context).textTheme.bodySmall
                              ?.copyWith(
                                color: Theme.of(
                                  context,
                                ).colorScheme.onSurfaceVariant,
                              ),
                        ),
                      if (_item.status == 'hidden') ...[
                        const SizedBox(width: 8),
                        Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 6,
                            vertical: 1,
                          ),
                          decoration: BoxDecoration(
                            color: Colors.amber.withValues(alpha: 0.2),
                            borderRadius: BorderRadius.circular(4),
                          ),
                          child: Text(
                            AppStrings.t('watch.hideComment'),
                            style: const TextStyle(
                              fontSize: 10,
                              color: Colors.amber,
                              fontWeight: FontWeight.w600,
                            ),
                          ),
                        ),
                      ],
                    ],
                  ),
                  const SizedBox(height: 3),
                  Text(
                    _item.content,
                    style: TextStyle(
                      height: 1.5,
                      color: _item.status == 'hidden'
                          ? Theme.of(
                              context,
                            ).colorScheme.onSurface.withValues(alpha: 0.5)
                          : null,
                    ),
                  ),
                  if (_item.status != 'deleted')
                    Wrap(
                      spacing: 4,
                      runSpacing: 4,
                      crossAxisAlignment: WrapCrossAlignment.center,
                      children: [
                        TextButton.icon(
                          key: ValueKey('comment-like-${_item.id}'),
                          onPressed: _reacting ? null : () => _react('like'),
                          style: TextButton.styleFrom(
                            minimumSize: const Size(48, 48),
                            foregroundColor: _item.myReaction == 'like'
                                ? Theme.of(context).colorScheme.primary
                                : Theme.of(
                                    context,
                                  ).colorScheme.onSurfaceVariant,
                          ),
                          icon: Icon(
                            _item.myReaction == 'like'
                                ? Icons.thumb_up_rounded
                                : Icons.thumb_up_outlined,
                            size: 18,
                          ),
                          label: Text(
                            AppStrings.compactLikeCount(_item.likes),
                            semanticsLabel:
                                '${AppStrings.t('watch.likeComment')}: ${AppStrings.number(_item.likes)}',
                          ),
                        ),
                        IconButton(
                          key: ValueKey('comment-dislike-${_item.id}'),
                          tooltip: AppStrings.t('watch.dislikeComment'),
                          isSelected: _item.myReaction == 'dislike',
                          icon: const Icon(Icons.thumb_down_outlined, size: 18),
                          selectedIcon: const Icon(
                            Icons.thumb_down_rounded,
                            size: 18,
                          ),
                          onPressed: _reacting ? null : () => _react('dislike'),
                        ),
                        TextButton(
                          onPressed: _reply,
                          child: Text(AppStrings.t('common.reply')),
                        ),
                        if (widget.isHearted || _item.hasCreatorHeart)
                          Container(
                            margin: const EdgeInsets.only(left: 4),
                            padding: const EdgeInsets.symmetric(
                              horizontal: 6,
                              vertical: 3,
                            ),
                            decoration: BoxDecoration(
                              color: Colors.red.withValues(alpha: 0.12),
                              borderRadius: BorderRadius.circular(12),
                            ),
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                const Icon(
                                  Icons.favorite_rounded,
                                  size: 13,
                                  color: Colors.redAccent,
                                ),
                                const SizedBox(width: 4),
                                CircleAvatar(
                                  radius: 8,
                                  backgroundColor: AppColors.primaryPink,
                                  child: Text(
                                    widget.channelName.isNotEmpty
                                        ? widget.channelName[0].toUpperCase()
                                        : 'H',
                                    style: const TextStyle(
                                      fontSize: 8,
                                      fontWeight: FontWeight.w800,
                                      color: Colors.white,
                                    ),
                                  ),
                                ),
                              ],
                            ),
                          ),

                        if (_item.status != 'deleted' &&
                            ((_item.userId != null &&
                                    _item.userId ==
                                        widget.auth.user?['userId']) ||
                                widget.isOwner))
                          PopupMenuButton<String>(
                            tooltip: AppStrings.t('watch.commentOptions'),
                            onSelected: (choice) {
                              if (choice == 'pin') widget.onTogglePin();
                              if (choice == 'heart') widget.onToggleHeart();
                              if (choice == 'hide') widget.onToggleHide();
                              if (choice == 'edit') _edit();
                              if (choice == 'delete') _delete();
                            },
                            itemBuilder: (_) => [
                              if (widget.isOwner) ...[
                                if (widget.depth == 0)
                                  PopupMenuItem(
                                    value: 'pin',
                                    child: Row(
                                      children: [
                                        Icon(
                                          widget.isPinned
                                              ? Icons.push_pin_rounded
                                              : Icons.push_pin_outlined,
                                          size: 18,
                                        ),
                                        const SizedBox(width: 8),
                                        Text(
                                          widget.isPinned
                                              ? AppStrings.t(
                                                  'watch.unpinComment',
                                                )
                                              : AppStrings.t(
                                                  'watch.pinComment',
                                                ),
                                        ),
                                      ],
                                    ),
                                  ),
                                PopupMenuItem(
                                  value: 'heart',
                                  child: Row(
                                    children: [
                                      Icon(
                                        widget.isHearted
                                            ? Icons.favorite_rounded
                                            : Icons.favorite_border_rounded,
                                        size: 18,
                                        color: Colors.redAccent,
                                      ),
                                      const SizedBox(width: 8),
                                      Text(
                                        widget.isHearted
                                            ? AppStrings.t(
                                                'watch.unheartComment',
                                              )
                                            : AppStrings.t(
                                                'watch.heartComment',
                                              ),
                                      ),
                                    ],
                                  ),
                                ),
                                PopupMenuItem(
                                  value: 'hide',
                                  child: Row(
                                    children: [
                                      Icon(
                                        _item.status == 'hidden'
                                            ? Icons.visibility_rounded
                                            : Icons.visibility_off_outlined,
                                        size: 18,
                                      ),
                                      const SizedBox(width: 8),
                                      Text(
                                        _item.status == 'hidden'
                                            ? AppStrings.t(
                                                'watch.unhideComment',
                                              )
                                            : AppStrings.t('watch.hideComment'),
                                      ),
                                    ],
                                  ),
                                ),
                              ],
                              if (_item.userId != null &&
                                  _item.userId ==
                                      widget.auth.user?['userId']) ...[
                                PopupMenuItem(
                                  value: 'edit',
                                  child: Row(
                                    children: [
                                      const Icon(Icons.edit_outlined, size: 18),
                                      const SizedBox(width: 8),
                                      Text(AppStrings.t('common.edit')),
                                    ],
                                  ),
                                ),
                                PopupMenuItem(
                                  value: 'delete',
                                  child: Row(
                                    children: [
                                      const Icon(
                                        Icons.delete_outline,
                                        size: 18,
                                        color: Colors.redAccent,
                                      ),
                                      const SizedBox(width: 8),
                                      Text(AppStrings.t('common.delete')),
                                    ],
                                  ),
                                ),
                              ],
                            ],
                          )
                        else if (widget.signedIn && _item.status != 'deleted')
                          IconButton(
                            tooltip: AppStrings.t('watch.reportComment'),
                            onPressed: () => showContentReportDialog(
                              context,
                              auth: widget.auth,
                              targetType: 'comment',
                              targetId: _item.id,
                            ),
                            icon: const Icon(Icons.flag_outlined, size: 18),
                          ),
                      ],
                    ),
                ],
              ),
            ),
          ],
        ),
        if (widget.showReplies && _item.replyCount > 0)
          Align(
            alignment: Alignment.centerLeft,
            child: TextButton.icon(
              key: ValueKey('comment-replies-${_item.id}'),
              onPressed: () => _openThread(),
              icon: const Icon(
                Icons.subdirectory_arrow_right_rounded,
                size: 20,
              ),
              label: Text(
                AppStrings.format('watch.replies', {
                  'count': AppStrings.number(_item.replyCount),
                }),
              ),
            ),
          ),
      ],
    ),
  );
}
