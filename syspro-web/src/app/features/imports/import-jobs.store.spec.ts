import { TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { Subject } from 'rxjs';

import { ApiError } from '../../core/api/api-error';
import { ImportsApiService } from '../../core/api/imports-api.service';
import { ImportJob } from '../../core/api/models/import-job';
import { APP_CONFIG, AppConfig } from '../../core/config/app-config';
import { ImportJobsStore, MAX_CONSECUTIVE_POLL_FAILURES, TrackedJob } from './import-jobs.store';

const INTERVAL = 1000;

const config: AppConfig = {
  production: false,
  environmentName: 'Test',
  apiBaseUrl: '/api',
  importPollIntervalMs: INTERVAL,
};

const job = (id: number, changes: Partial<ImportJob> = {}): ImportJob => ({
  id,
  status: 'Pending',
  createdAt: '2026-11-09T10:00:00Z',
  startedAt: null,
  completedAt: null,
  processed: 0,
  created: 0,
  updated: 0,
  failed: 0,
  failureReason: null,
  ...changes,
});

describe('ImportJobsStore', () => {
  let store: ImportJobsStore;
  let requests: { id: number; response: Subject<ImportJob> }[];
  let latest: readonly TrackedJob[];

  const answer = (value: ImportJob | ApiError): void => {
    const pending = requests.filter((r) => !r.response.closed && r.response.observed);
    const request = pending[pending.length - 1];

    if (value instanceof ApiError) {
      request.response.error(value);
    } else {
      request.response.next(value);
      request.response.complete();
    }
  };

  const tracked = (id: number): TrackedJob | undefined => latest.find((t) => t.id === id);

  function create(): void {
    store = TestBed.inject(ImportJobsStore);
    store.jobs$.subscribe((jobs) => (latest = jobs));
  }

  beforeEach(() => {
    requests = [];
    sessionStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        { provide: APP_CONFIG, useValue: config },
        {
          provide: ImportsApiService,
          useValue: {
            getImportJob: (id: number) => {
              const response = new Subject<ImportJob>();
              requests.push({ id, response });
              return response.asObservable();
            },
          },
        },
      ],
    });
  });

  it('polls a running job until it completes, then stops', fakeAsync(() => {
    // Arrange
    create();

    // Act
    store.track(job(1));
    tick(0);
    answer(job(1, { status: 'Processing', processed: 500 }));
    tick(INTERVAL);
    answer(job(1, { status: 'Completed', processed: 1000, created: 1000 }));
    tick(INTERVAL * 5);

    // Assert
    expect(requests).toHaveLength(2);
    expect(tracked(1)).toMatchObject({
      polling: false,
      error: null,
      job: { status: 'Completed', created: 1000 },
    });
  }));

  it('does not poll a job that has already finished', fakeAsync(() => {
    create();

    store.track(job(1, { status: 'Failed', failureReason: 'Bad file' }));
    tick(INTERVAL * 3);

    expect(requests).toHaveLength(0);
    expect(tracked(1)).toMatchObject({ polling: false, job: { status: 'Failed' } });
  }));

  it('never has two requests for a job in flight, however slow the API is (exhaustMap)', fakeAsync(() => {
    // Arrange
    create();
    store.track(job(1));

    // Act: the first request stays unanswered across five intervals.
    tick(INTERVAL * 5);

    // Assert
    expect(requests).toHaveLength(1);

    answer(job(1, { status: 'Completed' }));
    tick(INTERVAL * 3);
    expect(requests).toHaveLength(1);
  }));

  it('polls several jobs at once, each on its own (mergeMap)', fakeAsync(() => {
    create();

    store.track(job(1));
    store.track(job(2));
    tick(0);

    expect(requests.map((r) => r.id).sort()).toEqual([1, 2]);
    requests[0].response.next(job(requests[0].id, { status: 'Completed' }));
    requests[0].response.complete();
    requests[1].response.next(job(requests[1].id, { status: 'Processing' }));
    requests[1].response.complete();
    tick(INTERVAL);

    const still = requests[1].id;
    expect(requests.slice(2).map((r) => r.id)).toEqual([still]);
    store.forget(still);
    discardPeriodicTasks();
  }));

  it('starts one timer per job, even when tracked twice', fakeAsync(() => {
    create();

    store.track(job(1));
    store.track(job(1));
    store.resume(1);
    tick(0);

    expect(requests).toHaveLength(1);
    store.forget(1);
    discardPeriodicTasks();
  }));

  it('keeps polling through occasional failures and clears the error on success', fakeAsync(() => {
    create();
    store.track(job(1));
    tick(0);

    answer(new ApiError('unavailable', 'Try again in a moment.', 503));
    expect(tracked(1)).toMatchObject({ polling: true, error: { kind: 'unavailable' } });

    tick(INTERVAL);
    answer(job(1, { status: 'Processing' }));
    expect(tracked(1)?.error).toBeNull();

    store.forget(1);
    discardPeriodicTasks();
  }));

  it(`gives up after ${MAX_CONSECUTIVE_POLL_FAILURES} failures in a row, and can resume`, fakeAsync(() => {
    // Arrange
    create();
    store.track(job(1));
    tick(0);

    // Act
    for (let i = 0; i < MAX_CONSECUTIVE_POLL_FAILURES; i++) {
      answer(new ApiError('network', 'Offline', null));
      tick(INTERVAL);
    }
    tick(INTERVAL * 3);

    // Assert
    expect(requests).toHaveLength(MAX_CONSECUTIVE_POLL_FAILURES);
    expect(tracked(1)).toMatchObject({ polling: false, error: { kind: 'network' } });

    store.resume(1);
    tick(0);
    answer(job(1, { status: 'Completed' }));
    expect(tracked(1)).toMatchObject({ polling: false, error: null, job: { status: 'Completed' } });
  }));

  it('stops at once when the job does not exist', fakeAsync(() => {
    create();
    store.track(job(1));
    tick(0);

    answer(new ApiError('not-found', 'Import job with id 1 was not found.', 404));
    tick(INTERVAL * 3);

    expect(requests).toHaveLength(1);
    expect(tracked(1)).toMatchObject({ polling: false, error: { kind: 'not-found' } });
  }));

  it('stops polling a job that is forgotten', fakeAsync(() => {
    create();
    store.track(job(1));
    tick(0);

    store.forget(1);
    answer(job(1, { status: 'Processing' }));
    tick(INTERVAL * 3);

    expect(requests).toHaveLength(1);
    expect(latest).toEqual([]);
  }));

  it('lists the newest job first and remembers ids for this tab', fakeAsync(() => {
    create();

    store.track(job(3, { status: 'Completed' }));
    store.track(job(7, { status: 'Completed' }));

    expect(latest.map((t) => t.id)).toEqual([7, 3]);
    expect(JSON.parse(sessionStorage.getItem('syspro.importJobIds')!)).toEqual([3, 7]);
  }));

  it('restores remembered jobs after a reload and refreshes them', fakeAsync(() => {
    // Arrange
    sessionStorage.setItem('syspro.importJobIds', JSON.stringify([4, 'junk', -1]));

    // Act
    create();
    tick(0);
    answer(job(4, { status: 'Completed' }));

    // Assert
    expect(requests.map((r) => r.id)).toEqual([4]);
    expect(tracked(4)?.job?.status).toBe('Completed');
  }));

  it('works without storage when it is unavailable', fakeAsync(() => {
    jest.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    create();
    store.track(job(1, { status: 'Completed' }));

    expect(latest.map((t) => t.id)).toEqual([1]);
  }));
});
