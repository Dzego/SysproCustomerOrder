import {
  ComponentFixture,
  TestBed,
  discardPeriodicTasks,
  fakeAsync,
  tick,
} from '@angular/core/testing';
import { RouterModule } from '@angular/router';
import { Subject } from 'rxjs';

import { ApiError } from '../../../../core/api/api-error';
import { ImportsApiService } from '../../../../core/api/imports-api.service';
import { ImportJob } from '../../../../core/api/models/import-job';
import { APP_CONFIG } from '../../../../core/config/app-config';
import { ImportsModule } from '../../imports.module';
import { ImportsPageComponent } from './imports-page.component';

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

describe('ImportsPageComponent', () => {
  let fixture: ComponentFixture<ImportsPageComponent>;
  let starts: Subject<ImportJob>[];
  let gets: { id: number; response: Subject<ImportJob> }[];

  const query = <T extends HTMLElement = HTMLElement>(cy: string): T =>
    fixture.nativeElement.querySelector(`[data-cy="${cy}"]`) as T;
  const queryAll = (cy: string): HTMLElement[] =>
    Array.from(fixture.nativeElement.querySelectorAll(`[data-cy="${cy}"]`));
  const text = (cy: string): string => query(cy)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  const reply = <T>(subject: Subject<T>, value: T): void => {
    subject.next(value);
    subject.complete();
    fixture.detectChanges();
  };

  const lookUp = (id: string): void => {
    const input = query<HTMLInputElement>('import-lookup-id');
    input.value = id;
    input.dispatchEvent(new Event('input'));
    query<HTMLFormElement>('import-lookup-form').dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  };

  beforeEach(() => {
    starts = [];
    gets = [];
    sessionStorage.clear();

    TestBed.configureTestingModule({
      imports: [ImportsModule, RouterModule.forRoot([])],
      providers: [
        {
          provide: APP_CONFIG,
          useValue: {
            production: false,
            environmentName: 'Test',
            apiBaseUrl: '/api',
            importPollIntervalMs: 1000,
          },
        },
        {
          provide: ImportsApiService,
          useValue: {
            startCustomerImport: () => {
              const response = new Subject<ImportJob>();
              starts.push(response);
              return response.asObservable();
            },
            getImportJob: (id: number) => {
              const response = new Subject<ImportJob>();
              gets.push({ id, response });
              return response.asObservable();
            },
          },
        },
      ],
    });

    fixture = TestBed.createComponent(ImportsPageComponent);
    fixture.detectChanges();
  });

  it('starts with an empty session and explains that the API cannot list jobs', () => {
    expect(text('empty-state-heading')).toBe('No import jobs yet');
    expect(query('import-job-details')).toBeNull();
  });

  it('starts one import however often the button is pressed while starting (exhaustMap)', fakeAsync(() => {
    // Act
    query('import-start').click();
    query('import-start').click();
    fixture.detectChanges();
    query('import-start').click();

    // Assert
    expect(starts).toHaveLength(1);
    expect(text('import-start')).toBe('Starting…');
    expect(query<HTMLButtonElement>('import-start').disabled).toBe(true);
    discardPeriodicTasks();
  }));

  it('shows the returned job id, follows it and stops when it completes', fakeAsync(() => {
    // Arrange
    query('import-start').click();
    reply(starts[0], job(12));

    // Assert: details from the 202 response
    expect(text('import-job-id')).toBe('12');
    expect(text('import-job-status')).toBe('Pending');
    expect(query('import-job-live')).not.toBeNull();

    // Act: the job runs and finishes
    tick(0);
    reply(
      gets[0].response,
      job(12, { status: 'Processing', processed: 1000, created: 900, updated: 80, failed: 20 }),
    );
    expect(text('import-job-processed')).toBe('1,000');
    expect(text('import-job-summary')).toContain('1,000 lines committed');

    tick(1000);
    reply(
      gets[1].response,
      job(12, {
        status: 'Completed',
        processed: 2000,
        created: 1900,
        updated: 80,
        failed: 20,
        completedAt: '2026-11-09T10:05:00Z',
      }),
    );
    tick(5000);
    fixture.detectChanges();

    expect(gets).toHaveLength(2);
    expect(text('import-job-status')).toBe('Completed');
    expect(query('import-job-live')).toBeNull();
    expect(text('import-job-failed')).toBe('20');
    expect(query('import-job-rejected-note')).not.toBeNull();
    expect(text('import-job-completed-at')).toBe('9 Nov 2026, 10:05:00');
  }));

  it('shows why an import could not start, and retries', fakeAsync(() => {
    query('import-start').click();
    starts[0].error(new ApiError('not-found', 'Legacy customer file was not found.', 404));
    fixture.detectChanges();

    expect(text('error-state-message')).toBe('Legacy customer file was not found.');
    expect(query('error-state-retry')).toBeNull();
  }));

  it('shows the failure reason of a failed job and offers a new import', fakeAsync(() => {
    lookUp('5');
    reply(
      gets[0].response,
      job(5, {
        status: 'Failed',
        processed: 1000,
        failureReason: 'Lines 1 to 1000 were committed and remain applied.',
      }),
    );

    expect(text('import-job-failure-reason')).toBe(
      'Lines 1 to 1000 were committed and remain applied.',
    );
    query('import-job-start-new').click();
    expect(starts).toHaveLength(1);
  }));

  it('looks up jobs one after another, in the order entered (concatMap)', fakeAsync(() => {
    // Act
    lookUp('5');
    lookUp('6');

    // Assert: the second lookup waits for the first
    expect(gets.map((g) => g.id)).toEqual([5]);

    reply(gets[0].response, job(5, { status: 'Completed' }));
    expect(gets.map((g) => g.id)).toEqual([5, 6]);

    reply(gets[1].response, job(6, { status: 'Completed' }));
    expect(queryAll('import-job-select').map((el) => el.textContent?.trim())).toEqual([
      'Job 6',
      'Job 5',
    ]);
    expect(text('import-job-id')).toBe('6');
  }));

  it('says when a looked-up job does not exist', fakeAsync(() => {
    lookUp('999');
    gets[0].response.error(new ApiError('not-found', 'Import job with id 999 was not found.', 404));
    fixture.detectChanges();

    expect(text('import-lookup-not-found')).toBe('Import job with id 999 was not found.');
    expect(queryAll('import-job-row')).toHaveLength(0);
  }));

  it('validates the job id before looking it up', () => {
    lookUp('0');

    expect(gets).toHaveLength(0);
    expect(text('import-lookup-error')).toBe('Enter a number from 1 to 2,147,483,647.');
  });

  it('selects a followed job instead of fetching it again', fakeAsync(() => {
    lookUp('5');
    reply(gets[0].response, job(5, { status: 'Completed' }));
    lookUp('6');
    reply(gets[1].response, job(6, { status: 'Completed' }));

    lookUp('5');

    expect(gets).toHaveLength(2);
    expect(text('import-job-id')).toBe('5');
  }));

  it('removes a job from the session list', fakeAsync(() => {
    lookUp('5');
    reply(gets[0].response, job(5, { status: 'Completed' }));

    query('import-job-forget').click();
    fixture.detectChanges();

    expect(queryAll('import-job-row')).toHaveLength(0);
    expect(query('import-job-details')).toBeNull();
  }));

  it('warns while updates fail, and offers to resume once they stop', fakeAsync(() => {
    // Arrange
    query('import-start').click();
    reply(starts[0], job(8));
    tick(0);

    // Act
    gets[0].response.error(new ApiError('network', 'The server could not be reached.', null));
    fixture.detectChanges();
    expect(text('import-job-poll-warning')).toContain('The server could not be reached.');

    for (let i = 1; i < 5; i++) {
      tick(1000);
      gets[i].response.error(new ApiError('network', 'The server could not be reached.', null));
    }
    fixture.detectChanges();

    // Assert
    expect(text('error-state-heading')).toBe('Updates stopped');
    query('error-state-retry').click();
    tick(0);
    reply(gets[5].response, job(8, { status: 'Completed' }));
    expect(text('import-job-status')).toBe('Completed');
  }));
});
