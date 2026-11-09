import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { APP_CONFIG, AppConfig } from '../config/app-config';
import { ApiError } from './api-error';
import { CustomersApiService } from './customers-api.service';
import { CustomerOrderTotal } from './models/customer-order-total';

const testConfig: AppConfig = {
  production: false,
  environmentName: 'Test',
  apiBaseUrl: '/api',
  importPollIntervalMs: 1000,
};

describe('CustomersApiService', () => {
  let service: CustomersApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: APP_CONFIG, useValue: testConfig },
      ],
    });

    service = TestBed.inject(CustomersApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('requests totals for the first and last instant of the UTC days in the range', () => {
    // Arrange
    const rows: CustomerOrderTotal[] = [
      { customerId: 1, legacyCustomerId: '0000012345', name: 'Acme', total: 59.97 },
    ];
    let received: readonly CustomerOrderTotal[] | undefined;

    // Act
    service
      .getCustomerTotals({ from: '2026-10-11', to: '2026-11-09' })
      .subscribe((result) => (received = result));

    const request = http.expectOne((req) => req.url === '/api/customers/totals');
    request.flush(rows);

    // Assert
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('fromDate')).toBe('2026-10-11T00:00:00.0000000Z');
    expect(request.request.params.get('toDate')).toBe('2026-11-09T23:59:59.9999999Z');
    expect(received).toEqual(rows);
  });

  it('emits an ApiError carrying the API detail when the range is rejected', () => {
    // Arrange
    let error: unknown;

    // Act
    service
      .getCustomerTotals({ from: '2026-11-09', to: '2026-11-01' })
      .subscribe({ error: (caught: unknown) => (error = caught) });

    http
      .expectOne((req) => req.url === '/api/customers/totals')
      .flush(
        { status: 400, title: 'Bad Request', detail: 'fromDate cannot be later than toDate.' },
        { status: 400, statusText: 'Bad Request' },
      );

    // Assert
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).kind).toBe('rejected');
    expect((error as ApiError).message).toBe('fromDate cannot be later than toDate.');
  });

  it('cancels the HTTP request when the subscriber unsubscribes', () => {
    // Arrange
    const subscription = service
      .getCustomerTotals({ from: '2026-11-01', to: '2026-11-09' })
      .subscribe();
    const request = http.expectOne((req) => req.url === '/api/customers/totals');

    // Act
    subscription.unsubscribe();

    // Assert
    expect(request.cancelled).toBe(true);
  });
});
