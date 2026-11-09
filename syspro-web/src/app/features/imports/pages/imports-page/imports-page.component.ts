import { ChangeDetectionStrategy, Component, DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, Validators } from '@angular/forms';
import {
  BehaviorSubject,
  Observable,
  Subject,
  combineLatest,
  concatMap,
  exhaustMap,
  map,
} from 'rxjs';

import { ApiError, isRetryable } from '../../../../core/api/api-error';
import { ImportsApiService } from '../../../../core/api/imports-api.service';
import { LoadState, toLoadState } from '../../../../core/api/load-state';
import { ImportJob } from '../../../../core/api/models/import-job';
import { MAX_INT, commonErrorMessage, wholeNumber } from '../../../../shared/forms/form-validators';
import { ImportJobsStore, TrackedJob } from '../../import-jobs.store';

export interface ImportsViewModel {
  readonly jobs: readonly TrackedJob[];
  readonly selected: TrackedJob | null;
  readonly starting: boolean;
  readonly startError: ApiError | null;
  readonly lookupError: ApiError | null;
  readonly lookingUp: boolean;
}

type ActionState = { readonly status: 'idle' } | LoadState<ImportJob>;

const IDLE: ActionState = { status: 'idle' };

/**
 * Starts customer imports from the legacy file on the server, and follows their
 * progress. Supported by POST /api/import/customers and GET /api/import/jobs/{id}.
 */
@Component({
  selector: 'app-imports-page',
  templateUrl: './imports-page.component.html',
  styleUrl: './imports-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class ImportsPageComponent {
  readonly isRetryable = isRetryable;
  readonly maxId = MAX_INT;

  readonly lookupForm = new FormGroup({
    jobId: new FormControl<number | null>(null, {
      validators: [Validators.required, wholeNumber(1)],
    }),
  });

  lookupSubmitted = false;

  private readonly starts = new Subject<void>();
  private readonly lookups = new Subject<number>();

  private readonly startState = new BehaviorSubject<ActionState>(IDLE);
  private readonly lookupState = new BehaviorSubject<ActionState>(IDLE);
  /** The job shown in detail; null shows the newest. */
  private readonly selectedId = new BehaviorSubject<number | null>(null);

  readonly vm$: Observable<ImportsViewModel>;

  constructor(
    importsApi: ImportsApiService,
    private readonly store: ImportJobsStore,
    destroyRef: DestroyRef,
  ) {
    // exhaustMap: clicks while a start request is in flight are ignored, so one
    // click cannot become several jobs. The API itself accepts every POST as a
    // new job, so this does not stop a second import started from another tab.
    this.starts
      .pipe(
        exhaustMap(() => importsApi.startCustomerImport().pipe(toLoadState())),
        takeUntilDestroyed(destroyRef),
      )
      .subscribe((state) => {
        this.startState.next(state);

        if (state.status === 'success') {
          this.store.track(state.data);
          this.selectedId.next(state.data.id);
        }
      });

    // concatMap: each id the user looks up is fetched in turn and added in the
    // order entered. Unlike switchMap, a second lookup does not cancel the first;
    // unlike exhaustMap, it is not ignored; unlike mergeMap, results cannot
    // arrive out of order.
    this.lookups
      .pipe(
        concatMap((id) => importsApi.getImportJob(id).pipe(toLoadState())),
        takeUntilDestroyed(destroyRef),
      )
      .subscribe((state) => {
        this.lookupState.next(state);

        if (state.status === 'success') {
          this.store.track(state.data);
          this.selectedId.next(state.data.id);
        }
      });

    this.vm$ = combineLatest([
      this.store.jobs$,
      this.selectedId,
      this.startState,
      this.lookupState,
    ]).pipe(
      map(([jobs, selectedId, start, lookup]) => ({
        jobs,
        selected: jobs.find((job) => job.id === selectedId) ?? jobs[0] ?? null,
        starting: start.status === 'loading',
        startError: start.status === 'error' ? start.error : null,
        lookingUp: lookup.status === 'loading',
        lookupError: lookup.status === 'error' ? lookup.error : null,
      })),
    );
  }

  get lookupIdError(): string | null {
    return this.lookupSubmitted ? commonErrorMessage(this.lookupForm.controls.jobId.errors) : null;
  }

  startImport(): void {
    this.starts.next();
  }

  lookUp(): void {
    this.lookupSubmitted = true;
    const { jobId } = this.lookupForm.controls;

    if (jobId.invalid || jobId.value === null) {
      return;
    }

    const existing = this.store.snapshot(jobId.value);

    if (existing) {
      // Already followed: show it, and refresh it if updates had stopped.
      this.selectedId.next(existing.id);
      this.store.resume(existing.id);
      this.lookupState.next(IDLE);
    } else {
      this.lookups.next(jobId.value);
    }

    this.lookupForm.reset();
    this.lookupSubmitted = false;
  }

  select(id: number): void {
    this.selectedId.next(id);
  }

  resume(id: number): void {
    this.store.resume(id);
  }

  forget(id: number): void {
    this.store.forget(id);

    if (this.selectedId.value === id) {
      this.selectedId.next(null);
    }
  }
}
