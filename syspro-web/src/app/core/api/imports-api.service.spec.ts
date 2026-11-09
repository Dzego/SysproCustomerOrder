import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { APP_CONFIG, AppConfig } from '../config/app-config';
import { ApiError } from './api-error';
import { ImportsApiService } from './imports-api.service';
import { ImportJob } from './models/import-job';

const testConfig: AppConfig = {
  production: false,
  environmentName: 'Test',
  apiBaseUrl: '/api',
  importPollIntervalMs: 1000,
};

const pendingJob: ImportJob = {
  id: 3,
  status: 'Pending',
  createdAt: '2026-11-09T10:00:00Z',
  startedAt: null,
  completedAt: null,
  processed: 0,
  created: 0,
  updated: 0,
  failed: 0,
  failureReason: null,
};

describe('ImportsApiService', () => {
  let service: ImportsApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: APP_CONFIG, useValue: testConfig },
      ],
    });

    service = TestBed.inject(ImportsApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('schedules a customer import with an empty body and returns the pending job', () => {
    // Arrange
    let received: ImportJob | undefined;

    // Act
    service.startCustomerImport().subscribe((job) => (received = job));
    const request = http.expectOne('/api/import/customers');
    request.flush(pendingJob, { status: 202, statusText: 'Accepted' });

    // Assert
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toBeNull();
    expect(received).toEqual(pendingJob);
  });

  it('gets a job by id', () => {
    // Arrange
    const completed: ImportJob = {
      ...pendingJob,
      status: 'Completed',
      startedAt: '2026-11-09T10:00:01Z',
      completedAt: '2026-11-09T10:00:05Z',
      processed: 5,
      created: 5,
    };
    let received: ImportJob | undefined;

    // Act
    service.getImportJob(3).subscribe((job) => (received = job));
    http.expectOne('/api/import/jobs/3').flush(completed);

    // Assert
    expect(received).toEqual(completed);
  });

  it('reports an unknown job as not found', () => {
    // Arrange
    let error: ApiError | undefined;

    // Act
    service.getImportJob(99).subscribe({ error: (caught: ApiError) => (error = caught) });
    http
      .expectOne('/api/import/jobs/99')
      .flush(
        { status: 404, title: 'Not Found', detail: 'Import job with id 99 was not found.' },
        { status: 404, statusText: 'Not Found' },
      );

    // Assert
    expect(error?.kind).toBe('not-found');
    expect(error?.message).toBe('Import job with id 99 was not found.');
  });
});
