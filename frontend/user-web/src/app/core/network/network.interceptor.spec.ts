import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TimeoutError } from 'rxjs';
import { RuntimeConfig } from '../runtime-config';
import { NetworkStatusService } from './network-status.service';
import { networkInterceptor } from './network.interceptor';

describe('Recommendation response deadline', () => {
  it('cancels an unresponsive direct feed after eight seconds so the fallback can run', fakeAsync(() => {
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(withInterceptors([networkInterceptor])), provideHttpClientTesting(),
      { provide: NetworkStatusService, useValue: {
        markAvailable() {}, markUnavailable() {}, checkConnection: () => Promise.resolve(true),
      } },
    ] });
    const config = TestBed.inject(RuntimeConfig);
    config.apiBaseUrl = 'https://api.test/api/v1';
    config.recommendationBaseUrl = 'https://recommendation.test/api/v1';
    const http = TestBed.inject(HttpTestingController);
    let error: unknown;
    TestBed.inject(HttpClient).get(config.recommendationBaseUrl + '/feed/recommended')
      .subscribe({ error: value => { error = value; } });
    const request = http.expectOne(config.recommendationBaseUrl + '/feed/recommended');
    tick(8_000);
    expect(request.cancelled).toBeTrue();
    expect(error instanceof TimeoutError).toBeTrue();
    http.verify();
  }));

  it('treats API rate limiting as reachable instead of showing the network fallback', () => {
    const networkStatus = {
      markAvailable: jasmine.createSpy('markAvailable'),
      markUnavailable: jasmine.createSpy('markUnavailable'),
      checkConnection: () => Promise.resolve(true),
    };
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(withInterceptors([networkInterceptor])), provideHttpClientTesting(),
      { provide: NetworkStatusService, useValue: networkStatus },
    ] });
    const config = TestBed.inject(RuntimeConfig);
    config.apiBaseUrl = 'https://api.test/api/v1';
    const http = TestBed.inject(HttpTestingController);
    let error: unknown;

    TestBed.inject(HttpClient).post(`${config.apiBaseUrl}/videos/video-id/comments`, { content: 'spam' })
      .subscribe({ error: value => { error = value; } });
    http.expectOne(`${config.apiBaseUrl}/videos/video-id/comments`)
      .flush({ code: 'COMMENT_RATE_LIMIT_EXCEEDED' }, { status: 429, statusText: 'Too Many Requests' });

    expect((error as { status: number }).status).toBe(429);
    expect(networkStatus.markUnavailable).not.toHaveBeenCalled();
    expect(networkStatus.markAvailable).toHaveBeenCalled();
    http.verify();
  });
});
