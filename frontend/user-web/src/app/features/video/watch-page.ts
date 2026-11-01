import { ResultNoticeComponent } from '@dalitu/ui/result-notice.component';
import { confirmChannelUnsubscribe } from '../../shared/subscription-confirmation';
import { NgTemplateOutlet } from '@angular/common';
import { VideoDownloadService } from '../../core/video-download.service';
import { FeedbackService } from '@dalitu/ui/feedback.service';
import { Component, DestroyRef, ElementRef, HostListener, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { finalize, Subject, switchMap, takeUntil } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { LoginPromptService } from '../../core/login-prompt.service';
import { RequireLoginDirective } from '../../shared/login-prompt/require-login.directive';
import { VideoDescriptionComponent } from '../../shared/video-description/video-description.component';
import { AuthService, errorMessage } from '../../core/auth.service';
import { ChannelDetail, ChannelService } from '../../core/channel.service';
import { CommentItem, ContentService, Playback, Rendition, VideoCard, VideoCardLink, VideoDetail, ViolationType } from '../../core/content.service';
import { I18nService } from '../../core/i18n.service';
import { LocaleDatePipe } from '../../core/locale-date.pipe';
import { CompactLikeCountPipe } from '../../core/locale-number.pipe';
import { TranslatePipe } from '../../core/translate.pipe';
import { PlaylistItem, PlaylistService, PlaylistSummary } from '../../core/playlist.service';
import { MyPlan, PlanService } from '../../core/plan.service';
import { PictureInPictureService } from '../../core/picture-in-picture.service';
import { PlayerPreferencesService } from '../../core/player-preferences.service';
import { ReportModalComponent } from '../../shared/report-modal/report-modal.component';
import { VideoPlaylistMenuComponent } from '../../shared/video-playlist-menu/video-playlist-menu.component';
import { InfiniteScrollDirective } from '../../shared/infinite-scroll.directive';
import { PlayerContextMenu } from './player-context-menu';
import { WATCH_TRANSLATIONS } from './watch-translations';

@Component({
  selector: 'app-watch-page',
  imports: [NgTemplateOutlet, ResultNoticeComponent, RequireLoginDirective, VideoDescriptionComponent, LocaleDatePipe, CompactLikeCountPipe, FormsModule, RouterLink, TranslatePipe, ReportModalComponent, VideoPlaylistMenuComponent, InfiniteScrollDirective, PlayerContextMenu],
  templateUrl: './watch-page.html',
  styleUrl: './watch-page.scss'
})
export class WatchPage {
  private readonly videoDownload = inject(VideoDownloadService);
  private readonly feedback = inject(FeedbackService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly content = inject(ContentService);
  private readonly channels = inject(ChannelService);
  private readonly playlists = inject(PlaylistService);
  private readonly pictureInPicture = inject(PictureInPictureService);
  private readonly playerPreferences = inject(PlayerPreferencesService);
  private readonly plans = inject(PlanService);
  readonly pipAllowed = signal(false);
  readonly backgroundPlayAllowed = signal(false);
  private entitlementExpiryTimer: ReturnType<typeof setTimeout> | null = null;
  readonly editingCommentId = signal<string | null>(null);
  readonly commentEditBusy = signal(false);
  commentEditText = "";
  readonly i18n = inject(I18nService);
  readonly auth = inject(AuthService);
  private readonly loginPrompt = inject(LoginPromptService);

  readonly video = signal<VideoDetail | null>(null);
  readonly playback = signal<Playback | null>(null);
  readonly activeRendition = signal<Rendition | null>(null);
  readonly relatedVideos = signal<VideoCard[]>([]);
  readonly recommendationsLoading = signal(false);
  readonly recommendationsError = signal(false);
  readonly recommendationsMoreError = signal(false);
  readonly recommendationsHasMore = signal(false);
  readonly channel = signal<ChannelDetail | null>(null);
  readonly comments = signal<CommentItem[]>([]);
  readonly commentSort = signal<'newest' | 'top'>('newest');
  readonly commentPage = signal(1);
  readonly commentTotal = signal(0);
  readonly commentsLoading = signal(false);
  readonly commentsError = signal('');
  readonly repliesByComment = signal<Record<string, CommentItem[]>>({});
  readonly expandedReplies = signal<Record<string, boolean>>({});
  readonly repliesLoading = signal<Record<string, boolean>>({});
  readonly repliesError = signal<Record<string, string>>({});
  readonly replyPages = signal<Record<string, number>>({});
  readonly replyTotals = signal<Record<string, number>>({});
  readonly visibleReplyCounts = signal<Record<string, number>>({});
  readonly replyBusy = signal<string | null>(null);
  readonly replyError = signal('');
  readonly commentError = signal('');
  private readonly repliesPageSize = 3;
  readonly quality = signal('');
  readonly speed = signal(1);
  readonly error = signal('');
  readonly loading = signal(true);
  readonly replyTo = signal<string | null>(null);
  readonly myChannelId = signal('');
  readonly violationTypes = signal<ViolationType[]>([]);
  readonly actionMessage = signal('');
  readonly autoplay = signal(true);
  readonly recommendationFilter = signal<'all' | 'related' | 'channel'>('all');
  readonly isPlaying = signal(false);
  readonly muted = signal(this.playerPreferences.preferences().muted);
  readonly volume = signal(this.playerPreferences.preferences().volume);
  readonly currentTime = signal(0);
  readonly totalDuration = signal(0);
  readonly seekFeedback = signal<'backward' | 'forward' | null>(null);
  readonly videoCardDrawer = signal<VideoCardLink | null>(null);
  readonly videoInfoOpen = signal(false);
  readonly dismissedVideoCardIds = signal<string[]>([]);
  readonly videoCardLinks = computed(() => [...(this.video()?.videoCards ?? [])].sort((a, b) => a.startSeconds - b.startSeconds));
  readonly activeVideoCard = computed(() => {
    if (this.videoCardDrawer() || this.videoInfoOpen()) return null;
    const current = this.currentTime();
    const visibleCards = (this.video()?.videoCards ?? []).filter(card => current >= card.startSeconds
      && current < card.startSeconds + this.videoCardDisplaySeconds
      && !this.dismissedVideoCardIds().includes(card.videoId));
    // If two cards are configured close together, the newest cue wins instead
    // of keeping the previous banner on screen over the next cue.
    return visibleCards.sort((a, b) => b.startSeconds - a.startSeconds)[0] ?? null;
  });
  readonly progressPercent = computed(() => {
    const duration = this.totalDuration();
    if (duration <= 0) return 0;
    return Math.min(100, Math.max(0, (this.currentTime() / duration) * 100));
  });
  readonly pipActive = this.pictureInPicture.active;
  readonly pipEntering = this.pictureInPicture.entering;
  readonly shareOpen = signal(false);
  readonly shareUrl = signal('');
  readonly downloadOptions = signal<Rendition[]>([]);
  readonly downloadOpen = signal(false);
  readonly downloadBusy = signal(false);
  readonly playlistPickerOpen = signal(false);
  readonly playlistOptions = signal<PlaylistSummary[]>([]);
  readonly playlistLoading = signal(false);
  readonly playlistAddingId = signal<string | null>(null);
  readonly addedPlaylistIds = signal<Record<string, boolean>>({});
  readonly subscribed = signal(false);
  readonly subscriptionBusy = signal(false);
  readonly subscriptionNotificationsEnabled = signal(false);
  readonly subscriberCount = signal(0);
  readonly visibleRelatedVideos = computed(() => {
    return this.relatedVideos();
  });
  readonly speeds = [.25, .5, .75, 1, 1.25, 1.5, 1.75, 2];

  commentText = '';
  replyText = '';
  private readonly destroyRef = inject(DestroyRef);
  private readonly videoChanged = new Subject<void>();
  private readonly commentsChanged = new Subject<void>();
  private readonly recommendationsChanged = new Subject<void>();
  private recommendationVersion = 0;
  private recommendationNextPage = 1;
  private recommendationSource: 'feed' | 'channel' | 'tag' | 'category' = 'feed';
  private playlistId: string | null = null;
  private videoId = '';
  private lastSaved = 0;
  private pendingSeek: number | null = null;
  private resumeApplied = false;
  private continuePlaying = false;
  private autoPlayAttempted = false;
  private autoplayMuted = false;
  private readonly videoCardDisplaySeconds = 8;
  private playerTapTimer: ReturnType<typeof setTimeout> | null = null;
  private lastPlayerTapAt = 0;
  private lastPlayerTapX = 0;
  private seekFeedbackTimer: ReturnType<typeof setTimeout> | null = null;
  readonly playlistQueue = signal<PlaylistItem[]>([]);
  private playlistIndex = -1;

  @ViewChild('player') playerRef?: ElementRef<HTMLVideoElement>;
  @ViewChild('playerStage') playerStageRef?: ElementRef<HTMLElement>;
  @ViewChild(PlayerContextMenu) playerMenu?: PlayerContextMenu;

  constructor() {
    this.i18n.registerTranslations(WATCH_TRANSLATIONS);
    this.destroyRef.onDestroy(() => {
      this.clearPlayerTapTimer();
      this.clearSeekFeedbackTimer();
      this.clearEntitlementExpiryTimer();
    });

    this.videoId = this.route.snapshot.paramMap.get('id') ?? '';
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(params => {
      const id = params.get('id') ?? '';
      if (id && id !== this.videoId) {
        this.videoId = id;
        this.resetPlayerState();
        this.loadPageData();
      }
    });

    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(params => {
      const start = params.get('t');
      if (start !== null && /^\d+$/.test(start)) {
        const seconds = Number(start);
        if (Number.isSafeInteger(seconds)) {
          this.resumeApplied = true;
          const player = this.playerRef?.nativeElement;
          if (player && player.readyState >= 1) {
            player.currentTime = Math.min(seconds, Number.isFinite(player.duration) ? player.duration : seconds);
          } else this.pendingSeek = seconds;
        }
      }
      const playlistId = params.get('playlist');
      this.playlistIndex = Number(params.get('index') ?? -1);
      if (playlistId !== this.playlistId) {
        this.playlistId = playlistId;
        this.playlistQueue.set([]);
        if (playlistId) this.playlists.get(playlistId).subscribe({
          next: playlist => { if (this.playlistId === playlistId) this.playlistQueue.set(playlist.items); },
          error: () => { if (this.playlistId === playlistId) this.playlistQueue.set([]); }
        });
      }
    });

    // Restore the refresh-cookie session before loading the detail/comments.
    // Otherwise a hard refresh sends these public requests anonymously and the
    // API cannot include the current user's reaction, rating, or comment votes.
    this.auth.restore().pipe(
      switchMap(() => this.playerPreferences.load()),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: () => { this.restorePlayerAudio(); this.loadPageData(); },
      error: () => this.loadPageData()
    });
  }

  private resetPlayerState() {
    this.recommendationVersion++;
    this.videoChanged.next();
    this.clearPlayerTapTimer();
    this.clearSeekFeedbackTimer();
    this.lastPlayerTapAt = 0;
    this.repliesByComment.set({});
    this.expandedReplies.set({});
    this.repliesLoading.set({});
    this.resetReplyState();
    this.relatedVideos.set([]);
    this.recommendationsHasMore.set(false);
    this.recommendationsLoading.set(false);
    this.recommendationsError.set(false);
    this.recommendationsMoreError.set(false);
    this.channel.set(null);
    this.loading.set(true);
    this.error.set('');
    this.video.set(null);
    this.videoCardDrawer.set(null);
    this.videoInfoOpen.set(false);
    this.dismissedVideoCardIds.set([]);
    this.playback.set(null);
    this.activeRendition.set(null);
    this.comments.set([]);
    this.commentPage.set(1);
    this.commentTotal.set(0);
    this.commentsLoading.set(false);
    this.commentsError.set('');
    this.subscribed.set(false);
    this.currentTime.set(0);
    this.totalDuration.set(0);
    this.isPlaying.set(false);
    this.lastSaved = 0;
    this.pendingSeek = null;
    this.resumeApplied = false;
    this.continuePlaying = false;
    this.autoPlayAttempted = false;
    this.autoplayMuted = false;
    if (this.playerRef?.nativeElement) {
      this.playerRef.nativeElement.pause();
      this.playerRef.nativeElement.currentTime = 0;
    }
  }

  private loadPageData() {
    this.editingCommentId.set(null);
    this.pipAllowed.set(false);
    this.backgroundPlayAllowed.set(false);
    this.refreshPlaybackEntitlements();
    if (this.auth.user()) {
      this.channels.getMyChannel().pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({ next: channel => this.myChannelId.set(channel.channelId), error: () => {} });
    }
    this.content.violationTypes().pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({ next: types => this.violationTypes.set(types), error: () => {} });

    this.content.detail(this.videoId).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: video => {
        this.video.set(video);
        this.loadRecommendations();
        if (video.videoUrl) {
          this.setAutoplayRendition({ quality: this.i18n.t('watch.sourceQuality'), width: 0, height: 0, fileSize: video.fileSize, url: video.videoUrl });
        }
        this.channels.getChannel(video.channelHandle).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
          next: channel => {
            this.channel.set(channel);
            this.subscriberCount.set(channel.subscriberCount);
            if (this.auth.user()) {
              this.channels.getSubscriptionStatus(channel.channelId).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
                next: sub => {
                  this.subscribed.set(sub.status === 'active');
                  this.subscriptionNotificationsEnabled.set(sub.status === 'active' && sub.notificationsEnabled);
                },
                error: () => {
                  this.subscribed.set(false);
                  this.subscriptionNotificationsEnabled.set(false);
                }
              });
            }
          },
          error: () => {}
        });
        this.loading.set(false);
      },
      error: () => {
        this.error.set(this.i18n.t('watch.unavailableError'));
        this.loading.set(false);
      }
    });

    this.content.playback(this.videoId).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: playback => {
        this.playback.set(playback);
        this.totalDuration.set(playback.duration);
        if (!this.resumeApplied && playback.resumeAtSeconds > 0) this.currentTime.set(playback.resumeAtSeconds);
        const best = playback.renditions.reduce<Rendition | null>((current, item) => !current || item.height > current.height ? item : current, null);
        if (best) {
          this.quality.set(best.quality);
          this.setAutoplayRendition(best);
        }
      },
      error: () => this.actionMessage.set(this.i18n.t('watch.playbackQualityError'))
    });

    this.loadComments();
  }

  private loadComments(page = 1) {
    if (page > 1 && (this.commentsLoading() || this.comments().length >= this.commentTotal())) return;
    const requestVideoId = this.videoId;
    const requestSort = this.commentSort();
    this.commentsLoading.set(true);
    this.commentsError.set('');
    this.content.comments(requestVideoId, page, 20, requestSort).pipe(
      takeUntil(this.videoChanged),
      takeUntil(this.commentsChanged),
      takeUntilDestroyed(this.destroyRef),
      finalize(() => {
        if (requestVideoId === this.videoId && requestSort === this.commentSort()) this.commentsLoading.set(false);
      })
    ).subscribe({
      next: value => {
        if (requestVideoId !== this.videoId || requestSort !== this.commentSort()) return;
        const items = value.items ?? [];
        this.comments.set(page === 1 ? items : [...new Map([...this.comments(), ...items].map(item => [item.commentId, item])).values()]);
        this.commentPage.set(page);
        this.commentTotal.set(value.total);
        // Keep existing replies visible after navigating directly to a video.
        for (const comment of items.filter(item => item.replyCount > 0
          && !Object.prototype.hasOwnProperty.call(this.repliesByComment(), item.commentId))) this.loadReplies(comment, true);
      },
      error: () => {
        if (requestVideoId !== this.videoId || requestSort !== this.commentSort()) return;
        if (page === 1) this.comments.set([]);
        this.commentsError.set(this.i18n.t('watch.commentsLoadError'));
      }
    });
  }

  loadMoreComments() { this.loadComments(this.commentPage() + 1); }

  retryComments() { this.loadComments(this.comments().length ? this.commentPage() + 1 : 1); }

  changeQuality(value: string) {
    const player = this.playerRef?.nativeElement;
    const rendition = this.playback()?.renditions.find(item => item.quality === value);
    if (!rendition) return;
    if (player) {
      this.pendingSeek = player.currentTime;
      this.continuePlaying = !player.paused;
    }
    this.quality.set(value);
    this.activeRendition.set(rendition);
  }

  onMetadata() {
    const player = this.playerRef?.nativeElement;
    if (!player) return;
    this.restorePlayerAudio();
    this.totalDuration.set(Number.isFinite(player.duration) ? player.duration : this.playback()?.duration ?? 0);
    if (this.pendingSeek !== null) {
      player.currentTime = Math.min(this.pendingSeek, player.duration || this.pendingSeek);
      this.pendingSeek = null;
      this.resumeApplied = true;
    } else if (!this.resumeApplied && (this.playback()?.resumeAtSeconds ?? 0) > 0) {
      const resume = this.playback()!.resumeAtSeconds;
      const duration = Number.isFinite(player.duration) ? player.duration : this.playback()?.duration ?? 0;
      player.currentTime = (duration > 0 && resume >= duration - 3) ? 0 : resume;
      this.resumeApplied = true;
    }
    player.playbackRate = this.speed();
    if (this.continuePlaying) {
      this.continuePlaying = false;
      void player.play();
    }
    if (!this.autoPlayAttempted) {
      this.autoPlayAttempted = true;
      this.tryAutoplay(player);
    }
    this.pictureInPicture.prepare(player);
    this.currentTime.set(player.currentTime);
  }

  private tryAutoplay(player: HTMLVideoElement) {
    const videoId = this.videoId;
    const source = this.activeRendition()?.url;
    void player.play().catch(() => {
      if (videoId !== this.videoId || source !== this.activeRendition()?.url || player !== this.playerRef?.nativeElement) return;
      // Browsers commonly reject audible autoplay. A muted fallback still
      // starts the requested video without making sound unexpectedly.
      this.autoplayMuted = true;
      player.muted = true;
      this.muted.set(true);
      return player.play().catch(() => {
        this.actionMessage.set(this.i18n.t('watch.autoplayBlocked'));
      });
    });
  }

  private setAutoplayRendition(rendition: Rendition) {
    if (this.activeRendition()?.url !== rendition.url) {
      const player = this.playerRef?.nativeElement;
      if (player && Number.isFinite(player.currentTime)) {
        this.pendingSeek = player.currentTime;
        this.continuePlaying = !player.paused;
      }
      this.autoPlayAttempted = false;
    }
    this.activeRendition.set(rendition);
  }

  onPlayerError() {
    const player = this.playerRef?.nativeElement;
    const current = this.activeRendition();
    const lowerRendition = (this.playback()?.renditions ?? [])
      .filter(item => item.url !== current?.url && (current?.height ? item.height < current.height : true))
      .sort((a, b) => b.height - a.height)[0];

    if (lowerRendition) {
      if (player && Number.isFinite(player.currentTime)) this.pendingSeek = player.currentTime;
      this.continuePlaying = true;
      this.quality.set(lowerRendition.quality);
      this.setAutoplayRendition(lowerRendition);
      this.actionMessage.set(this.i18n.t('watch.lowerRenditionFallback'));
      return;
    }

    const source = this.video();
    if (source?.videoUrl && source.videoUrl !== current?.url) {
      if (player && Number.isFinite(player.currentTime)) this.pendingSeek = player.currentTime;
      this.continuePlaying = true;
      this.setAutoplayRendition({ quality: this.i18n.t('watch.sourceQuality'), width: 0, height: 0, fileSize: source.fileSize, url: source.videoUrl });
      this.actionMessage.set(this.i18n.t('watch.sourceFallback'));
      return;
    }
    this.actionMessage.set(this.i18n.t('watch.renditionError'));
  }

  changeSpeed(value: number | string) {
    const next = Number(value);
    this.speed.set(next);
    if (this.playerRef) this.playerRef.nativeElement.playbackRate = next;
  }

  togglePlayback() {
    if (this.pipActive()) return;
    const player = this.playerRef?.nativeElement;
    if (!player) return;
    if (player.paused) {
      void player.play().catch(() => this.actionMessage.set(this.i18n.t('watch.playError')));
    } else {
      player.pause();
    }
  }

  onPlaybackState(playing: boolean) {
    if (playing && this.pipActive()) {
      this.playerRef?.nativeElement.pause();
      this.isPlaying.set(false);
      return;
    }
    this.isPlaying.set(playing);
    if (playing) this.enforceBackgroundPlayback();
  }

  @HostListener('document:visibilitychange')
  onVisibilityChange() {
    this.enforceBackgroundPlayback();
    this.refreshPlaybackEntitlements();
  }

  @HostListener('window:focus')
  onWindowFocus() {
    this.refreshPlaybackEntitlements();
  }

  private clearEntitlementExpiryTimer() {
    if (this.entitlementExpiryTimer !== null) clearTimeout(this.entitlementExpiryTimer);
    this.entitlementExpiryTimer = null;
  }

  private refreshPlaybackEntitlements() {
    if (!this.auth.user()) { this.applyPlaybackEntitlements(null); return; }
    this.plans.getMyPlan().pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: plan => this.applyPlaybackEntitlements(plan),
      error: () => this.applyPlaybackEntitlements(null)
    });
  }

  private applyPlaybackEntitlements(plan: MyPlan | null) {
    this.clearEntitlementExpiryTimer();
    const endedAt = plan?.subscription?.endedAt;
    const remaining = endedAt ? Date.parse(endedAt) - Date.now() : null;
    const valid = remaining === null || remaining > 0;
    this.pipAllowed.set(valid && plan?.features?.['pip'] === true);
    this.backgroundPlayAllowed.set(valid && plan?.features?.['background_play'] === true);
    if (!this.pipAllowed() && this.pictureInPicture.active()) {
      void this.pictureInPicture.exit().finally(() => this.enforceBackgroundPlayback());
    }
    this.enforceBackgroundPlayback();
    if (remaining !== null && remaining > 0) {
      this.entitlementExpiryTimer = setTimeout(() => {
        this.applyPlaybackEntitlements(null);
        this.refreshPlaybackEntitlements();
      }, Math.min(remaining, 2_147_483_647));
    }
  }

  private enforceBackgroundPlayback() {
    if (document.hidden && !this.backgroundPlayAllowed()) this.playerRef?.nativeElement.pause();
  }

  playlistTarget(direction: -1 | 1): PlaylistItem | undefined {
    const queue = this.playlistQueue();
    const index = queue.findIndex(item => item.videoId === this.videoId);
    if (index < 0) return undefined;
    if (direction === 1) return queue.slice(index + 1).find(item => item.available);
    return queue.slice(0, index).reverse().find(item => item.available);
  }

  movePlaylist(direction: -1 | 1) {
    const target = this.playlistTarget(direction);
    if (!target || !this.playlistId) return;
    void this.router.navigate(['/watch', target.videoId], { queryParams: {
      playlist: this.playlistId, index: this.playlistQueue().findIndex(item => item.playlistVideoId === target.playlistVideoId)
    } });
  }

  onEnded() {
    this.isPlaying.set(false);
    this.currentTime.set(this.totalDuration());
    // A PiP window may keep playing while the user is viewing another tab.
    // Do not silently replace the original watch page when that window ends.
    if (!this.autoplay() || this.pipActive() || document.pictureInPictureElement === this.playerRef?.nativeElement) return;
    const currentIndex = this.playlistQueue().findIndex(item => item.videoId === this.videoId);
    const next = this.playlistQueue().slice((currentIndex >= 0 ? currentIndex : this.playlistIndex) + 1).find(item => item.available);
    if (next) {
      const nextIndex = this.playlistQueue().findIndex(item => item.playlistVideoId === next.playlistVideoId);
      const tree = this.router.createUrlTree(['/watch', next.videoId], {
        queryParams: { playlist: this.route.snapshot.queryParamMap.get('playlist'), index: nextIndex }
      });
      void this.router.navigateByUrl(tree);
      return;
    }
    if (this.route.snapshot.queryParamMap.has('playlist')) return;
    const following = this.visibleRelatedVideos()[0];
    if (following && following.videoId !== this.videoId) void this.router.navigateByUrl(this.router.createUrlTree(['/watch', following.videoId]));
  }

  toggleMute() {
    const player = this.playerRef?.nativeElement;
    if (!player) return;
    this.autoplayMuted = false;
    player.muted = !player.muted;
    this.muted.set(player.muted);
    this.playerPreferences.update(player.volume, player.muted);
  }

  setVolume(value: number | string) {
    const next = Math.max(0, Math.min(1, Number(value)));
    if (!Number.isFinite(next)) return;
    const player = this.playerRef?.nativeElement;
    if (!player) return;
    this.autoplayMuted = false;
    player.volume = next;
    player.muted = next === 0;
    this.volume.set(next);
    this.muted.set(player.muted);
    this.playerPreferences.update(next, player.muted);
  }

  onVolumeChange() {
    const player = this.playerRef?.nativeElement;
    if (!player) return;
    this.volume.set(player.volume);
    this.muted.set(player.muted);
    // Browser autoplay can temporarily mute a video. Preserve the user's mute
    // preference while still remembering volume changes from native/PiP controls.
    const muted = this.autoplayMuted && player.muted
      ? this.playerPreferences.preferences().muted : player.muted;
    if (!player.muted) this.autoplayMuted = false;
    this.playerPreferences.update(player.volume, muted);
  }

  private restorePlayerAudio() {
    const preferences = this.playerPreferences.preferences();
    // Keep a temporary autoplay mute while switching quality on this video.
    // Starting another video clears the flag in resetPlayerState().
    const muted = preferences.muted || this.autoplayMuted;
    this.volume.set(preferences.volume);
    this.muted.set(muted);
    const player = this.playerRef?.nativeElement;
    if (player) {
      player.volume = preferences.volume;
      player.muted = muted;
    }
  }

  ratingSelected(item: VideoDetail, star: number) {
    return (item.viewerState?.rating ?? 0) >= star;
  }

  seekFromSlider(value: number | string) {
    const next = Math.max(0, Number(value) || 0);
    const player = this.playerRef?.nativeElement;
    if (!player) return;
    player.currentTime = next;
    this.currentTime.set(next);
  }

  seekBy(seconds: number) {
    const player = this.playerRef?.nativeElement;
    if (!player) return;
    this.seekFromSlider(player.currentTime + seconds);
  }

  onPlayerPointerUp(event: PointerEvent) {
    if (event.pointerType && !['mouse', 'pen', 'touch'].includes(event.pointerType)) return;
    if (event.pointerType === 'mouse' && event.button !== 0) return;
    const player = this.playerRef?.nativeElement;
    if (!player) return;

    const now = performance.now();
    const rect = player.getBoundingClientRect();
    const tapGap = now - this.lastPlayerTapAt;
    const tapDistance = Math.abs(event.clientX - this.lastPlayerTapX);
    const isDoubleTap = tapGap > 0 && tapGap <= 320 && tapDistance <= Math.max(72, rect.width * .12);

    if (isDoubleTap) {
      this.clearPlayerTapTimer();
      this.lastPlayerTapAt = 0;
      const seconds = event.clientX - rect.left < rect.width / 2 ? -10 : 10;
      this.seekBy(seconds);
      this.showSeekFeedback(seconds < 0 ? 'backward' : 'forward');
      return;
    }

    this.lastPlayerTapAt = now;
    this.lastPlayerTapX = event.clientX;
    this.clearPlayerTapTimer();
    this.playerTapTimer = setTimeout(() => {
      this.playerTapTimer = null;
      this.togglePlayback();
    }, 250);
  }

  onPlayerPointerCancel() {
    this.clearPlayerTapTimer();
    this.lastPlayerTapAt = 0;
  }

  private showSeekFeedback(direction: 'backward' | 'forward') {
    this.clearSeekFeedbackTimer();
    this.seekFeedback.set(direction);
    this.seekFeedbackTimer = setTimeout(() => {
      this.seekFeedback.set(null);
      this.seekFeedbackTimer = null;
    }, 650);
  }

  private clearPlayerTapTimer() {
    if (this.playerTapTimer === null) return;
    clearTimeout(this.playerTapTimer);
    this.playerTapTimer = null;
  }

  private clearSeekFeedbackTimer() {
    if (this.seekFeedbackTimer === null) return;
    clearTimeout(this.seekFeedbackTimer);
    this.seekFeedbackTimer = null;
    this.seekFeedback.set(null);
  }

  toggleFullscreen() {
    const element = this.playerStageRef?.nativeElement;
    if (!element) return;
    if (document.fullscreenElement) {
      void document.exitFullscreen();
    } else {
      void element.requestFullscreen();
    }
  }

  supportsPictureInPicture(): boolean {
    const player = this.playerRef?.nativeElement;
    return this.pipAllowed() && this.pictureInPicture.supports(player);
  }

  async togglePictureInPicture(): Promise<void> {
    if (this.pipEntering()) return;
    const player = this.playerRef?.nativeElement;
    if (!this.pipAllowed() && !this.pipActive()) {
      this.actionMessage.set(this.i18n.t('watch.pipNotIncluded'));
      return;
    }
    if (!this.pictureInPicture.supports(player) || !player) {
      this.actionMessage.set(this.i18n.t('watch.pictureInPictureUnsupported'));
      return;
    }

    try {
      if (this.pipActive()) await this.pictureInPicture.exit();
      else await this.pictureInPicture.enter(player);
    } catch {
      this.actionMessage.set(this.i18n.t('watch.pictureInPictureFailed'));
    }
  }

  seekTo(seconds: number) {
    const player = this.playerRef?.nativeElement;
    if (!player) return;
    player.currentTime = Math.max(0, Math.min(seconds, player.duration || seconds));
    if (player.paused) void player.play();
  }

  openVideoCard(card: VideoCardLink): void {
    this.videoInfoOpen.set(false);
    this.playerRef?.nativeElement.pause();
    this.isPlaying.set(false);
    this.videoCardDrawer.set(card);
  }

  toggleVideoInfo(): void {
    if (this.videoCardDrawer()) this.videoCardDrawer.set(null);
    this.videoInfoOpen.update(open => !open);
  }

  closeVideoInfo(): void {
    this.videoInfoOpen.set(false);
  }

  closeVideoCard(): void {
    const card = this.videoCardDrawer();
    if (card) this.dismissedVideoCardIds.update(ids => [...new Set([...ids, card.videoId])]);
    this.videoCardDrawer.set(null);
  }

  openLinkedVideo(): void {
    const target = this.videoCardDrawer();
    if (!target || target.videoId === this.videoId) return;
    this.videoCardDrawer.set(null);
    void this.router.navigate(['/watch', target.videoId]);
  }

  onProgress() {
    const player = this.playerRef?.nativeElement;
    if (!player) return;
    this.currentTime.set(player.currentTime);
    if (!this.auth.user() || this.playback()?.videoId !== this.videoId || !this.activeRendition() || Math.abs(player.currentTime - this.lastSaved) < 10) return;
    this.lastSaved = player.currentTime;
    this.content.progress(this.playback()?.videoId ?? this.video()!.videoId, Math.floor(player.currentTime)).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe();
  }

  react(type: 'like' | 'dislike') {
    if (!this.requireAuthentication(this.i18n.t('watch.loginReaction'))) return;
    const item = this.video();
    if (!item) return;
    const next = item.viewerState?.reaction === type ? null : type;
    this.content.reactVideo(item.videoId, next).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => this.video.set({
        ...item,
        stats: { ...item.stats, likes: result.likes, dislikes: result.dislikes },
        viewerState: { ...this.viewerState(item), reaction: result.myReaction as 'like' | 'dislike' | null }
      }),
      error: error => this.actionMessage.set(this.readError(error) || this.i18n.t('watch.interactionError'))
    });
  }

  rate(score: number | null) {
    if (!this.requireAuthentication(this.i18n.t('watch.loginRating'))) return;
    const item = this.video();
    if (!item) return;
    this.content.rate(item.videoId, score).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => {
        this.video.set({
          ...item,
          stats: {
            ...item.stats,
            averageRating: result.average,
            ratingCount: result.count
          },
          viewerState: { ...this.viewerState(item), rating: result.myRating }
        });
        this.actionMessage.set(this.i18n.t('watch.ratingSaved'));
      },
      error: error => this.actionMessage.set(this.readError(error) || this.i18n.t('watch.loginRating'))
    });
  }

  share() {
    const item = this.video();
    if (!item) return;
    this.shareUrl.set(window.location.href);
    this.shareOpen.set(true);
    // A guest may share the public URL. Only authenticated viewers can persist
    // the share event because the API endpoint is protected.
    if (!this.auth.user()) {
      this.actionMessage.set(this.i18n.t('watch.shareReadyPublic'));
      return;
    }
    this.content.share(item.videoId).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => {
        if (result?.url) this.shareUrl.set(this.absoluteUrl(result.url));
        this.actionMessage.set(this.i18n.t('watch.shareReady'));
      },
      error: () => {}
    });
  }

  openPlaylistPicker() {
    if (!this.requireAuthentication(this.i18n.t('watchPlaylist.loginRequired'))) return;
    this.playlistPickerOpen.set(true);
    this.playlistLoading.set(true);
    this.playlists.mine().pipe(finalize(() => this.playlistLoading.set(false))).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: lists => this.playlistOptions.set(lists),
      error: () => this.actionMessage.set(this.i18n.t('watchPlaylist.loadError'))
    });
  }

  closePlaylistPicker() {
    if (!this.playlistAddingId()) this.playlistPickerOpen.set(false);
  }

  addToPlaylist(playlist: PlaylistSummary) {
    const item = this.video();
    if (!item || this.playlistAddingId() || this.addedPlaylistIds()[playlist.playlistId]) return;
    this.playlistAddingId.set(playlist.playlistId);
    this.playlists.addVideo(playlist.playlistId, item.videoId).pipe(finalize(() => this.playlistAddingId.set(null))).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.addedPlaylistIds.update(ids => ({ ...ids, [playlist.playlistId]: true }));
        this.playlistOptions.update(lists => lists.map(list => list.playlistId === playlist.playlistId
          ? { ...list, itemCount: list.itemCount + 1 }
          : list));
        this.actionMessage.set(this.i18n.format('watchPlaylist.added', { name: playlist.name }));
        this.playlistPickerOpen.set(false);
      },
      error: () => this.actionMessage.set(this.i18n.format('watchPlaylist.addError', { name: playlist.name }))
    });
  }

  copyShareLink() {
    const url = this.shareUrl() || window.location.href;
    if (!navigator.clipboard) {
      this.actionMessage.set(this.i18n.t('watch.copyHint'));
      return;
    }
    navigator.clipboard.writeText(url).then(() => {
      this.actionMessage.set(this.i18n.t('watch.shareCopied'));
      this.shareOpen.set(false);
    }).catch(() => this.actionMessage.set(this.i18n.t('watch.copyError')));
  }

  closeShare() {
    this.shareOpen.set(false);
  }

  private absoluteUrl(value: string) {
    try { return new URL(value, window.location.origin).toString(); }
    catch { return value; }
  }

  comment() {
    if (!this.requireAuthentication(this.i18n.t('watch.loginCommentAction'))) return;
    const item = this.video();
    const text = this.commentText.trim();
    if (!item || !text) return;
    this.commentError.set('');
    this.content.createComment(item.videoId, text).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: comment => {
        this.comments.update(items => [comment, ...items]);
        this.commentTotal.update(total => total + 1);
        this.commentsChanged.next();
        this.loadComments();
        this.commentText = '';
        this.video.update(video => video ? { ...video, stats: { ...video.stats, comments: video.stats.comments + 1 } } : video);
      },
      error: error => this.commentError.set(error?.status === 429
        ? this.i18n.t('watch.commentRateLimited')
        : this.readError(error) || this.i18n.t('watch.commentError'))
    });
  }

  clearCommentError() {
    if (this.commentError()) this.commentError.set('');
  }

  setCommentSort(sort: 'newest' | 'top') {
    if (sort === this.commentSort()) return;
    this.commentSort.set(sort);
    this.commentsChanged.next();
    this.comments.set([]);
    this.commentPage.set(1);
    this.commentTotal.set(0);
    this.commentsLoading.set(false);
    this.repliesByComment.set({});
    this.expandedReplies.set({});
    this.repliesLoading.set({});
    this.resetReplyState();
    this.loadComments();
  }

  private resetReplyState() {
    this.repliesError.set({});
    this.replyPages.set({});
    this.replyTotals.set({});
    this.visibleReplyCounts.set({});
    this.replyTo.set(null);
    this.replyText = '';
    this.replyError.set('');
  }

  repliesFor(commentId: string): CommentItem[] {
    return this.repliesByComment()[commentId] ?? [];
  }

  visibleRepliesFor(commentId: string): CommentItem[] {
    return this.repliesFor(commentId).slice(0, this.visibleReplyCounts()[commentId] ?? this.repliesPageSize);
  }

  remainingReplies(comment: CommentItem): number {
    return Math.max(0, (this.replyTotals()[comment.commentId] ?? comment.replyCount) - this.visibleRepliesFor(comment.commentId).length);
  }

  moreRepliesLabel(comment: CommentItem): string {
    return this.i18n.t('watch.moreReplies', { count: this.i18n.formatNumber(this.remainingReplies(comment)) });
  }

  loadMoreReplies(comment: CommentItem) {
    if (this.isRepliesLoading(comment.commentId)) return;
    const limit = (this.visibleReplyCounts()[comment.commentId] ?? this.repliesPageSize) + this.repliesPageSize;
    this.visibleReplyCounts.update(state => ({ ...state, [comment.commentId]: limit }));
    if (this.repliesFor(comment.commentId).length < Math.min(limit, this.replyTotals()[comment.commentId] ?? comment.replyCount)) {
      this.loadReplies(comment, true);
    }
  }

  isRepliesOpen(commentId: string): boolean {
    return this.expandedReplies()[commentId] === true;
  }

  isRepliesLoading(commentId: string): boolean {
    return this.repliesLoading()[commentId] === true;
  }

  toggleReplies(comment: CommentItem) {
    if (this.isRepliesOpen(comment.commentId)) {
      this.expandedReplies.update(state => ({ ...state, [comment.commentId]: false }));
      this.visibleReplyCounts.update(state => ({ ...state, [comment.commentId]: this.repliesPageSize }));
      return;
    }
    if (Object.prototype.hasOwnProperty.call(this.repliesByComment(), comment.commentId)) {
      this.expandedReplies.update(state => ({ ...state, [comment.commentId]: true }));
      return;
    }
    this.loadReplies(comment, true);
  }

  loadReplies(comment: CommentItem, expand = true) {
    if (this.isRepliesLoading(comment.commentId)) return;
    const page = (this.replyPages()[comment.commentId] ?? 0) + 1;
    this.repliesLoading.update(state => ({ ...state, [comment.commentId]: true }));
    this.repliesError.update(state => ({ ...state, [comment.commentId]: '' }));
    if (expand) this.expandedReplies.update(state => ({ ...state, [comment.commentId]: true }));
    this.content.replies(comment.commentId, page, this.repliesPageSize).pipe(
      takeUntil(this.videoChanged),
      takeUntil(this.commentsChanged),
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.repliesLoading.update(state => ({ ...state, [comment.commentId]: false })))
    ).subscribe({
      next: value => {
        this.repliesByComment.update(groups => ({ ...groups, [comment.commentId]:
          [...new Map([...(groups[comment.commentId] ?? []), ...(value.items ?? [])].map(item => [item.commentId, item])).values()]
        }));
        this.replyPages.update(state => ({ ...state, [comment.commentId]: page }));
        this.replyTotals.update(state => ({ ...state, [comment.commentId]: value.total }));
      },
      error: () => this.repliesError.update(state => ({ ...state, [comment.commentId]: this.i18n.t('watch.replyLoadError') }))
    });
  }

  startReply(comment: CommentItem) {
    if (this.replyBusy() || !this.requireAuthentication(this.i18n.t('watch.loginReply'))) return;
    if (this.replyTo() !== comment.commentId) this.replyText = '';
    this.replyError.set('');
    this.replyTo.set(comment.commentId);
  }

  cancelReply() {
    if (this.replyBusy()) return;
    this.replyTo.set(null);
    this.replyText = '';
    this.replyError.set('');
  }

  reply(comment: CommentItem) {
    if (this.replyBusy() || !this.requireAuthentication(this.i18n.t('watch.loginReply'))) return;
    const text = this.replyText.trim();
    if (!text || text.length > 5000) {
      this.replyError.set(this.i18n.t('watch.replyInvalid'));
      return;
    }
    this.replyBusy.set(comment.commentId);
    this.replyError.set('');
    this.content.createComment(this.video()?.videoId ?? comment.videoId, text, comment.commentId).pipe(
      takeUntil(this.videoChanged), takeUntil(this.commentsChanged), takeUntilDestroyed(this.destroyRef),
      finalize(() => this.replyBusy.set(null))
    ).subscribe({
      next: reply => {
        this.repliesByComment.update(groups => ({
          ...groups,
          [comment.commentId]: [reply, ...(groups[comment.commentId] ?? [])]
        }));
        this.expandedReplies.update(state => ({ ...state, [comment.commentId]: true }));
        const parent = this.currentComment(comment);
        this.replaceCommentInState({ ...parent, replyCount: parent.replyCount + 1 });
        this.replyTotals.update(state => ({ ...state, [comment.commentId]: (state[comment.commentId] ?? comment.replyCount) + 1 }));
        this.video.update(video => video ? { ...video, stats: { ...video.stats, comments: video.stats.comments + 1 } } : video);
        this.replyText = '';
        this.replyTo.set(null);
        this.actionMessage.set(this.i18n.t('watch.replySaved'));
      },
      error: error => this.replyError.set(error?.error?.code === 'INVALID_COMMENT'
        ? this.i18n.t('watch.replyInvalid')
        : error?.status === 429 ? this.i18n.t('watch.commentRateLimited') : errorMessage(error, this.i18n))
    });
  }

  reactComment(comment: CommentItem, type: 'like' | 'dislike') {
    if (!this.requireAuthentication(this.i18n.t('watch.loginCommentInteraction'))) return;
    const next = comment.myReaction === type ? null : type;
    this.content.reactComment(comment.commentId, next).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => {
        const updated = {
          ...this.currentComment(comment),
          myReaction: result.myReaction as 'like' | 'dislike' | null,
          likes: result.likes,
          dislikes: result.dislikes
        };
        this.replaceCommentInState(updated);
      },
      error: error => this.actionMessage.set(this.readError(error) || this.i18n.t('watch.loginCommentInteraction'))
    });
  }

  private currentComment(comment: CommentItem): CommentItem {
    return this.comments().find(item => item.commentId === comment.commentId)
      ?? Object.values(this.repliesByComment()).flat().find(item => item.commentId === comment.commentId)
      ?? comment;
  }

  private replaceCommentInState(updated: CommentItem) {
    this.comments.update(items => items.map(item => item.commentId === updated.commentId ? updated : item));
    this.repliesByComment.update(groups => {
      const next: Record<string, CommentItem[]> = {};
      for (const [parentId, items] of Object.entries(groups)) {
        next[parentId] = items.map(item => item.commentId === updated.commentId ? updated : item);
      }
      return next;
    });
  }

  hide(comment: CommentItem) {
    if (!this.requireAuthentication(this.i18n.t('watch.loginCommentManage'))) return;
    this.content.hideComment(comment.commentId, comment.status !== 'hidden').pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: value => this.replaceCommentInState(value),
      error: error => this.actionMessage.set(this.readError(error) || this.i18n.t('watch.commentUpdateError'))
    });
  }

  editComment(comment: CommentItem) {
    if (!this.isOwnComment(comment) || this.commentEditBusy()) return;
    this.commentEditText = comment.content;
    this.editingCommentId.set(comment.commentId);
    this.actionMessage.set('');
  }

  cancelCommentEdit() {
    if (this.commentEditBusy()) return;
    this.editingCommentId.set(null);
    this.commentEditText = '';
  }

  saveCommentEdit(comment: CommentItem) {
    const text = this.commentEditText.trim();
    if (!text || !this.isOwnComment(comment) || this.commentEditBusy()) return;
    this.commentEditBusy.set(true);
    this.content.updateComment(comment.commentId, text).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef),
      finalize(() => this.commentEditBusy.set(false))).subscribe({
      next: updated => {
        this.replaceCommentInState(updated);
        this.editingCommentId.set(null);
        this.commentEditText = '';
      },
      error: error => this.actionMessage.set(this.readError(error) || this.i18n.t('watch.commentUpdateError'))
    });
  }

  async remove(comment: CommentItem) {
    if (!this.isOwnComment(comment)) return;
    if (!this.requireAuthentication(this.i18n.t('watch.loginCommentDelete'))) return;
    if (!(await this.feedback.confirm(this.i18n.t('watch.deleteCommentConfirm'), { tone: 'danger' }))) return;
    this.content.deleteComment(comment.commentId).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        const wasRoot = this.comments().some(item => item.commentId === comment.commentId);
        if (wasRoot) {
          this.comments.update(items => items.filter(item => item.commentId !== comment.commentId));
          this.commentTotal.update(total => Math.max(0, total - 1));
          this.commentsChanged.next();
          this.loadComments();
          this.repliesByComment.update(groups => {
            const next = { ...groups };
            delete next[comment.commentId];
            return next;
          });
        } else {
          const parentId = Object.entries(this.repliesByComment())
            .find(([, items]) => items.some(item => item.commentId === comment.commentId))?.[0];
          const parent = [...this.comments(), ...Object.values(this.repliesByComment()).flat()]
            .find(item => item.commentId === parentId);
          this.repliesByComment.update(groups => Object.fromEntries(
            Object.entries(groups).map(([parentId, items]) => [parentId, items.filter(item => item.commentId !== comment.commentId)])
          ));
          if (parent) {
            const updated = { ...parent, replyCount: Math.max(0, parent.replyCount - 1) };
            this.replaceCommentInState(updated);
            // Deletion shifts server offsets: restart this branch before paging again.
            this.repliesByComment.update(groups => ({ ...groups, [parent.commentId]: [] }));
            this.replyPages.update(state => ({ ...state, [parent.commentId]: 0 }));
            this.replyTotals.update(state => ({ ...state, [parent.commentId]: updated.replyCount }));
            this.visibleReplyCounts.update(state => ({ ...state, [parent.commentId]: this.repliesPageSize }));
            this.loadReplies(updated);
          }
        }
        this.video.update(video => video ? { ...video, stats: { ...video.stats, comments: Math.max(0, video.stats.comments - 1) } } : video);
      },
      error: error => this.actionMessage.set(this.readError(error) || this.i18n.t('watch.commentUpdateError'))
    });
  }

  readonly reportModalOpen = signal(false);
  readonly reportTargetType = signal<'video' | 'channel' | 'comment'>('video');
  readonly reportTargetId = signal('');
  readonly reportTargetTitle = signal('');

  report(comment: CommentItem) {
    if (!this.requireAuthentication(this.i18n.t('watch.loginReport'))) return;
    this.reportTargetType.set('comment');
    this.reportTargetId.set(comment.commentId);
    this.reportTargetTitle.set(this.i18n.format('watch.commentBy', { name: comment.displayName }));
    this.reportModalOpen.set(true);
  }

  reportVideo() {
    if (!this.requireAuthentication(this.i18n.t('watchPlaylist.loginReport'))) return;
    const currentVideo = this.video();
    if (!currentVideo) return;
    this.reportTargetType.set('video');
    this.reportTargetId.set(currentVideo.videoId);
    this.reportTargetTitle.set(currentVideo.title);
    this.reportModalOpen.set(true);
  }

  onReportSubmitted() {
    this.actionMessage.set(this.i18n.t('watch.reportSubmitted'));
    setTimeout(() => this.actionMessage.set(''), 4000);
  }

  openDownloads() {
    if (!this.requireAuthentication(this.i18n.t('watch.loginDownload'))) return;
    if (this.downloadOptions().length) {
      this.downloadOpen.update(value => !value);
      return;
    }
    if (this.downloadBusy()) return;
    this.downloadBusy.set(true);
    this.content.downloadOptions(this.videoId).pipe(finalize(() => this.downloadBusy.set(false))).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: options => {
        this.downloadOptions.set(options ?? []);
        this.downloadOpen.set(true);
        if (!options?.length) this.actionMessage.set(this.i18n.t('watch.noDownloadQuality'));
      },
      error: error => this.actionMessage.set(this.readError(error) || this.i18n.t('watch.downloadUnsupported'))
    });
  }

  async download(quality: string) {
    if (this.downloadBusy() || !this.requireAuthentication(this.i18n.t('watch.loginDownload'))) return;
    this.downloadBusy.set(true);
    try {
      const extension = this.downloadOptions().find(option => option.quality === quality)?.fileExtension ?? '.mp4';
      const saved = await this.videoDownload.save(this.videoId, quality, this.video()?.title ?? 'Dalitu', extension);
      if (saved) {
        this.downloadOpen.set(false);
        this.actionMessage.set(this.i18n.t(saved === 'saved' ? 'watch.downloadSaved' : 'watch.downloadStarted'));
      }
    } catch (error) {
      this.actionMessage.set(this.readError(error) || this.i18n.t('watch.downloadError'));
    } finally {
      this.downloadBusy.set(false);
    }
  }

  setRecommendationFilter(filter: 'all' | 'related' | 'channel') {
    this.recommendationFilter.set(filter);
    this.loadRecommendations();
  }

  private loadRecommendations() {
    this.recommendationVersion++;
    this.recommendationsChanged.next();
    this.relatedVideos.set([]);
    this.recommendationsLoading.set(false);
    this.recommendationsError.set(false);
    this.recommendationsMoreError.set(false);
    const filter = this.recommendationFilter();
    const current = this.video();
    this.recommendationNextPage = 1;
    if (filter === 'channel' && current) {
      this.recommendationSource = 'channel';
    } else if (filter === 'related' && current && (current.categoryId || current.tags?.length)) {
      this.recommendationSource = current.tags?.length ? 'tag' : 'category';
    } else {
      this.recommendationSource = 'feed';
    }
    this.recommendationsHasMore.set(true);
    this.loadMoreRecommendations();
  }

  loadMoreRecommendations() {
    const current = this.video();
    if (!current || !this.recommendationsHasMore() || this.recommendationsLoading()) return;
    const version = this.recommendationVersion;
    const source = this.recommendationSource;
    const page = this.recommendationNextPage;
    const pageSize = 20;
    this.recommendationsLoading.set(true);
    this.recommendationsError.set(false);
    this.recommendationsMoreError.set(false);
    let fillCategory = false;
    const videos = source === 'channel'
      ? this.content.search({ channelId: current.channelId, sort: 'newest', page, pageSize })
      : source === 'tag' || source === 'category'
        ? this.content.search({ categoryId: current.categoryId ?? undefined,
          tag: source === 'tag' ? current.tags?.[0] : undefined, sort: 'popular', page, pageSize })
        : this.content.feed('home', page, pageSize);
    videos.pipe(
      takeUntil(this.recommendationsChanged), takeUntil(this.videoChanged),
      takeUntilDestroyed(this.destroyRef), finalize(() => {
        if (version !== this.recommendationVersion) return;
        this.recommendationsLoading.set(false);
        if (fillCategory) this.loadMoreRecommendations();
      })
    ).subscribe({
      next: value => {
        if (version !== this.recommendationVersion) return;
        const combined = new Map(this.relatedVideos().map(item => [item.videoId, item]));
        for (const item of value.items ?? []) {
          if (item.videoId !== this.videoId && !combined.has(item.videoId)) combined.set(item.videoId, item);
        }
        this.relatedVideos.set([...combined.values()]);
        const hasMore = !!value.items?.length && page * pageSize < value.total;
        if (hasMore) {
          this.recommendationNextPage = page + 1;
          this.recommendationsHasMore.set(true);
        } else if (source === 'tag' && current.categoryId) {
          this.recommendationSource = 'category';
          this.recommendationNextPage = 1;
          this.recommendationsHasMore.set(true);
          // Fill a sparse tag result promptly, then continue through the category on scroll.
          fillCategory = this.relatedVideos().length < 10;
        } else {
          this.recommendationsHasMore.set(false);
        }
      },
      error: () => {
        if (version !== this.recommendationVersion) return;
        if (this.relatedVideos().length) this.recommendationsMoreError.set(true);
        else this.recommendationsError.set(true);
      }
    });
  }

  toggleAutoplay() {
    this.autoplay.update(value => !value);
  }

  isOwnComment(comment: CommentItem) {
    const userId = this.auth.user()?.userId;
    return !!userId && !!comment.userId && comment.status !== 'deleted' && comment.userId === userId;
  }
  isOwner() { return this.video()?.channelId === this.myChannelId(); }

  initials(value: string | null | undefined) {
    const text = (value ?? '').trim();
    return text ? text.charAt(0).toUpperCase() : 'H';
  }

  visibilityLabel(value: string | null | undefined) {
    if (value === 'public') return this.i18n.t('ui.public');
    if (value === 'unlisted') return this.i18n.t('ui.unlisted');
    return this.i18n.t('ui.private');
  }

  languageLabel(value: string | null | undefined) {
    const code = (value ?? '').toLowerCase();
    if (code.startsWith('en')) return this.i18n.t('upload.languageEnglish');
    if (code.startsWith('ja')) return this.i18n.t('upload.languageJapanese');
    if (code.startsWith('vi')) return this.i18n.t('upload.languageVietnamese');
    return value || this.i18n.t('watch.defaultLanguage');
  }

  countLabel(key: 'watch.views' | 'watch.subscribers' | 'watch.chapterCountLabel' | 'watch.commentCount' | 'watch.recommendationViews' | 'watch.repliesCount', count: number) {
    return this.i18n.t(key, { count: this.i18n.formatNumber(count) });
  }

  ratingLabel(star: number) {
    return this.i18n.t('watch.ratingStar', { star: String(star) });
  }

  repliesLabel(count: number) {
    return this.countLabel('watch.repliesCount', count);
  }

  commentStatusLabel(hidden: boolean) {
    return this.i18n.t(hidden ? 'watch.show' : 'watch.hide');
  }

  duration(value: number) {
    const seconds = Math.max(0, Math.floor(value || 0));
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  }

  formatBytes(bytes: number) {
    if (bytes >= 1024 ** 3) return `${this.i18n.formatNumber(bytes / 1024 ** 3, { maximumFractionDigits: 2 })} GB`;
    return `${this.i18n.formatNumber(bytes / 1024 ** 2, { maximumFractionDigits: 2 })} MB`;
  }

  private viewerState(item: VideoDetail) {
    return item.viewerState ?? { reaction: null, rating: null, resumeAtSeconds: 0, progress: 0 };
  }

  private readError(_error: any) {
    return '';
  }

  private requireAuthentication(message: string): boolean {
    return this.loginPrompt.require(undefined, undefined, message);
  }

  @HostListener('window:keydown', ['$event'])
  keys(event: KeyboardEvent) {
    if (event.key === 'Escape' && this.playlistPickerOpen()) {
      event.preventDefault();
      this.closePlaylistPicker();
      return;
    }
    if (event.key === 'Escape' && this.shareOpen()) {
      event.preventDefault();
      this.closeShare();
      return;
    }
    if (event.key === 'Escape' && this.downloadOpen()) {
      event.preventDefault();
      this.downloadOpen.set(false);
      return;
    }
    const target = event.target as HTMLElement | null;
    if (this.pipActive()) return;
    if (event.ctrlKey || event.metaKey || event.altKey || target?.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT', 'BUTTON'].includes(target?.tagName ?? '')) return;
    const player = this.playerRef?.nativeElement;
    if (!player) return;
    const key = event.key.toLowerCase();
    if (event.code === 'Space' || key === 'k') this.togglePlayback();
    else if (key === 'j' || event.key === 'ArrowLeft') this.seekBy(-10);
    else if (key === 'l' || event.key === 'ArrowRight') this.seekBy(10);
    else if (key === 'm') this.toggleMute();
    else if (key === 'f') this.toggleFullscreen();
    else return;
    event.preventDefault();
    event.stopPropagation();
  }

  async toggleSubscribe() {
    const channel = this.channel();
    if (!channel || this.subscriptionBusy()) return;

    if (!this.loginPrompt.require()) return;

    if (this.myChannelId() === channel.channelId) {
      this.actionMessage.set(this.i18n.t('watch.cannotSubscribeSelf'));
      return;
    }

    this.subscriptionBusy.set(true);
    if (this.subscribed()) {
      if (!(await confirmChannelUnsubscribe(this.feedback, this.i18n)) || this.destroyRef.destroyed
          || this.channel()?.channelId !== channel.channelId || !this.subscribed()) {
        this.subscriptionBusy.set(false);
        return;
      }
      this.channels.unsubscribe(channel.channelId).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef), finalize(() => this.subscriptionBusy.set(false))).subscribe({
        next: () => {
          this.subscribed.set(false);
          this.subscriptionNotificationsEnabled.set(false);
          this.subscriberCount.update(c => Math.max(0, c - 1));
        },
        error: () => this.actionMessage.set(this.i18n.t('watch.unsubscribeError'))
      });
    } else {
      this.channels.subscribe(channel.channelId).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef), finalize(() => this.subscriptionBusy.set(false))).subscribe({
        next: sub => {
          this.subscribed.set(true);
          this.subscriptionNotificationsEnabled.set(sub.notificationsEnabled);
          this.subscriberCount.update(c => c + 1);
        },
        error: () => this.actionMessage.set(this.i18n.t('watch.subscribeError'))
      });
    }
  }

  loginReturnUrl() {
    return this.router.url;
  }

  toggleSubscriptionNotifications() {
    const channel = this.channel();
    if (!channel || !this.subscribed()) {
      this.actionMessage.set(this.i18n.t('watchPlaylist.subscribeFirst'));
      return;
    }
    const enabled = !this.subscriptionNotificationsEnabled();
    this.channels.updateSubscriptionNotifications(channel.channelId, enabled).pipe(takeUntil(this.videoChanged), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: response => this.subscriptionNotificationsEnabled.set(response.notificationsEnabled),
      error: () => this.actionMessage.set(this.i18n.t('watchPlaylist.notificationsError'))
    });
  }
}
