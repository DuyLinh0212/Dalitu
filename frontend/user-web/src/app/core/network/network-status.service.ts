import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { RuntimeConfig } from '../runtime-config';

@Injectable({ providedIn: 'root' })
export class NetworkStatusService implements OnDestroy {
  private readonly config = inject(RuntimeConfig);
  private checkFlight?: Promise<boolean>;
  private retryTimer?: number;
  private nextProbeAt = 0;
  private failedProbes = 0;

  readonly unavailable = signal(typeof navigator !== 'undefined' && !navigator.onLine);
  readonly checking = signal(false);
  readonly dismissed = signal(false);
  readonly shouldNotify = computed(() => this.unavailable() && !this.dismissed());

  constructor() {
    if (typeof window === 'undefined') return;

    window.addEventListener('offline', this.onOffline);
    window.addEventListener('online', this.onOnline);
    this.retryTimer = window.setInterval(() => {
      if (this.unavailable() && !document.hidden) void this.checkConnection();
    }, 15_000);

    // RuntimeConfig already probes the API during startup. Do not duplicate it.
  }

  ngOnDestroy(): void {
    if (typeof window === 'undefined') return;
    window.clearInterval(this.retryTimer);
    window.removeEventListener('offline', this.onOffline);
    window.removeEventListener('online', this.onOnline);
  }

  markUnavailable(): void {
    if (!this.unavailable()) {
      this.dismissed.set(false);
      this.nextProbeAt = Date.now() + 30_000;
    }
    this.unavailable.set(true);
  }

  markAvailable(): void {
    this.failedProbes = 0;
    this.nextProbeAt = 0;
    this.unavailable.set(false);
    this.dismissed.set(false);
  }

  dismiss(): void {
    if (!this.unavailable()) return;
    this.dismissed.set(true);
  }

  async checkConnection(manual = false): Promise<boolean> {
    if (this.checkFlight) return this.checkFlight;
    if (!manual && Date.now() < this.nextProbeAt) return false;
    if (typeof window === 'undefined' || !this.config.apiBaseUrl) return false;
    if (typeof navigator !== 'undefined' && !navigator.onLine) {
      this.markUnavailable();
      return false;
    }

    this.checking.set(true);
    const controller = new AbortController();
    const timeoutId = window.setTimeout(() => controller.abort(), 8_000);
    const flight = fetch(`${this.config.apiBaseUrl}/system/config`, {
      cache: 'no-store',
      signal: controller.signal,
    })
      .then(response => {
        if (!response.ok && response.status !== 429) throw new Error('API_UNAVAILABLE');
        this.markAvailable();
        return true;
      })
      .catch(() => {
        this.markUnavailable();
        this.failedProbes++;
        this.nextProbeAt = Date.now() + Math.min(120_000, 30_000 * 2 ** Math.min(this.failedProbes - 1, 2));
        return false;
      })
      .finally(() => {
        window.clearTimeout(timeoutId);
        this.checking.set(false);
        this.checkFlight = undefined;
      });

    this.checkFlight = flight;
    return flight;
  }

  private readonly onOffline = (): void => this.markUnavailable();
  private readonly onOnline = (): void => {
    void this.checkConnection();
  };
}
