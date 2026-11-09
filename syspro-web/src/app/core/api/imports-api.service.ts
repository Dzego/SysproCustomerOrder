import { HttpClient } from '@angular/common/http';
import { Inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { APP_CONFIG, AppConfig } from '../config/app-config';
import { mapApiError } from './api-error';
import { ImportJob } from './models/import-job';

/** Client for ImportController. The API has no endpoint that lists import jobs. */
@Injectable({ providedIn: 'root' })
export class ImportsApiService {
  private readonly baseUrl: string;

  constructor(
    private readonly http: HttpClient,
    @Inject(APP_CONFIG) config: AppConfig,
  ) {
    this.baseUrl = `${config.apiBaseUrl}/import`;
  }

  /**
   * POST /api/import/customers. The API answers 202 Accepted with a Pending
   * job; a background worker runs it. Each call schedules a new job.
   */
  startCustomerImport(): Observable<ImportJob> {
    return this.http.post<ImportJob>(`${this.baseUrl}/customers`, null).pipe(mapApiError());
  }

  /** GET /api/import/jobs/{id}. An unknown id arrives as an ApiError of kind 'not-found'. */
  getImportJob(id: number): Observable<ImportJob> {
    return this.http.get<ImportJob>(`${this.baseUrl}/jobs/${id}`).pipe(mapApiError());
  }
}
