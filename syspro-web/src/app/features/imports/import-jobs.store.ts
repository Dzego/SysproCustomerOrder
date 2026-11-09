import { Inject, Injectable } from '@angular/core';
import {
  BehaviorSubject,
  Observable,
  Subject,
  catchError,
  exhaustMap,
  finalize,
  map,
  mergeMap,
  of,
  scan,
  takeWhile,
  timer,
} from 'rxjs';

import { ApiError, isRetryable, toApiError } from '../../core/api/api-error';
import { ImportsApiService } from '../../core/api/imports-api.service';
import { ImportJob } from '../../core/api/models/import-job';
import { isTerminalJob } from '../../core/api/models/import-job-status';
import { APP_CONFIG, AppConfig } from '../../core/config/app-config';

/** A job this browser is following, with what it last learned about it. */
export interface TrackedJob {
  readonly id: number;
  /** The latest response, or null until the first one arrives. */
  readonly job: ImportJob | null;
  /** True while its status is being requested on a timer. */
  readonly polling: boolean;
  /** The latest failed status request, cleared by the next success. */
  readonly error: ApiError | null;
}

/** Polling stops after this many failed requests in a row; the user can resume it. */
export const MAX_CONSECUTIVE_POLL_FAILURES = 5;

const STORAGE_KEY = 'syspro.importJobIds';

type PollResult =
  | { readonly kind: 'job'; readonly job: ImportJob }
  | { readonly kind: 'error'; readonly error: ApiError };

interface PollProgress {
  readonly result: PollResult | null;
  readonly failures: number;
}

/**
 * The import jobs started or looked up in this browser tab, kept up to date
 * while they run.
 *
 * The API has no endpoint that lists jobs, so the ids are kept here and in
 * sessionStorage, which survives a page reload but not a closed tab. Provided
 * in root, so a job keeps being followed while the user is on another page.
 *
 * State lives in a private BehaviorSubject; components read the jobs$ stream
 * and change it only through the methods below.
 */
@Injectable({ providedIn: 'root' })
export class ImportJobsStore {
  private readonly state = new BehaviorSubject<ReadonlyMap<number, TrackedJob>>(new Map());

  /** Newest job first. */
  readonly jobs$: Observable<readonly TrackedJob[]> = this.state.pipe(
    map((jobs) => [...jobs.values()].sort((a, b) => b.id - a.id)),
  );

  /** Ids to start polling. */
  private readonly watchRequests = new Subject<number>();

  private readonly pollIntervalMs: number;

  constructor(
    private readonly importsApi: ImportsApiService,
    @Inject(APP_CONFIG) config: AppConfig,
  ) {
    this.pollIntervalMs = config.importPollIntervalMs;

    // mergeMap: every followed job is polled at the same time, each on its own
    // timer, and a job that finishes does not hold up the others. switchMap
    // would stop polling one job as soon as another was added.
    this.watchRequests.pipe(mergeMap((id) => this.poll(id))).subscribe();

    for (const id of readStoredIds()) {
      this.add(id, null);
      this.watch(id);
    }
  }

  /** Follows a job the API just returned, and polls it until it finishes. */
  track(job: ImportJob): void {
    this.add(job.id, job);

    if (!isTerminalJob(job)) {
      this.watch(job.id);
    }
  }

  /** Polls a job again after polling gave up. */
  resume(id: number): void {
    if (this.state.value.has(id)) {
      this.watch(id);
    }
  }

  /** Stops following a job. A request in flight for it is ignored when it returns. */
  forget(id: number): void {
    const next = new Map(this.state.value);
    next.delete(id);
    this.setState(next);
  }

  snapshot(id: number): TrackedJob | undefined {
    return this.state.value.get(id);
  }

  private watch(id: number): void {
    const tracked = this.state.value.get(id);

    // One timer per job, however many times it is requested.
    if (tracked && !tracked.polling) {
      this.update(id, { polling: true });
      this.watchRequests.next(id);
    }
  }

  /**
   * Requests the job's status straight away and then every interval, until it
   * reaches Completed or Failed, it turns out not to exist, or requests keep
   * failing.
   *
   * exhaustMap ignores a tick while the previous request is still waiting for a
   * response, so a slow API never has two requests for the same job in flight.
   * concatMap would queue the ticks instead, and the queue would grow without
   * limit while the API was slow.
   */
  private poll(id: number): Observable<unknown> {
    return timer(0, this.pollIntervalMs).pipe(
      exhaustMap(() =>
        this.importsApi.getImportJob(id).pipe(
          map((job): PollResult => ({ kind: 'job', job })),
          catchError((error: unknown) =>
            of<PollResult>({ kind: 'error', error: toApiError(error) }),
          ),
        ),
      ),
      // Counts failed requests in a row; a success resets the count.
      scan(
        (acc: PollProgress, result: PollResult): PollProgress => ({
          result,
          failures: result.kind === 'error' ? acc.failures + 1 : 0,
        }),
        { result: null, failures: 0 },
      ),
      // The value that ends polling is still let through (inclusive), so the
      // final state is recorded.
      takeWhile(
        ({ result, failures }) => result === null || this.onPollResult(id, result, failures),
        true,
      ),
      // Runs however polling ends: finished job, give-up, or forget().
      finalize(() => this.update(id, { polling: false })),
    );
  }

  /** Records a result and returns whether to keep polling. */
  private onPollResult(id: number, result: PollResult, failures: number): boolean {
    if (!this.state.value.has(id)) {
      return false;
    }

    if (result.kind === 'job') {
      this.update(id, { job: result.job, error: null });

      return !isTerminalJob(result.job);
    }

    this.update(id, { error: result.error });

    return isRetryable(result.error) && failures < MAX_CONSECUTIVE_POLL_FAILURES;
  }

  private add(id: number, job: ImportJob | null): void {
    const existing = this.state.value.get(id);
    const next = new Map(this.state.value);

    next.set(id, {
      id,
      job: job ?? existing?.job ?? null,
      polling: existing?.polling ?? false,
      error: null,
    });

    this.setState(next);
  }

  private update(id: number, changes: Partial<Omit<TrackedJob, 'id'>>): void {
    const existing = this.state.value.get(id);

    if (!existing) {
      return;
    }

    const next = new Map(this.state.value);
    next.set(id, { ...existing, ...changes });
    this.setState(next);
  }

  private setState(jobs: ReadonlyMap<number, TrackedJob>): void {
    this.state.next(jobs);
    writeStoredIds([...jobs.keys()]);
  }
}

// Storage can be unavailable (private mode, blocked site data) or hold anything,
// so both functions fail quietly: the store then works for this page only.

function readStoredIds(): number[] {
  try {
    const parsed: unknown = JSON.parse(sessionStorage.getItem(STORAGE_KEY) ?? '[]');

    return Array.isArray(parsed)
      ? parsed.filter((id): id is number => Number.isSafeInteger(id) && id > 0).slice(0, 50)
      : [];
  } catch {
    return [];
  }
}

function writeStoredIds(ids: number[]): void {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(ids));
  } catch {
    // Not stored; the jobs are still followed until the page is reloaded.
  }
}
