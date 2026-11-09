import { DOCUMENT } from '@angular/common';
import { Component, DestroyRef, ElementRef, ViewChild, afterNextRender, effect, inject, input, output, signal } from '@angular/core';
import { I18nService } from '../../core/i18n.service';
import { TranslatePipe } from '../../core/translate.pipe';
import { CHANNEL_BRANDING_TRANSLATIONS } from './channel-branding.translations';

@Component({
  selector: 'app-channel-avatar-cropper',
  imports: [TranslatePipe],
  templateUrl: './channel-avatar-cropper.component.html',
  styleUrl: './channel-avatar-cropper.component.scss',
})
export class ChannelAvatarCropperComponent {
  readonly file = input.required<File>();
  readonly confirmed = output<File>();
  readonly cancelled = output<void>();
  readonly loaded = signal(false);
  readonly saving = signal(false);
  readonly error = signal('');
  readonly zoom = signal(1);
  readonly centerX = signal(0);
  readonly centerY = signal(0);
  readonly source = signal('');
  private image?: HTMLImageElement;
  private destroyed = false;
  private drag?: { id: number; x: number; y: number; centerX: number; centerY: number };
  private readonly document = inject(DOCUMENT);
  private readonly i18n = inject(I18nService);
  @ViewChild('dialog', { static: true }) private dialog!: ElementRef<HTMLDialogElement>;
  @ViewChild('stage') private stage?: ElementRef<HTMLElement>;

  constructor() {
    this.i18n.registerTranslations(CHANNEL_BRANDING_TRANSLATIONS);
    effect(onCleanup => {
      const url = URL.createObjectURL(this.file());
      let active = true;
      this.source.set(url);
      const image = new Image();
      image.onload = () => {
        if (!active) return;
        this.image = image;
        this.reset();
        this.loaded.set(true);
      };
      image.onerror = () => { if (active) this.error.set(this.i18n.t('channel.avatarReadError')); };
      image.src = url;
      onCleanup(() => { active = false; URL.revokeObjectURL(url); });
    });
    let previousOverflow = '';
    let previousFocus: HTMLElement | null = null;
    afterNextRender(() => {
      previousFocus = this.document.activeElement as HTMLElement;
      previousOverflow = this.document.body.style.overflow;
      this.document.body.style.overflow = 'hidden';
      this.dialog.nativeElement.showModal();
    });
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.dialog.nativeElement.close();
      this.document.body.style.overflow = previousOverflow;
      previousFocus?.focus({ preventScroll: true });
    });
  }

  cropSize(): number { return this.image ? Math.min(this.image.naturalWidth, this.image.naturalHeight) / this.zoom() : 1; }
  imageWidth(): number { return (this.image?.naturalWidth ?? 1) / this.cropSize() * 100; }
  imageHeight(): number { return (this.image?.naturalHeight ?? 1) / this.cropSize() * 100; }
  imageLeft(): number { return (0.5 - this.centerX() / this.cropSize()) * 100; }
  imageTop(): number { return (0.5 - this.centerY() / this.cropSize()) * 100; }

  reset() {
    this.zoom.set(1);
    this.centerX.set((this.image?.naturalWidth ?? 0) / 2);
    this.centerY.set((this.image?.naturalHeight ?? 0) / 2);
  }

  setZoom(value: string) {
    this.zoom.set(Math.max(1, Math.min(4, Number(value) || 1)));
    this.moveTo(this.centerX(), this.centerY());
  }

  private moveTo(x: number, y: number) {
    if (!this.image) return;
    const half = this.cropSize() / 2;
    this.centerX.set(Math.max(half, Math.min(this.image.naturalWidth - half, x)));
    this.centerY.set(Math.max(half, Math.min(this.image.naturalHeight - half, y)));
  }

  startDrag(event: PointerEvent) {
    if (!this.loaded() || this.saving() || !event.isPrimary || event.button !== 0) return;
    const target = event.currentTarget as HTMLElement;
    target.focus({ preventScroll: true });
    target.setPointerCapture(event.pointerId);
    this.drag = { id: event.pointerId, x: event.clientX, y: event.clientY, centerX: this.centerX(), centerY: this.centerY() };
  }

  moveDrag(event: PointerEvent) {
    if (!this.drag || this.drag.id !== event.pointerId || !this.stage) return;
    const scale = this.cropSize() / this.stage.nativeElement.clientWidth;
    this.moveTo(this.drag.centerX - (event.clientX - this.drag.x) * scale,
      this.drag.centerY - (event.clientY - this.drag.y) * scale);
  }

  endDrag() { this.drag = undefined; }

  moveWithKeyboard(event: KeyboardEvent) {
    const directions: Record<string, number[]> = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
    const delta = directions[event.key];
    if (!delta || this.saving()) return;
    event.preventDefault();
    const step = this.cropSize() * (event.shiftKey ? 0.1 : 0.025);
    this.moveTo(this.centerX() + delta[0] * step, this.centerY() + delta[1] * step);
  }

  cancel(event?: Event) {
    event?.preventDefault();
    if (!this.saving()) this.cancelled.emit();
  }

  apply() {
    if (!this.image || !this.loaded() || this.saving()) return;
    this.saving.set(true);
    this.error.set('');
    try {
      const canvas = this.document.createElement('canvas');
      canvas.width = canvas.height = 512;
      const context = canvas.getContext('2d');
      if (!context) throw new Error('Canvas unavailable');
      context.beginPath();
      context.arc(256, 256, 256, 0, Math.PI * 2);
      context.clip();
      const size = this.cropSize();
      context.drawImage(this.image, this.centerX() - size / 2, this.centerY() - size / 2, size, size, 0, 0, 512, 512);
      canvas.toBlob(blob => {
        if (this.destroyed) return;
        this.saving.set(false);
        if (!blob) { this.error.set(this.i18n.t('channel.avatarCropError')); return; }
        this.confirmed.emit(new File([blob], 'channel-avatar.png', { type: 'image/png' }));
      }, 'image/png');
    } catch {
      this.saving.set(false);
      this.error.set(this.i18n.t('channel.avatarCropError'));
    }
  }
}
