import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { ApiService, Stats } from '../../core/api.service';
import { RefreshService } from '../../core/refresh.service';
import { Dashboard } from './dashboard';

describe('Dashboard polling', () => {
  const stats: Stats = { activeSessions: 1, totalBytesToClient: 10, totalBytesFromClient: 5, totalStreams: 0 };
  let failing = true;

  beforeEach(() => {
    vi.useFakeTimers();
    failing = true;
    TestBed.configureTestingModule({
      providers: [
        { provide: RefreshService, useValue: { intervalMs: signal(1000), set: () => {} } },
        {
          provide: ApiService,
          useValue: {
            stats: () => (failing ? throwError(() => new Error('down')) : of(stats)),
            sessions: () => of([]),
            events: () => of([]),
            clients: () => of([]),
            health: () => of({ warnings: [], probesLastHour: 0, deniedLastHour: 0 }),
          },
        },
      ],
    });
  });

  afterEach(() => vi.useRealTimers());

  it('keeps polling after an error and clears the error once the server is back', () => {
    const dashboard = TestBed.runInInjectionContext(() => new Dashboard());
    dashboard.ngOnInit();

    vi.advanceTimersByTime(0);
    expect(dashboard.loadError()).toBe(true);
    expect(dashboard.stats()).toBeNull();

    failing = false;
    vi.advanceTimersByTime(1000);
    expect(dashboard.loadError()).toBe(false);
    expect(dashboard.stats()).toEqual(stats);

    dashboard.ngOnDestroy();
  });
});
