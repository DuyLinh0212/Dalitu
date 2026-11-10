import { Component, EventEmitter, Input, Output, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CreatePaymentResponse, PaymentSummary, PlanService } from '../../core/plan.service';
import { LocaleCurrencyPipe } from '../../core/locale-currency.pipe';
import { TranslatePipe } from '../../core/translate.pipe';
import { I18nService } from '../../core/i18n.service';
import { Subscription, interval } from 'rxjs';
import { formatPaymentCountdown } from './payment-countdown';

@Component({
  selector: 'app-payment-modal',
  standalone: true,
  imports: [CommonModule, LocaleCurrencyPipe, TranslatePipe],
  template: `
    <div class="modal-backdrop" (click)="close()">
      <div class="modal-card" (click)="$event.stopPropagation()">
        <div class="modal-header">
          <div class="title-wrap">
            <span class="badge">SePay VietQR</span>
            <h2>{{ 'payment.title' | translate:{ plan: payment.planName } }}</h2>
          </div>
          <button class="close-btn" (click)="close()" [attr.aria-label]="'payment.close' | translate">✕</button>
        </div>

        <div class="modal-body">
          <!-- Trạng thái: Đang chờ thanh toán -->
          <ng-container *ngIf="status() === 'pending'">
            <div class="qr-section">
              <div class="qr-wrapper" *ngIf="payment.qrCodeUrl && !qrImageFailed(); else noQr">
                <img [src]="payment.qrCodeUrl" [alt]="'payment.qrAlt' | translate" class="qr-image" (error)="onQrImageError()" />
                <div class="scan-hint">{{ 'payment.scanHint' | translate }}</div>
              </div>
              <ng-template #noQr>
                <div class="no-qr-box">
                  <p>{{ (qrImageFailed() ? 'payment.qrLoadFailed' : 'payment.notConfigured') | translate }}</p>
                  <p class="sub-hint">{{ (qrImageFailed() ? 'payment.qrOpenHint' : 'payment.contactAdmin') | translate }}</p>
                  <a *ngIf="qrImageFailed() && payment.qrCodeUrl" class="open-qr-link" [href]="payment.qrCodeUrl" target="_blank" rel="noopener noreferrer">
                    {{ 'payment.openQrDirectly' | translate }}
                  </a>
                </div>
              </ng-template>

              <div class="info-list">
                <div class="info-item">
                  <span class="label">{{ 'payment.amount' | translate }}</span>
                  <span class="value amount">{{ payment.amount | localeCurrency:payment.currency }}</span>
                </div>
                <div class="info-item">
                  <span class="label">{{ 'payment.transferContent' | translate }}</span>
                  <div class="code-box">
                    <span class="code">{{ payment.transactionCode }}</span>
                    <button class="copy-btn" (click)="copyCode()" type="button">
                      {{ (copied() ? 'payment.copied' : 'payment.copy') | translate }}
                    </button>
                  </div>
                </div>
                <div class="info-item countdown">
                  <span class="label">{{ 'payment.expires' | translate }}</span>
                  <span class="value timer">{{ remainingTime() }}</span>
                </div>
              </div>
            </div>

            <div class="waiting-indicator">
              <span class="spinner"></span>
              <span>{{ 'payment.waiting' | translate }}</span>
            </div>
            <p *ngIf="cancelError()" class="cancel-error" role="alert">{{ cancelError() }}</p>
            <div class="pending-actions">
              <button class="cancel-btn" type="button" [disabled]="cancelling()" (click)="cancelPending()">
                {{ (cancelling() ? 'payment.cancelling' : 'payment.cancel') | translate }}
              </button>
            </div>
          </ng-container>

          <!-- Trạng thái: Thành công -->
          <div *ngIf="status() === 'paid'" class="result-box success">
            <div class="icon">✅</div>
            <h3>{{ 'payment.successTitle' | translate }}</h3>
            <p>{{ 'payment.successDescription' | translate:{ plan: payment.planName } }}</p>
            <button class="action-btn" (click)="onSuccessDone()">{{ 'payment.complete' | translate }}</button>
          </div>

          <!-- Trạng thái: Hết hạn / Huỷ -->
          <div *ngIf="status() === 'cancelled'" class="result-box failed">
            <div class="icon">⏰</div>
            <h3>{{ (expired() ? 'payment.expiredTitle' : 'payment.cancelledTitle') | translate }}</h3>
            <p>{{ (expired() ? 'payment.expiredDescription' : 'payment.cancelledDescription') | translate }}</p>
            <button class="action-btn" (click)="close()">{{ 'payment.close' | translate }}</button>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    .modal-backdrop {
      position: fixed; inset: 0; background: rgba(0, 0, 0, 0.7);
      display: flex; align-items: center; justify-content: center; z-index: 1000;
      backdrop-filter: blur(4px); padding: 16px; box-sizing: border-box; overflow-y: auto;
    }
    .modal-card {
      background: var(--surface-primary, #1e1e24); color: var(--text-primary, #fff);
      border-radius: 16px; width: 100%; max-width: 480px; padding: 24px; box-sizing: border-box; max-height: calc(100dvh - 32px); overflow-y: auto;
      box-shadow: 0 12px 32px rgba(0,0,0,0.5); border: 1px solid rgba(255,255,255,0.1);
    }
    .modal-header {
      display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 20px;
    }
    .title-wrap h2 { margin: 6px 0 0; font-size: 1.25rem; }
    .badge {
      font-size: 0.75rem; background: #0066cc; color: #fff; padding: 2px 8px;
      border-radius: 6px; font-weight: 600; text-transform: uppercase;
    }
    .close-btn {
      background: none; border: none; font-size: 1.2rem; color: var(--text-secondary, #aaa);
      cursor: pointer; padding: 4px 8px; border-radius: 6px;
    }
    .close-btn:hover { color: #fff; background: rgba(255,255,255,0.1); }
    .qr-wrapper {
      text-align: center; margin-bottom: 20px; background: #fff; padding: 16px;
      border-radius: 12px; display: inline-block; width: 100%; box-sizing: border-box;
    }
    .qr-image { width: min(220px, 100%); height: auto; aspect-ratio: 1; object-fit: contain; margin: 0 auto; display: block; }
    .scan-hint { color: #333; font-size: 0.85rem; margin-top: 8px; font-weight: 500; }
    .no-qr-box {
      text-align: center; padding: 24px; background: rgba(255,255,255,0.05);
      border-radius: 12px; margin-bottom: 20px;
    }
    .no-qr-box .sub-hint { font-size: 0.85rem; opacity: 0.7; }
    .open-qr-link { color: #93c5fd; font-size: 0.9rem; font-weight: 600; text-decoration: underline; }
    .info-list { display: flex; flex-direction: column; gap: 12px; margin-bottom: 20px; }
    .info-item { display: flex; justify-content: space-between; align-items: center; font-size: 0.95rem; }
    .info-item .label { opacity: 0.8; }
    .info-item .value.amount { font-size: 1.2rem; font-weight: 700; color: #4ade80; }
    .code-box { display: flex; align-items: center; gap: 8px; }
    .code {
      font-family: monospace; font-weight: 700; background: rgba(255,255,255,0.1);
      padding: 4px 8px; border-radius: 6px; letter-spacing: 1px; color: #fbbf24;
    }
    .copy-btn {
      background: rgba(255,255,255,0.15); border: none; color: #fff; padding: 4px 8px;
      border-radius: 4px; font-size: 0.8rem; cursor: pointer;
    }
    .copy-btn:hover { background: rgba(255,255,255,0.25); }
    .countdown .timer { font-weight: 600; color: #f87171; }
    .waiting-indicator {
      display: flex; align-items: center; justify-content: center; gap: 10px;
      font-size: 0.85rem; opacity: 0.8; padding: 12px;
      background: rgba(255,255,255,0.03); border-radius: 8px;
    }
    .spinner {
      width: 16px; height: 16px; border: 2px solid rgba(255,255,255,0.2);
      border-top-color: #3b82f6; border-radius: 50%;
      animation: spin 1s linear infinite; display: inline-block;
    }
    @keyframes spin { to { transform: rotate(360deg); } }
    .result-box { text-align: center; padding: 24px 0; }
    .result-box .icon { font-size: 3rem; margin-bottom: 12px; }
    .result-box h3 { margin-bottom: 8px; }
    .result-box p { opacity: 0.85; margin-bottom: 20px; font-size: 0.95rem; }
    .action-btn {
      background: #2563eb; color: #fff; border: none; padding: 10px 24px;
      border-radius: 8px; font-weight: 600; cursor: pointer;
    }
    .action-btn:hover { background: #1d4ed8; }
    .pending-actions { display: flex; justify-content: center; margin-top: 12px; }
    .cancel-btn { padding: 8px 16px; border: 1px solid rgba(255,255,255,.22); border-radius: 8px; background: transparent; color: inherit; font: inherit; cursor: pointer; }
    .cancel-btn:disabled { opacity: .6; cursor: wait; }
    .cancel-error { margin: 10px 0 0; color: #fca5a5; text-align: center; font-size: .85rem; }
  `]
})
export class PaymentModalComponent implements OnInit, OnDestroy {
  @Input({ required: true }) payment!: CreatePaymentResponse;
  @Output() completed = new EventEmitter<void>();
  @Output() dismissed = new EventEmitter<void>();

  private readonly planService = inject(PlanService);
  private readonly i18n = inject(I18nService);

  readonly status = signal<'pending' | 'paid' | 'cancelled'>('pending');
  readonly copied = signal(false);
  readonly remainingTime = signal('');
  readonly cancelling = signal(false);
  readonly cancelError = signal('');
  readonly expired = signal(false);
  readonly qrImageFailed = signal(false);

  private pollSub?: Subscription;
  private timerSub?: Subscription;

  ngOnInit() {
    this.startCountdown();
    this.startPolling();
  }

  ngOnDestroy() {
    this.pollSub?.unsubscribe();
    this.timerSub?.unsubscribe();
  }

  copyCode() {
    const code = this.payment.transactionCode;
    navigator.clipboard.writeText(code).then(() => {
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 2000);
    });
  }

  onQrImageError() {
    this.qrImageFailed.set(true);
  }

  close() {
    this.dismissed.emit();
  }

  onSuccessDone() {
    this.completed.emit();
  }

  cancelPending(expired = false) {
    if (this.cancelling() || this.status() !== 'pending') return;
    this.cancelling.set(true);
    this.expired.set(expired);
    this.cancelError.set('');
    this.planService.cancelPayment(this.payment.paymentId).subscribe({
      next: () => {
        this.status.set('cancelled');
        this.pollSub?.unsubscribe();
        this.timerSub?.unsubscribe();
      },
      error: () => {
        this.cancelError.set(this.i18n.t('payment.cancelError'));
        this.cancelling.set(false);
      }
    });
  }

  private startCountdown() {
    const expiresAt = new Date(this.payment.expiresAt).getTime();
    const update = () => {
      const now = Date.now();
      const diff = Math.max(0, Math.floor((expiresAt - now) / 1000));
      if (diff <= 0) {
        this.remainingTime.set(this.i18n.t('payment.expired'));
        if (this.status() === 'pending') {
          this.cancelPending(true);
        }
        return;
      }
      this.remainingTime.set(formatPaymentCountdown(diff));
    };
    update();
    this.timerSub = interval(1000).subscribe(update);
  }

  private startPolling() {
    // Poll mỗi 4 giây để kiểm tra trạng thái thanh toán từ webhook SePay
    this.pollSub = interval(4000).subscribe(() => {
      this.planService.getPayment(this.payment.paymentId).subscribe({
        next: (p: PaymentSummary) => {
          if (p.status === 'paid') {
            this.status.set('paid');
            this.pollSub?.unsubscribe();
            this.timerSub?.unsubscribe();
          } else if (p.status === 'cancelled' || p.status === 'failed') {
            this.status.set('cancelled');
            this.pollSub?.unsubscribe();
            this.timerSub?.unsubscribe();
          }
        },
        error: () => undefined
      });
    });
  }
}
