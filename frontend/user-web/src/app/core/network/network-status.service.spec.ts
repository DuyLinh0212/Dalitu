import { fakeAsync, flushMicrotasks, TestBed, tick } from '@angular/core/testing';
import { RuntimeConfig } from '../runtime-config';
import { NetworkStatusService } from './network-status.service';

describe('Network health probe backoff', () => {
  let service: NetworkStatusService;
  let fetchSpy: jasmine.Spy;
  beforeEach(() => {
    fetchSpy = spyOn(window, 'fetch').and.returnValue(Promise.resolve({ ok: false, status: 503 } as Response));
    TestBed.configureTestingModule({ providers: [
      { provide: RuntimeConfig, useValue: { apiBaseUrl: 'https://api.test/api/v1' } },
    ] });
  });
  afterEach(() => service.ngOnDestroy());

  it('does not amplify failed requests with immediate probes', fakeAsync(() => {
    service = TestBed.inject(NetworkStatusService);
    service.markAvailable();
    expect(fetchSpy).not.toHaveBeenCalled();
    for (let i = 0; i < 100; i++) service.markUnavailable();
    void service.checkConnection(); flushMicrotasks();
    expect(fetchSpy).not.toHaveBeenCalled();
    tick(30_000); flushMicrotasks();
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(service.unavailable()).toBeTrue();
    tick(30_000); flushMicrotasks();
    expect(fetchSpy).toHaveBeenCalledTimes(2);
    tick(59_999); flushMicrotasks();
    expect(fetchSpy).toHaveBeenCalledTimes(2);
    service.ngOnDestroy();
  }));

  it('treats HTTP 429 as a reachable API response', fakeAsync(() => {
    service = TestBed.inject(NetworkStatusService);
    service.markAvailable();
    service.markUnavailable();
    fetchSpy.and.returnValue(Promise.resolve({ ok: false, status: 429 } as Response));

    tick(30_000); flushMicrotasks();

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(service.unavailable()).toBeFalse();
    service.ngOnDestroy();
  }));

  it('recovers only after a successful probe', fakeAsync(() => {
    service = TestBed.inject(NetworkStatusService);
    service.markAvailable();
    fetchSpy.and.returnValue(Promise.resolve({ ok: true, status: 200 } as Response));
    service.markUnavailable();
    tick(30_000); flushMicrotasks();
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(service.unavailable()).toBeFalse();
    tick(120_000);
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    service.ngOnDestroy();
  }));
  it('lets an explicit retry check once during the automatic cooldown', fakeAsync(() => {
    service = TestBed.inject(NetworkStatusService);
    service.markAvailable();
    service.markUnavailable();
    void service.checkConnection();
    expect(fetchSpy).not.toHaveBeenCalled();
    void service.checkConnection(true);
    void service.checkConnection(true);
    flushMicrotasks();
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(service.unavailable()).toBeTrue();
    service.ngOnDestroy();
  }));
});
