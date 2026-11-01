import { HttpBackend, HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import {
  Observable,
  TimeoutError,
  catchError,
  defer,
  finalize,
  firstValueFrom,
  map,
  of,
  retry,
  shareReplay,
  switchMap,
  timer,
  tap,
  throwError,
  timeout,
} from 'rxjs';
import { ADMIN_APP, RuntimeConfig } from './runtime-config';
import { I18nService } from './i18n.service';
import { NetworkStatusService } from './network/network-status.service';
import { ReadCacheStore } from './read-cache.interceptor';

export interface User { userId: string; username: string; email: string; displayName: string; emailVerified: boolean; isAdmin: boolean; }
export interface LoginResponse { accessToken: string; expiresAt: string; user: User; }
export interface Session { sessionId: string; deviceName: string; platform: string; issuedAt: string; lastActiveAt: string; expiresAt: string; isCurrent: boolean; ipAddress?: string | null; deviceId?: string | null; countryCode?: string | null; region?: string | null; city?: string | null; latitude?: number | null; longitude?: number | null; }
export interface LoginHistoryItem { loginHistoryId: string; deviceId: string; deviceName: string; platform: string; ipAddress?: string | null; countryCode?: string | null; region?: string | null; city?: string | null; latitude?: number | null; longitude?: number | null; loginAt: string; }
export interface LoginHistoryPage { items: LoginHistoryItem[]; page: number; pageSize: number; total: number; }
export interface Message { message: string; }
export function isSessionRejected(error: unknown): boolean {
  return error instanceof HttpErrorResponse && (error.status === 401 || error.status === 403);
}
export interface ClientGeoPayload {
  latitude?: number;
  longitude?: number;
  city?: string;
  region?: string;
  countryCode?: string;
}

function formatDetailedAddress(data: Record<string, unknown>): ClientGeoPayload {
  const localityInfo = (data['localityInfo'] as Record<string, unknown> | undefined) || {};
  const admin = (localityInfo['administrative'] as Array<Record<string, unknown>> | undefined) || [];
  const informative = (localityInfo['informative'] as Array<Record<string, unknown>> | undefined) || [];

  const districtObj = informative.find(item =>
    /^(quận|huyện|thị xã|thành phố)\s/i.test(String(item['name'] || '')) ||
    /quận|huyện/i.test(String(item['description'] || ''))
  ) || admin.find(item =>
    /^(quận|huyện|thị xã)\s/i.test(String(item['name'] || ''))
  );

  const rawLocality = typeof data['locality'] === 'string' ? data['locality'].trim() : '';
  const districtName = districtObj?.['name']
    ? String(districtObj['name']).trim()
    : (/^(quận|huyện|thị xã|tp\.)/i.test(rawLocality) ? rawLocality : '');

  const wardObj = admin.find(item =>
    /phường|xã|thị trấn/i.test(String(item['description'] || '')) ||
    /^(phường|xã|thị trấn)\s/i.test(String(item['name'] || ''))
  );
  const rawWard = wardObj?.['name'] ? String(wardObj['name']).trim() : '';
  const candidateWard = rawWard || (!districtName || !districtName.includes(rawLocality) ? rawLocality : '');
  const wardName = candidateWard
    ? (/^(phường|xã|thị trấn)\s/i.test(candidateWard) ? candidateWard : `Phường ${candidateWard}`)
    : '';

  const rawProvince = typeof data['principalSubdivision'] === 'string' && data['principalSubdivision'].trim()
    ? data['principalSubdivision'].trim()
    : (typeof data['city'] === 'string' ? data['city'].trim() : '');

  const localDetails = [wardName, districtName]
    .filter(Boolean)
    .filter((val, idx, arr) => arr.indexOf(val) === idx);

  const city = localDetails.length > 0 ? localDetails.join(', ') : (rawLocality || (typeof data['city'] === 'string' ? data['city'] : ''));

  return {
    latitude: typeof data['latitude'] === 'number' ? data['latitude'] : undefined,
    longitude: typeof data['longitude'] === 'number' ? data['longitude'] : undefined,
    city: city ? city.slice(0, 120) : undefined,
    region: rawProvince ? rawProvince.slice(0, 120) : undefined,
    countryCode: typeof data['countryCode'] === 'string' ? data['countryCode'].slice(0, 8) : undefined
  };
}

export async function fetchIpLocation(): Promise<ClientGeoPayload> {
  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 2000);
    const res = await fetch('https://api.bigdatacloud.net/data/reverse-geocode-client?localityLanguage=vi', {
      signal: controller.signal
    });
    clearTimeout(timer);
    if (res.ok) {
      const data = await res.json();
      return formatDetailedAddress(data);
    }
  } catch {
    // ignore
  }
  return {};
}

export async function captureBrowserLocation(): Promise<ClientGeoPayload> {
  if (typeof navigator === 'undefined') {
    return {};
  }
  if (!navigator.geolocation) {
    return fetchIpLocation();
  }
  return new Promise<ClientGeoPayload>((resolve) => {
    let resolved = false;
    const done = (val: ClientGeoPayload) => {
      if (!resolved) {
        resolved = true;
        resolve(val);
      }
    };
    const timer = setTimeout(async () => {
      done(await fetchIpLocation());
    }, 2000);

    navigator.geolocation.getCurrentPosition(
      async (pos) => {
        clearTimeout(timer);
        const { latitude, longitude } = pos.coords;
        try {
          const controller = new AbortController();
          const geoTimer = setTimeout(() => controller.abort(), 1500);
          const res = await fetch(
            `https://api.bigdatacloud.net/data/reverse-geocode-client?latitude=${latitude}&longitude=${longitude}&localityLanguage=vi`,
            { signal: controller.signal }
          );
          clearTimeout(geoTimer);
          if (res.ok) {
            const data = await res.json();
            const formatted = formatDetailedAddress(data);
            done({ ...formatted, latitude, longitude });
            return;
          }
        } catch {
          // fallback below
        }
        const ipLoc = await fetchIpLocation();
        done({ ...ipLoc, latitude, longitude });
      },
      async () => {
        clearTimeout(timer);
        done(await fetchIpLocation());
      },
      { timeout: 1800, maximumAge: 300_000, enableHighAccuracy: false }
    );
  });
}

function stableDeviceId(): string {
  if (typeof document === 'undefined') return '';
  const key = `hutube_device_id_${ADMIN_APP ? 'admin' : 'user'}`;
  const existing = document.cookie.split('; ').find(item => item.startsWith(`${key}=`))?.slice(key.length + 1);
  if (existing) return decodeURIComponent(existing);
  const generated = typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
  try { document.cookie = `${key}=${encodeURIComponent(generated)}; Max-Age=31536000; Path=/; SameSite=Lax`; } catch { /* non-browser host */ }
  return generated;
}

function browserDeviceName(google = false, mobileLabel: string, desktopLabel: string): string {
  const agent = typeof navigator !== 'undefined' ? navigator.userAgent : '';
  const mobile = /android|iphone|ipad|ipod|mobile/i.test(agent);
  const kind = mobile ? mobileLabel : desktopLabel;
  const browser = /coc_coc_browser/i.test(agent) ? 'Cốc Cốc'
    : /edg\//i.test(agent) ? 'Microsoft Edge'
    : /opr\//i.test(agent) || /opera/i.test(agent) ? 'Opera'
    : /firefox\//i.test(agent) ? 'Firefox'
    : /samsungbrowser/i.test(agent) ? 'Samsung Internet'
    : /chrome\//i.test(agent) ? 'Chrome'
    : /safari\//i.test(agent) ? 'Safari'
    : 'Browser';
  const operatingSystem = /windows/i.test(agent) ? 'Windows'
    : /android/i.test(agent) ? 'Android'
    : /iphone|ipad|ipod/i.test(agent) ? 'iOS'
    : /macintosh|mac os/i.test(agent) ? 'macOS'
    : /cros/i.test(agent) ? 'ChromeOS'
    : /linux/i.test(agent) ? 'Linux'
    : '';
  const application = ADMIN_APP ? 'Dalitu Admin' : `Dalitu Web${google ? ' / Google' : ''}`;
  return [application, browser, operatingSystem, kind].filter(Boolean).join(' · ').slice(0, 200);
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private raw = new HttpClient(inject(HttpBackend));
  private http = inject(HttpClient);
  private config = inject(RuntimeConfig);
  private readonly readCache = inject(ReadCacheStore);
  private networkStatus = inject(NetworkStatusService);
  readonly i18n = inject(I18nService);
  readonly user = signal<User | null>(null);
  readonly accessToken = signal<string | null>(null);
  private refreshFlight?: Observable<LoginResponse>;
  private restoreFlight?: Observable<boolean>;
  private readonly verificationFlights = new Map<string, Observable<Message>>();
  private generation = 0;
  private restored = false;
  private accessTokenExpiresAt = 0;
  get sessionVersion(): number { return this.generation; }
  private get headers(): HttpHeaders {
    let headers = new HttpHeaders({ 'X-HuTube-Client': 'web' });
    if (ADMIN_APP) headers = headers.set('X-HuTube-App', 'admin');
    return headers;
  }
  private trackRawNetwork<T>(request: Observable<T>): Observable<T> {
    return request.pipe(
      timeout({ first: 15_000 }),
      tap(() => this.networkStatus.markAvailable()),
      catchError((error: unknown) => {
        if (error instanceof TimeoutError || (error instanceof HttpErrorResponse && (error.status === 0 || error.status >= 500))) {
          this.networkStatus.markUnavailable();
        } else if (error instanceof HttpErrorResponse && error.status > 0) {
          this.networkStatus.markAvailable();
        }
        return throwError(() => error);
      }),
    );
  }
  private isTransientNetworkError(error: unknown): boolean {
    return error instanceof TimeoutError || (error instanceof HttpErrorResponse && (
      error.status === 0 || error.status === 408 || error.status >= 500
    ));
  }
  private post<T>(path: string, body: unknown): Observable<T> {
    return this.trackRawNetwork(
      this.raw.post<T>(this.config.apiBaseUrl + path, body, { withCredentials: true, headers: this.headers }),
    );
  }
  clear(): void { this.readCache.clear(); this.generation++; this.restored = true; this.user.set(null); this.accessToken.set(null); this.accessTokenExpiresAt = 0; }
  private accept(response: LoginResponse): void {
    this.accessTokenExpiresAt = Date.parse(response.expiresAt) || 0;
    this.accessToken.set(response.accessToken); this.user.set(response.user); this.restored = true;
  }
  ensureFreshAccessToken(): Observable<string> {
    const token = this.accessToken();
    return token && this.accessTokenExpiresAt > Date.now() + 120_000
      ? of(token) : this.refresh().pipe(map(response => response.accessToken));
  }
  login(email: string, password: string, location?: ClientGeoPayload): Observable<User> {
    const generation = ++this.generation;
    const body: Record<string, unknown> = {
      email,
      password,
      platform: ADMIN_APP ? 'admin' : 'web',
      deviceName: browserDeviceName(false, this.i18n.t('auth.deviceMobile'), this.i18n.t('auth.deviceDesktop')),
      deviceId: stableDeviceId(),
    };
    if (location?.latitude != null) body['latitude'] = location.latitude;
    if (location?.longitude != null) body['longitude'] = location.longitude;
    if (location?.city) body['city'] = location.city;
    if (location?.region) body['region'] = location.region;
    if (location?.countryCode) body['countryCode'] = location.countryCode;

    return this.post<LoginResponse>('/auth/login', body).pipe(
      tap(response => { if (generation !== this.generation) throw new Error(this.i18n.t('auth.loginCancelled')); this.accept(response); }),
      switchMap(() => this.me()),
      catchError(error => { if (generation === this.generation) this.clear(); return throwError(() => error); })
    );
  }
  google(credential: string, location?: ClientGeoPayload): Observable<User> {
    const generation = ++this.generation;
    const body: Record<string, unknown> = {
      credential,
      platform: 'web',
      deviceName: browserDeviceName(true, this.i18n.t('auth.deviceMobile'), this.i18n.t('auth.deviceDesktop')),
      deviceId: stableDeviceId(),
    };
    if (location?.latitude != null) body['latitude'] = location.latitude;
    if (location?.longitude != null) body['longitude'] = location.longitude;
    if (location?.city) body['city'] = location.city;
    if (location?.region) body['region'] = location.region;
    if (location?.countryCode) body['countryCode'] = location.countryCode;

    return this.post<LoginResponse>('/auth/google', body).pipe(
      tap(response => { if (generation !== this.generation) throw new Error(this.i18n.t('auth.loginCancelled')); this.accept(response); }),
      switchMap(() => this.me()),
      catchError(error => { if (generation === this.generation) this.clear(); return throwError(() => error); })
    );
  }
  refresh(): Observable<LoginResponse> {
    if (!this.refreshFlight) {
      const generation = this.generation;
      // Serialize refresh cookie rotation across same-origin tabs as well as within this tab.
      const request = () => firstValueFrom(this.post<LoginResponse>('/auth/refresh', {
        deviceName: browserDeviceName(false, this.i18n.t('auth.deviceMobile'), this.i18n.t('auth.deviceDesktop'))
      }));
      this.refreshFlight = defer(async () => typeof navigator !== 'undefined' && navigator.locks
        ? await navigator.locks.request('hutube-refresh-' + (ADMIN_APP ? 'admin' : 'web'), request)
        : await request()).pipe(
        tap(response => { if (generation !== this.generation) throw new Error(this.i18n.t('auth.sessionEnded')); this.accept(response); }),
        catchError(error => {
          if (generation === this.generation && isSessionRejected(error)) this.clear();
          return throwError(() => error);
        }),
        finalize(() => { this.refreshFlight = undefined; }),
        shareReplay({ bufferSize: 1, refCount: false })
      );
    }
    return this.refreshFlight;
  }
  restore(): Observable<boolean> {
    const generation = this.generation;
    const denied = (error: unknown) => {
      if (generation !== this.generation) return of(false);
      if (!isSessionRejected(error)) return throwError(() => error);
      this.clear();
      return of(false);
    };
    if (this.restored && !this.accessToken()) return of(false);
    if (!this.restoreFlight) {
      const restoreAttempt = () => {
        if (generation !== this.generation || (this.restored && !this.accessToken())) return of(false);
        return this.accessToken()
          ? this.me().pipe(map(() => true))
          : this.refresh().pipe(switchMap(() => this.me()), map(() => true));
      };
      this.restoreFlight = defer(restoreAttempt).pipe(
        retry({ count: 2, delay: error => this.isTransientNetworkError(error) && generation === this.generation
          ? timer(5_000)
          : throwError(() => error) }),
        timeout({ first: 30_000 }),
        catchError(denied),
        finalize(() => { this.restoreFlight = undefined; }),
        shareReplay({ bufferSize: 1, refCount: false })
      );
    }
    return this.restoreFlight;
  }
  me(): Observable<User> {
    const generation = this.generation;
    return this.http.get<User>(this.config.apiBaseUrl + (ADMIN_APP ? '/admin/me' : '/auth/me')).pipe(tap(user => {
      if (generation !== this.generation) throw new Error(this.i18n.t('auth.sessionChanged'));
      this.user.set(user);
    }));
  }
  register(body: unknown) { return this.post<Message>('/auth/register', body); }
  verify(token: string): Observable<Message> {
    const existing = this.verificationFlights.get(token);
    if (existing) return existing;
    const request = this.post<Message>('/auth/verify-email', { token }).pipe(
      finalize(() => {
        if (this.verificationFlights.get(token) === request) this.verificationFlights.delete(token);
      }),
      shareReplay({ bufferSize: 1, refCount: false })
    );
    this.verificationFlights.set(token, request);
    return request;
  }
  resend(email: string) { return this.post<Message>('/auth/resend-verification', { email }); }
  forgot(email: string) { return this.post<Message>('/auth/forgot-password', { email }); }
  reset(email: string, code: string, password: string) { return this.post<Message>('/auth/reset-password', { email, code, password }).pipe(tap(() => this.clear())); }
  sessions() { return this.http.get<{ items: Session[] }>(this.config.apiBaseUrl + '/auth/sessions'); }
  loginHistory(page = 1, pageSize = 20) {
    return this.http.get<LoginHistoryPage>(this.config.apiBaseUrl + '/auth/login-history', { params: { page, pageSize } });
  }
  logoutOthers() { return this.http.post<Message>(this.config.apiBaseUrl + '/auth/logout-others', {}); }
  logoutAll() { return this.http.post<Message>(this.config.apiBaseUrl + '/auth/logout-all', {}).pipe(tap(() => this.clear())); }
  currentSessionId(): string | null {
    const token = this.accessToken();
    if (!token) return null;
    try {
      const encoded = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
      const payload = JSON.parse(atob(encoded.padEnd(encoded.length + (4 - encoded.length % 4) % 4, '=')));
      return typeof payload.sid === 'string' ? payload.sid : null;
    } catch { return null; }
  }
  revoke(id: string) { return this.http.delete<Message>(this.config.apiBaseUrl + '/auth/sessions/' + encodeURIComponent(id)); }
  logout() { this.clear(); return this.post<Message>('/auth/logout', {}); }
  info() { return this.trackRawNetwork(this.raw.get(this.config.apiBaseUrl + '/system/info')); }
}

export function safeReturnUrl(value: string | null): string {
  if (!value || !value.startsWith('/') || value.startsWith('//') || /[\\\r\n]/.test(value) || /^\/(login|register|verify-email|forgot-password|reset-password)([/?#]|$)/.test(value)) return '/home';
  // Decode before checking to reject encoded protocol-relative destinations.
  try { const decoded = decodeURIComponent(value); if (decoded.startsWith('//') || /[\\\r\n]/.test(decoded)) return '/home'; } catch { return '/home'; }
  return value;
}

export function errorMessage(error: unknown, i18n?: I18nService): string {
  const fallback = (key: string) => i18n?.t(key) ?? ({
    'auth.requestError': 'Không thể hoàn tất yêu cầu. Vui lòng thử lại.',
    'auth.serverUnavailable': 'Chưa kết nối được máy chủ. Kiểm tra kết nối và thử lại.',
    'auth.tooManyRequests': 'Bạn đã thử quá nhiều lần. Vui lòng đợi một lát rồi thử lại.',
    'auth.accessDenied': 'Tài khoản không có quyền truy cập hoặc đã bị vô hiệu hóa.'
  }[key] || key);
  if (error instanceof TimeoutError) return fallback('auth.serverUnavailable');
  if (!(error instanceof HttpErrorResponse)) return fallback('auth.requestError');
  const keys: Record<string, string> = {
    INVALID_CREDENTIALS: 'auth.invalidCredentials',
    EMAIL_NOT_VERIFIED: 'auth.emailNotVerified',
    EMAIL_UNVERIFIED: 'auth.emailNotVerified',
    ACCOUNT_SUSPENDED: 'auth.accountSuspended',
    ACCOUNT_BANNED: 'auth.accountBanned',
    ADMIN_ACCESS_DENIED: 'auth.adminAccessDenied',
    ADMIN_DISABLED: 'auth.adminDisabled',
    EMAIL_EXISTS: 'auth.emailExists',
    USERNAME_EXISTS: 'auth.usernameExists',
    INVALID_TOKEN: 'auth.invalidToken',
    TOKEN_EXPIRED: 'auth.tokenExpired',
    GOOGLE_LOGIN_NOT_CONFIGURED: 'auth.googleNotConfigured',
    INVALID_GOOGLE_TOKEN: 'auth.invalidGoogleToken',
    GOOGLE_ACCOUNT_CONFLICT: 'auth.googleConflict'
  };
  if (error.status === 0) return fallback('auth.serverUnavailable');
  if (error.status === 429) return fallback('auth.tooManyRequests');
  if (keys[error.error?.code]) return fallback(keys[error.error.code]);
  if (error.status === 403) return fallback('auth.accessDenied');
  return fallback('auth.requestError');
}
