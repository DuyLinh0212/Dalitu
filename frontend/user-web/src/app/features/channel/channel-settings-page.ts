import { ConfirmDialogComponent } from '@dalitu/ui/confirm-dialog.component';
import { ResultNoticeComponent } from '@dalitu/ui/result-notice.component';
import { FeedbackService } from '@dalitu/ui/feedback.service';
import { Component, DestroyRef, ElementRef, ViewChild, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import {
  ChannelDetail,
  ChannelInvitation,
  ChannelMember,
  ChannelRole,
  ChannelService
} from '../../core/channel.service';
import { errorMessage } from '../../core/auth.service';
import { ContentService } from '../../core/content.service';
import { I18nService } from '../../core/i18n.service';
import { LocaleDatePipe } from '../../core/locale-date.pipe';
import { TranslatePipe } from '../../core/translate.pipe';
import { ChannelAvatarCropperComponent } from './channel-avatar-cropper.component';
import { CHANNEL_BRANDING_TRANSLATIONS } from './channel-branding.translations';
import { avatarFileError, validContactEmail } from './channel-form-validation';

export type ChannelLinkPlatform = 'facebook' | 'instagram' | 'tiktok' | 'x' | 'other';

export interface ChannelLinkItem {
  platform: ChannelLinkPlatform;
  title: string;
  url: string;
}

type SettingsTab = 'basic' | 'branding' | 'members' | 'danger';

@Component({
  selector: 'app-channel-settings-page',
  imports: [ConfirmDialogComponent, ResultNoticeComponent, LocaleDatePipe, FormsModule, RouterLink, TranslatePipe, ChannelAvatarCropperComponent],
  templateUrl: './channel-settings-page.html',
  styleUrl: './channel-settings-page.scss'
})
export class ChannelSettingsPage {
  private readonly feedback = inject(FeedbackService);
  private route = inject(ActivatedRoute);
  private channelService = inject(ChannelService);
  private contentService = inject(ContentService);
  private router = inject(Router);
  readonly i18n = inject(I18nService);
  private readonly destroyRef = inject(DestroyRef);
  private handleTimer?: ReturnType<typeof setTimeout>;
  private handleGeneration = 0;
  readonly handleFeedback = signal('');
  readonly handleValid = signal<boolean | null>(null);
  readonly handleChecking = signal(false);

  @ViewChild('avatarInput') avatarInput?: ElementRef<HTMLInputElement>;
  @ViewChild('bannerInput') bannerInput?: ElementRef<HTMLInputElement>;
  @ViewChild('watermarkInput') watermarkInput?: ElementRef<HTMLInputElement>;

  readonly channel = signal<ChannelDetail | null>(null);
  readonly members = signal<ChannelMember[]>([]);
  readonly invitations = signal<ChannelInvitation[]>([]);
  readonly roles = signal<ChannelRole[]>([]);
  readonly activeTab = signal<SettingsTab>('basic');
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly avatarCropFile = signal<File | null>(null);
  readonly error = signal('');
  readonly message = signal('');
  readonly linkOptions: ChannelLinkPlatform[] = ['facebook', 'instagram', 'tiktok', 'x', 'other'];
  readonly linkOrderMessage = signal('');
  draggedLinkIndex: number | null = null;
  dragOverLinkIndex: number | null = null;

  readonly showAppealModal = signal(false);
  readonly appealSubmitting = signal(false);
  readonly appealSuccess = signal(false);
  appealReason = '';
  appealEvidenceFile: File | null = null;

  name = '';
  handle = '';
  description = '';
  contactEmail = '';
  links: ChannelLinkItem[] = [];
  inviteEmail = '';
  inviteRole: ChannelRole['code'] = 'editor';
  confirmChannelName = '';
  showDeleteModal = signal(false);

  constructor() {
    this.i18n.registerTranslations(CHANNEL_BRANDING_TRANSLATIONS);
    this.destroyRef.onDestroy(() => clearTimeout(this.handleTimer));
    this.loadChannel();
  }

  onHandleChanged(value: string) {
    this.handle = value;
    clearTimeout(this.handleTimer);
    const generation = ++this.handleGeneration;
    const clean = value.trim().replace(/^@+/, '').toLowerCase();
    this.handleFeedback.set('');
    this.handleValid.set(null);
    this.handleChecking.set(false);
    if (clean === this.channel()?.handle.toLowerCase()) return;
    if (!/^[a-z0-9_.-]{3,50}$/.test(clean)) {
      this.handleFeedback.set(this.i18n.t('channel.handleCharsError'));
      this.handleValid.set(false);
      return;
    }
    this.handleChecking.set(true);
    this.handleFeedback.set(this.i18n.t('channel.handleChecking'));
    this.handleTimer = setTimeout(() => {
      this.channelService.checkHandle(clean, this.channel()?.channelId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: result => {
          if (generation !== this.handleGeneration) return;
          this.handleChecking.set(false);
          this.handleValid.set(result.isAvailable);
          this.handleFeedback.set(this.i18n.t(result.isAvailable ? 'channel.handleAvailable' : 'channel.handleTaken'));
        },
        error: () => {
          if (generation !== this.handleGeneration) return;
          this.handleChecking.set(false);
          this.handleFeedback.set(this.i18n.t('channel.handleCheckError'));
          this.handleValid.set(false);
        }
      });
    }, 250);
  }

  openAppealModal() {
    this.appealReason = '';
    this.appealEvidenceFile = null;
    this.appealSuccess.set(false);
    this.showAppealModal.set(true);
  }

  closeAppealModal() {
    this.showAppealModal.set(false);
  }

  submitAppeal() {
    const ch = this.channel();
    if (!ch || !this.appealReason.trim()) return;

    this.appealSubmitting.set(true);
    this.contentService.createAppeal({
      targetType: 'channel',
      targetId: ch.channelId,
      reason: this.appealReason.trim(),
    }, this.appealEvidenceFile).pipe(finalize(() => this.appealSubmitting.set(false))).subscribe({
      next: () => {
        this.appealSuccess.set(true);
        setTimeout(() => {
          this.closeAppealModal();
          this.message.set(this.i18n.t('studio.appealSubmitted'));
        }, 1500);
      },
      error: err => {
        this.error.set(errorMessage(err, this.i18n) || this.i18n.t('channel.appealSubmitFallback'));
      }
    });
  }

  can(permission: string): boolean {
    return this.channel()?.permissions.includes(permission) ?? false;
  }

  roleName(code: string): string {
    if (code === 'owner') return this.i18n.t('channel.roleOwner');
    const key = this.roleKey(code);
    return key ? this.i18n.t(key) : (this.roles().find(role => role.code === code)?.name ?? code);
  }

  roleDescription(code: string): string {
    const key = this.roleDescriptionKey(code);
    return key ? this.i18n.t(key) : (this.roles().find(role => role.code === code)?.description ?? '');
  }

  private roleKey(code: string): string | null {
    if (code === 'manager') return 'channel.roleManager';
    if (code === 'editor') return 'channel.roleEditor';
    if (code === 'moderator') return 'channel.roleModerator';
    if (code === 'viewer') return 'channel.roleViewer';
    return null;
  }

  private roleDescriptionKey(code: string): string | null {
    if (code === 'manager') return 'channel.roleManagerDesc';
    if (code === 'editor') return 'channel.roleEditorDesc';
    if (code === 'moderator') return 'channel.roleModeratorDesc';
    if (code === 'viewer') return 'channel.roleViewerDesc';
    return null;
  }

  addLink() {
    if (this.links.length < 14) {
      this.links.push({ platform: 'facebook', title: this.i18n.t('channel.platform.facebook'), url: '' });
    }
  }

  removeLink(index: number) {
    this.links.splice(index, 1);
  }

  onLinkDragStart(event: DragEvent, index: number) {
    if (this.busy()) {
      event.preventDefault();
      return;
    }
    this.draggedLinkIndex = index;
    this.linkOrderMessage.set('');
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData('text/plain', String(index));
    }
  }

  onLinkDragOver(event: DragEvent, index: number) {
    if (this.draggedLinkIndex === null || this.busy()) return;
    event.preventDefault();
    this.dragOverLinkIndex = index;
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
  }

  onLinkDragLeave(event: DragEvent, index: number) {
    const row = event.currentTarget;
    if (row instanceof HTMLElement && event.relatedTarget instanceof Node && row.contains(event.relatedTarget)) return;
    if (this.dragOverLinkIndex === index) this.dragOverLinkIndex = null;
  }

  onLinkDrop(event: DragEvent, targetIndex: number) {
    if (this.draggedLinkIndex === null || this.busy()) return;
    event.preventDefault();
    const rawTransferIndex = event.dataTransfer?.getData('text/plain') ?? '';
    const transferIndex = rawTransferIndex === '' ? Number.NaN : Number(rawTransferIndex);
    const sourceIndex = Number.isInteger(transferIndex) ? transferIndex : this.draggedLinkIndex;
    this.moveLink(sourceIndex, targetIndex);
    this.resetLinkDrag();
  }

  onLinkDragEnd() {
    this.resetLinkDrag();
  }

  onLinkReorderKeydown(event: KeyboardEvent, index: number) {
    if ((event.key !== 'ArrowUp' && event.key !== 'ArrowDown') || event.ctrlKey || event.metaKey) return;
    event.preventDefault();
    const targetIndex = index + (event.key === 'ArrowUp' ? -1 : 1);
    if (!this.moveLink(index, targetIndex)) return;
    setTimeout(() => {
      document.querySelectorAll<HTMLButtonElement>('.channel-links-section .link-drag-handle')[targetIndex]?.focus();
    });
  }

  private moveLink(sourceIndex: number, targetIndex: number): boolean {
    if (this.busy() || sourceIndex < 0 || targetIndex < 0 || sourceIndex >= this.links.length || targetIndex >= this.links.length || sourceIndex === targetIndex) return false;
    const reordered = [...this.links];
    const [link] = reordered.splice(sourceIndex, 1);
    reordered.splice(targetIndex, 0, link);
    this.links = reordered;
    this.linkOrderMessage.set(this.i18n.t('channel.linkMoved', { position: targetIndex + 1 }));
    return true;
  }

  private resetLinkDrag() {
    this.draggedLinkIndex = null;
    this.dragOverLinkIndex = null;
  }

  loadChannel(preserveActiveTab = false) {
    const handle = this.route.snapshot.paramMap.get('handle');
    if (!handle) {
      this.error.set(this.i18n.t('channel.handleNotFound'));
      this.loading.set(false);
      return;
    }

    this.loading.set(true);
    this.channelService.getChannel(handle).pipe(finalize(() => this.loading.set(false))).subscribe({
      next: channel => {
        this.channel.set(channel);
        this.name = channel.name;
        this.handle = channel.handle;
        this.onHandleChanged(channel.handle);
        this.description = channel.description ?? '';
        this.contactEmail = channel.contactEmail ?? '';
        this.parseLinks(channel);
        if (!preserveActiveTab) this.chooseAvailableTab();
        this.loadCollaboration();
      },
      error: err => this.error.set(errorMessage(err, this.i18n) || this.i18n.t('channel.loadError'))
    });
  }

  private parseLinks(channel: ChannelDetail) {
    try {
      const parsed = typeof channel.settings === 'string' ? JSON.parse(channel.settings || '{}') : (channel.settings || {});
      if (Array.isArray(parsed?.links)) {
        this.links = parsed.links.map((l: any) => this.normalizeLink(l));
        return;
      }
    } catch {}

    const local = localStorage.getItem('hutube_channel_links_' + channel.channelId);
    if (local) {
      try {
        const parsedLocal = JSON.parse(local);
        if (Array.isArray(parsedLocal)) {
          this.links = parsedLocal.map((l: any) => this.normalizeLink(l));
          return;
        }
      } catch {}
    }
    this.links = [];
  }

  loadCollaboration() {
    const channel = this.channel();
    if (!channel || channel.status === 'suspended') return;

    this.channelService.getRoles().subscribe({ next: roles => this.roles.set(roles) });
    if (this.can('member.view')) {
      this.channelService.getMembers(channel.channelId).subscribe({
        next: members => this.members.set(members),
        error: err => {
          if (this.channel()?.status !== 'suspended') {
            this.error.set(errorMessage(err, this.i18n));
          }
        }
      });
    }
    if (this.can('member.invite')) {
      this.channelService.getPendingInvitations(channel.channelId).subscribe({
        next: invitations => this.invitations.set(invitations),
        error: err => {
          if (this.channel()?.status !== 'suspended') {
            this.error.set(errorMessage(err, this.i18n));
          }
        }
      });
    }
  }

  setTab(tab: SettingsTab) {
    this.activeTab.set(tab);
    this.error.set('');
    this.message.set('');
    if (tab === 'members') this.loadCollaboration();
  }

  onSaveBasicInfo() {
    const channel = this.channel();
    if (!channel || this.busy() || !this.can('channel.edit_profile')) return;
    if (!this.contactEmailValid()) return this.error.set(this.i18n.t('auth.emailInvalid'));
    if (!this.name.trim()) return this.error.set(this.i18n.t('channel.nameEmpty'));
    const cleanHandle = this.handle.trim().replace(/^@/, '');
    if (cleanHandle.length < 3) return this.error.set(this.i18n.t('channel.handleLength'));
    const handleChanged = cleanHandle.toLowerCase() !== channel.handle.toLowerCase();
    if (handleChanged && (this.handleChecking() || this.handleValid() === false)) return this.error.set(this.handleFeedback());

    for (const link of this.links) {
      if (link.url.trim() && !this.isValidLinkUrl(link.url)) {
        return this.error.set(this.i18n.t('channel.linkUrlError'));
      }
    }

    const validLinks = this.links.filter(l => l.url.trim()).map(l => ({
      platform: l.platform,
      title: this.linkTitle(l.platform, l.title),
      url: l.url.trim()
    }));
    let currentSettings: Record<string, any> = {};
    try {
      if (channel.settings) {
        currentSettings = typeof channel.settings === 'string' ? JSON.parse(channel.settings) : channel.settings;
      }
    } catch {}
    currentSettings['links'] = validLinks;
    localStorage.setItem('hutube_channel_links_' + channel.channelId, JSON.stringify(validLinks));

    this.runAction(
      this.channelService.updateChannel(channel.channelId, {
        name: this.name.trim(),
        ...(handleChanged ? { handle: cleanHandle } : {}),
        description: this.description.trim(),
        contactEmail: this.contactEmail.trim(),
        settings: JSON.stringify(currentSettings)
      }),
      this.i18n.t('channel.basicSaved')
    );
  }

  linkTitle(platform: ChannelLinkPlatform, current = ''): string {
    if (platform === 'other') return current.trim() || this.i18n.t('channel.otherLink');
    return this.i18n.t('channel.platform.' + platform);
  }

  onPlatformChange(link: ChannelLinkItem) {
    link.title = this.linkTitle(link.platform, link.title);
  }

  private normalizeLink(link: any): ChannelLinkItem {
    const platform = this.detectPlatform(link?.url, link?.platform);
    return { platform, title: this.linkTitle(platform, link?.title || ''), url: link?.url || '' };
  }

  private detectPlatform(url: string, value?: string): ChannelLinkPlatform {
    if (value === 'facebook' || value === 'instagram' || value === 'tiktok' || value === 'x' || value === 'other') return value;
    const lower = String(url || '').toLowerCase();
    if (lower.includes('facebook.com') || lower.includes('fb.me')) return 'facebook';
    if (lower.includes('instagram.com')) return 'instagram';
    if (lower.includes('tiktok.com')) return 'tiktok';
    if (lower.includes('twitter.com') || lower.includes('x.com')) return 'x';
    return 'other';
  }

  private isValidLinkUrl(value: string): boolean {
    try { return ['http:', 'https:'].includes(new URL(value.trim()).protocol); } catch { return false; }
  }

  linkUrlValid(value: string): boolean { return !value.trim() || this.isValidLinkUrl(value); }

  contactEmailValid(): boolean { return validContactEmail(this.contactEmail); }

  onAvatarSelected(event: Event) {
    const channel = this.channel();
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!channel || !file || this.busy() || !this.can('channel.edit_branding')) return;
    const error = avatarFileError(file);
    if (error) { this.error.set(this.i18n.t(error)); return; }
    this.clearFeedback();
    this.avatarCropFile.set(file);
  }

  onAvatarCropped(file: File) {
    this.avatarCropFile.set(null);
    const channel = this.channel();
    if (!channel || this.busy() || !this.can('channel.edit_branding')) return;
    this.runAction(this.channelService.uploadAvatar(channel.channelId, file), this.i18n.t('channel.avatarSaved'));
  }

  onBannerSelected(event: Event) {
    const channel = this.channel();
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!channel || !file || !this.can('channel.edit_branding')) return;
    this.runAction(this.channelService.uploadBanner(channel.channelId, file), this.i18n.t('channel.bannerSaved'));
    input.value = '';
  }

  onAppealEvidenceSelected(event: Event) {
    this.appealEvidenceFile = (event.target as HTMLInputElement).files?.[0] ?? null;
  }

  onWatermarkSelected(event: Event) {
    const channel = this.channel();
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!channel || !file || !this.can('channel.edit_branding')) return;
    this.runAction(this.channelService.uploadWatermark(channel.channelId, file), this.i18n.t('channel.watermarkSaved'));
    input.value = '';
  }

  inviteMember() {
    const channel = this.channel();
    if (!channel || !this.inviteEmail.trim() || !this.can('member.invite')) return;
    this.busy.set(true);
    this.clearFeedback();
    this.channelService.inviteMember(channel.channelId, this.inviteEmail.trim(), this.inviteRole)
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: () => {
          this.inviteEmail = '';
          this.message.set(this.i18n.t('channel.inviteSent'));
          this.loadCollaboration();
        },
        error: err => this.error.set(errorMessage(err, this.i18n))
      });
  }

  changeRole(member: ChannelMember, roleCode: string) {
    const channel = this.channel();
    if (!channel || roleCode === 'owner' || !this.can('member.change_role')) return;
    this.busy.set(true);
    this.clearFeedback();
    this.channelService.changeMemberRole(channel.channelId, member.userId, roleCode as ChannelRole['code'])
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: () => {
          this.message.set(this.i18n.t('channel.roleUpdated', { member: member.displayName }));
          this.loadCollaboration();
        },
        error: err => this.error.set(errorMessage(err, this.i18n))
      });
  }

  async removeMember(member: ChannelMember) {
    const channel = this.channel();
    if (!channel || member.roleCode === 'owner' || !this.can('member.remove')) return;
    if (!(await this.feedback.confirm(this.i18n.t('channel.removeConfirm', { member: member.displayName }), { tone: 'danger' }))) return;
    this.busy.set(true);
    this.clearFeedback();
    this.channelService.removeMember(channel.channelId, member.userId)
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: () => {
          this.message.set(this.i18n.t('channel.memberRemoved'));
          this.loadCollaboration();
        },
        error: err => this.error.set(errorMessage(err, this.i18n))
      });
  }

  revokeInvitation(invitation: ChannelInvitation) {
    const channel = this.channel();
    if (!channel || !this.can('member.invite')) return;
    this.busy.set(true);
    this.clearFeedback();
    this.channelService.revokeInvitation(channel.channelId, invitation.channelInvitationId)
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: () => {
          this.message.set(this.i18n.t('channel.inviteWithdrawn'));
          this.loadCollaboration();
        },
        error: err => this.error.set(errorMessage(err, this.i18n))
      });
  }

  onConfirmDeleteChannel() {
    const channel = this.channel();
    if (!channel || this.busy() || !this.can('channel.delete')) return;
    if (this.confirmChannelName.trim() !== channel.name.trim()) {
      this.error.set(this.i18n.t('channel.confirmNameMismatch'));
      return;
    }

    this.busy.set(true);
    this.clearFeedback();
    this.channelService.deleteChannel(channel.channelId).pipe(finalize(() => this.busy.set(false))).subscribe({
      next: () => {
        this.showDeleteModal.set(false);
        void this.router.navigate(['/account']);
      },
      error: err => this.error.set(errorMessage(err, this.i18n))
    });
  }

  private chooseAvailableTab() {
    if (this.can('channel.edit_profile')) this.activeTab.set('basic');
    else if (this.can('member.view')) this.activeTab.set('members');
    else this.activeTab.set('branding');
  }

  private clearFeedback() {
    this.error.set('');
    this.message.set('');
  }

  private runAction(request: ReturnType<ChannelService['updateChannel']>, successMessage: string) {
    this.busy.set(true);
    this.clearFeedback();
    request.pipe(finalize(() => this.busy.set(false))).subscribe({
      next: updated => {
        this.message.set(successMessage);
        if (updated.handle !== this.channel()?.handle) {
          void this.router.navigate(['/channel', updated.handle, 'customize']).then(() => this.loadChannel(true));
        } else this.loadChannel(true);
      },
      error: err => this.error.set(errorMessage(err, this.i18n))
    });
  }
}
