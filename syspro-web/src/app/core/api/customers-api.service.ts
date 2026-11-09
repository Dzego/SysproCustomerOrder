import { HttpClient, HttpParams } from '@angular/common/http';
import { Inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { DateRange } from '../../shared/date-range/date-range';
import { APP_CONFIG, AppConfig } from '../config/app-config';
import { mapApiError } from './api-error';
import { CustomerOrderTotal } from './models/customer-order-total';

/**
 * Client for CustomersController. It only builds requests: no caching or
 * state, so every subscription sends a new request, and unsubscribing cancels
 * it (the API stops the query through its CancellationToken).
 */
@Injectable({ providedIn: 'root' })
export class CustomersApiService {
  private readonly baseUrl: string;

  constructor(
    private readonly http: HttpClient,
    @Inject(APP_CONFIG) config: AppConfig,
  ) {
    this.baseUrl = `${config.apiBaseUrl}/customers`;
  }

  /**
   * GET /api/customers/totals. The API compares OrderDate >= fromDate and
   * OrderDate <= toDate, so the range is sent as the first and the last instant
   * (100 ns ticks, the precision of datetime2) of the UTC days it covers.
   */
  getCustomerTotals(range: DateRange): Observable<readonly CustomerOrderTotal[]> {
    const params = new HttpParams()
      .set('fromDate', `${range.from}T00:00:00.0000000Z`)
      .set('toDate', `${range.to}T23:59:59.9999999Z`);

    return this.http
      .get<readonly CustomerOrderTotal[]>(`${this.baseUrl}/totals`, { params })
      .pipe(mapApiError());
  }
}
