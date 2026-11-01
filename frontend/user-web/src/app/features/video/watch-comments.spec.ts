import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { EMPTY, NEVER, Subject, of, throwError } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { ChannelService } from '../../core/channel.service';
import { CommentItem, ContentService, VideoDetail } from '../../core/content.service';
import { I18nService } from '../../core/i18n.service';
import { LoginPromptService } from '../../core/login-prompt.service';
import { PictureInPictureService } from '../../core/picture-in-picture.service';
import { PlanService } from '../../core/plan.service';
import { PlayerPreferencesService } from '../../core/player-preferences.service';
import { PlaylistService } from '../../core/playlist.service';
import { VideoDownloadService } from '../../core/video-download.service';
import { FeedbackService } from '@dalitu/ui/feedback.service';
import { WatchPage } from './watch-page';

describe('Watch comment replies', () => {
  let page: WatchPage;
  let content: jasmine.SpyObj<ContentService>;
  const item = (id: string, replyCount = 0, parentCommentId: string | null = null): CommentItem => ({
    commentId: id, videoId: 'video', userId: 'viewer', displayName: 'Viewer', parentCommentId,
    content: id, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    status: 'visible', likes: 0, dislikes: 0, myReaction: null, replyCount,
  });
  const root = item('root', 7);
  const children = Array.from({ length: 7 }, (_, i) => item(`child-${i}`, 0, 'root'));

  beforeEach(() => {
    content = jasmine.createSpyObj('ContentService', ['replies', 'createComment', 'deleteComment']);
    content.replies.and.callFake((_id, n = 1) => of({ items: children.slice((n - 1) * 3, n * 3), page: n, pageSize: 3, total: 7 }));
    TestBed.configureTestingModule({ providers: [
      { provide: ContentService, useValue: content },
      { provide: AuthService, useValue: { user: signal({ userId: 'viewer' }), restore: () => NEVER } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'video' }) }, paramMap: EMPTY, queryParamMap: EMPTY } },
      { provide: Router, useValue: { url: '/watch/video' } },
      { provide: LoginPromptService, useValue: { require: () => true } },
      { provide: PlayerPreferencesService, useValue: { preferences: () => ({ muted: false, volume: 1 }) } },
      { provide: PictureInPictureService, useValue: { active: signal(false), entering: signal(false) } },
      { provide: ChannelService, useValue: {} }, { provide: PlaylistService, useValue: {} },
      { provide: PlanService, useValue: {} }, { provide: VideoDownloadService, useValue: {} },
      { provide: FeedbackService, useValue: { confirm: () => Promise.resolve(true) } },
    ] });
    TestBed.overrideComponent(WatchPage, { set: { template: '', imports: [] } });
    page = TestBed.createComponent(WatchPage).componentInstance;
    page.comments.set([root]);
  });

  it('previews three replies, then loads three at a time until the branch ends', () => {
    page.loadReplies(root);
    expect(content.replies).toHaveBeenCalledWith('root', 1, 3);
    expect(page.visibleRepliesFor('root').length).toBe(3);
    expect(page.remainingReplies(root)).toBe(4);
    page.loadMoreReplies(root);
    expect(content.replies).toHaveBeenCalledWith('root', 2, 3);
    expect(page.visibleRepliesFor('root').length).toBe(6);
    page.loadMoreReplies(root);
    expect(page.visibleRepliesFor('root').length).toBe(7);
    expect(page.remainingReplies(root)).toBe(0);
  });

  it('collapses back to the preview and reuses cached replies', () => {
    page.loadReplies(root); page.loadMoreReplies(root);
    page.toggleReplies(root); page.toggleReplies(root);
    expect(page.visibleRepliesFor('root').length).toBe(3);
    page.loadMoreReplies(root);
    expect(content.replies.calls.count()).toBe(2);
    expect(page.visibleRepliesFor('root').length).toBe(6);
  });

  it('posts a single-character nested reply to the selected parent once', () => {
    const child = item('child', 0, 'root');
    page.repliesByComment.set({ root: [child] });
    const request = new Subject<CommentItem>();
    content.createComment.and.returnValue(request);
    page.startReply(child); page.replyText = ' s ';
    page.reply(child); page.reply(child);
    expect(content.createComment).toHaveBeenCalledOnceWith('video', 's', 'child');
    expect(page.replyBusy()).toBe('child');
    // A reaction can complete while the reply request is still pending.
    page.repliesByComment.set({ root: [{ ...child, likes: 7, myReaction: 'like' }] });
    request.next(item('nested', 0, 'child')); request.complete();
    expect(page.repliesFor('child')[0].commentId).toBe('nested');
    expect(page.repliesFor('root')[0].replyCount).toBe(1);
    expect(page.repliesFor('root')[0].likes).toBe(7);
    expect(page.comments()[0].replyCount).toBe(7);
    expect(page.replyTo()).toBeNull(); expect(page.replyBusy()).toBeNull();
  });

  it('keeps failed reply text and an inline error so the user can retry', () => {
    content.createComment.and.returnValue(throwError(() => new Error('offline')));
    page.startReply(root); page.replyText = 'Reply draft'; page.reply(root);
    expect(page.replyText).toBe('Reply draft'); expect(page.replyTo()).toBe('root');
    expect(page.replyError()).toBeTruthy(); expect(page.replyBusy()).toBeNull();
  });

  it('shows comment rate limits inline beside the composer instead of in the page notice', () => {
    page.video.set({ videoId: 'video' } as unknown as VideoDetail);
    page.commentText = 'A new comment';
    content.createComment.and.returnValue(throwError(() => ({ status: 429 })));

    page.comment();

    expect(page.commentError()).toBe(TestBed.inject(I18nService).t('watch.commentRateLimited'));
    expect(page.actionMessage()).toBe('');
  });

  it('rejects blank and overlong replies without making a request', () => {
    page.startReply(root);
    for (const text of ['  ', 'a'.repeat(5001)]) { page.replyText = text; page.reply(root); }
    expect(content.createComment).not.toHaveBeenCalled();
    expect(page.replyError()).toBe(TestBed.inject(I18nService).t('watch.replyInvalid'));
  });

  it('retries a failed page without skipping replies', () => {
    page.loadReplies(root);
    content.replies.and.returnValue(throwError(() => new Error('offline')));
    page.loadMoreReplies(root);
    expect(page.repliesError()['root']).toBeTruthy();
    content.replies.and.returnValue(of({ items: children.slice(3, 6), page: 2, pageSize: 3, total: 7 }));
    page.loadReplies(root);
    expect(content.replies.calls.mostRecent().args).toEqual(['root', 2, 3]);
    expect(page.visibleRepliesFor('root').length).toBe(6);
    expect(page.repliesError()['root']).toBe('');
  });

  it('keeps a newly posted reply visible while paging without duplicates', () => {
    page.loadReplies(root);
    content.createComment.and.returnValue(of(children[6]));
    page.startReply(root); page.replyText = 'My new reply'; page.reply(root);
    expect(page.visibleRepliesFor('root')[0].commentId).toBe('child-6');
    expect(page.visibleRepliesFor('root').length).toBe(3);
    page.loadMoreReplies(root); page.loadMoreReplies(root);
    expect(new Set(page.repliesFor('root').map(item => item.commentId)).size).toBe(page.repliesFor('root').length);
  });

  it('restarts paging after deletion so the next sibling is not skipped', async () => {
    page.loadReplies(root);
    content.deleteComment.and.returnValue(of(undefined));
    const remaining = children.slice(1);
    content.replies.and.returnValue(of({ items: remaining.slice(0, 3), page: 1, pageSize: 3, total: 6 }));
    await page.remove(children[0]);
    expect(content.replies.calls.mostRecent().args).toEqual(['root', 1, 3]);
    expect(page.comments()[0].replyCount).toBe(6);
    expect(page.visibleRepliesFor('root').map(item => item.commentId)).toEqual(['child-1', 'child-2', 'child-3']);
    expect(page.remainingReplies(page.comments()[0])).toBe(3);
  });
});
