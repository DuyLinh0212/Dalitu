import 'dart:io';
import '../../core/media/picture_in_picture.dart';
import 'package:better_native_video_player/better_native_video_player.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../core/localization/app_strings.dart';
import '../../core/theme/app_theme.dart';
import 'playback_session.dart';

class MiniPlayer extends StatefulWidget {
  const MiniPlayer({super.key, required this.session});
  final PlaybackSession session;

  @override
  State<MiniPlayer> createState() => _MiniPlayerState();
}

class _MiniPlayerState extends State<MiniPlayer> {
  Alignment _alignment = Alignment.bottomRight;
  final _surfaceKey = GlobalKey();
  NativeVideoPlayer? _nativeSurface;
  @override
  void initState() {
    super.initState();
    if (Platform.isAndroid) AppPictureInPicture.bindFallback(this, _preparePip);
  }

  @override
  void dispose() {
    AppPictureInPicture.unbind(this);
    super.dispose();
  }

  Future<bool> _preparePip() async {
    if (!mounted ||
        !session.minimized ||
        !session.automaticPipEligible ||
        ModalRoute.of(context)?.isCurrent != true) {
      return false;
    }
    final video = session.videoId;
    final ready = await AppPictureInPicture.prepareSurface(_surfaceKey);
    return ready &&
        mounted &&
        session.videoId == video &&
        session.automaticPipEligible;
  }

  Widget _surface(NativeVideoPlayerController player) {
    if (_nativeSurface?.controller != player) {
      _nativeSurface = NativeVideoPlayer(controller: player);
    }
    return KeyedSubtree(
      key: _surfaceKey,
      child: PipVideoSurface(child: _nativeSurface!),
    );
  }

  PlaybackSession get session => widget.session;

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: Listenable.merge([session, AppPictureInPicture.active]),
    builder: (context, _) {
      final player = session.player;
      final videoId = session.videoId;
      if (!session.minimized || player == null || videoId == null) {
        return const SizedBox.shrink();
      }
      if (AppPictureInPicture.active.value) {
        return ColoredBox(
          color: Colors.black,
          child: Center(
            child: AspectRatio(
              aspectRatio: player.videoSize?.aspectRatio ?? 16 / 9,
              child: _surface(player),
            ),
          ),
        );
      }
      final width = (MediaQuery.sizeOf(context).width * .64).clamp(
        205.0,
        270.0,
      );
      return LayoutBuilder(
        builder: (context, constraints) {
          final actualWidth = width.clamp(0.0, constraints.maxWidth - 24);
          final captionHeight =
              (12 + MediaQuery.textScalerOf(context).scale(26)).clamp(
                48.0,
                double.infinity,
              );
          final horizontalTravel = constraints.maxWidth - actualWidth - 24;
          final verticalTravel =
              constraints.maxHeight - actualWidth * 9 / 16 - captionHeight - 21;
          return Align(
            alignment: _alignment,
            child: GestureDetector(
              key: const ValueKey('mini-player-drag-handle'),
              behavior: HitTestBehavior.opaque,
              onPanUpdate: (details) => setState(() {
                _alignment = Alignment(
                  horizontalTravel > 0
                      ? (_alignment.x + details.delta.dx * 2 / horizontalTravel)
                            .clamp(-1.0, 1.0)
                      : 0,
                  verticalTravel > 0
                      ? (_alignment.y + details.delta.dy * 2 / verticalTravel)
                            .clamp(-1.0, 1.0)
                      : 0,
                );
              }),
              child: Material(
                color: Colors.transparent,
                child: Container(
                  width: actualWidth,
                  margin: const EdgeInsets.fromLTRB(12, 8, 12, 10),
                  clipBehavior: Clip.antiAlias,
                  decoration: BoxDecoration(
                    color: AppColors.ink,
                    borderRadius: BorderRadius.circular(16),
                    border: Border.all(
                      color: Colors.white.withValues(alpha: .12),
                    ),
                    boxShadow: const [
                      BoxShadow(
                        color: Color(0x66000000),
                        blurRadius: 22,
                        offset: Offset(0, 8),
                      ),
                    ],
                  ),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      GestureDetector(
                        key: const ValueKey('mini-player-video'),
                        behavior: HitTestBehavior.opaque,
                        onTap: () {
                          session.restore();
                          context.push('/watch/$videoId');
                        },
                        child: AspectRatio(
                          aspectRatio: 16 / 9,
                          child: Stack(
                            fit: StackFit.expand,
                            children: [
                              IgnorePointer(child: _surface(player)),
                              Positioned(
                                top: 3,
                                right: 3,
                                child: IconButton.filledTonal(
                                  tooltip: AppStrings.t('mini.close'),
                                  onPressed: session.dismiss,
                                  style: IconButton.styleFrom(
                                    backgroundColor: Colors.black.withValues(
                                      alpha: .72,
                                    ),
                                    foregroundColor: Colors.white,
                                    minimumSize: const Size(48, 48),
                                  ),
                                  icon: const Icon(
                                    Icons.close_rounded,
                                    size: 20,
                                  ),
                                ),
                              ),
                              if (!session.ready)
                                const Center(
                                  child: CircularProgressIndicator(
                                    color: AppColors.primaryPink,
                                    strokeWidth: 2,
                                  ),
                                ),
                            ],
                          ),
                        ),
                      ),
                      SizedBox(
                        height: captionHeight,
                        child: Row(
                          children: [
                            const SizedBox(width: 11),
                            Expanded(
                              child: GestureDetector(
                                behavior: HitTestBehavior.opaque,
                                onTap: () {
                                  session.restore();
                                  context.push('/watch/$videoId');
                                },
                                child: Column(
                                  mainAxisAlignment: MainAxisAlignment.center,
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  children: [
                                    Text(
                                      session.title,
                                      maxLines: 1,
                                      overflow: TextOverflow.ellipsis,
                                      style: const TextStyle(
                                        color: Colors.white,
                                        fontSize: 12,
                                        fontWeight: FontWeight.w800,
                                      ),
                                    ),
                                    Text(
                                      session.channelName,
                                      maxLines: 1,
                                      overflow: TextOverflow.ellipsis,
                                      style: const TextStyle(
                                        color: Colors.white60,
                                        fontSize: 10,
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                            ),
                            IconButton(
                              tooltip: AppStrings.t(
                                session.isPlaying ? 'mini.pause' : 'mini.play',
                              ),
                              onPressed: session.togglePlayback,
                              color: Colors.white,
                              icon: Icon(
                                session.isPlaying
                                    ? Icons.pause_rounded
                                    : Icons.play_arrow_rounded,
                              ),
                            ),
                          ],
                        ),
                      ),
                      LinearProgressIndicator(
                        value: session.duration.inMilliseconds <= 0
                            ? 0
                            : (session.position.inMilliseconds /
                                      session.duration.inMilliseconds)
                                  .clamp(0.0, 1.0),
                        minHeight: 3,
                        color: AppColors.primaryPink,
                        backgroundColor: Colors.white12,
                      ),
                    ],
                  ),
                ),
              ),
            ),
          );
        },
      );
    },
  );
}
