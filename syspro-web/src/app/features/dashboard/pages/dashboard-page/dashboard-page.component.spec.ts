import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RouterModule } from '@angular/router';
import { Observable, Subject } from 'rxjs';

import { ApiError } from '../../../../core/api/api-error';
import { CustomersApiService } from '../../../../core/api/customers-api.service';
import { CustomerOrderTotal } from '../../../../core/api/models/customer-order-total';
import { CLOCK } from '../../../../core/time/clock';
import { DateRange } from '../../../../shared/date-range/date-range';
import { DashboardModule } from '../../dashboard.module';
import { DashboardPageComponent } from './dashboard-page.component';

interface PendingRequest {
  readonly range: DateRange;
  readonly response: Subject<readonly CustomerOrderTotal[]>;
}

const NOW = new Date('2026-11-09T12:00:00Z');
const LAST_30_DAYS: DateRange = { from: '2026-10-11', to: '2026-11-09' };

const totals: CustomerOrderTotal[] = [
  { customerId: 1, legacyCustomerId: '0000012346', name: 'Acme Trading', total: 250.25 },
  { customerId: 2, legacyCustomerId: '0000012345', name: 'Globex', total: 59.97 },
  { customerId: 3, legacyCustomerId: '0000012347', name: 'Initech', total: 0 },
];

describe('DashboardPageComponent', () => {
  let fixture: ComponentFixture<DashboardPageComponent>;
  let requests: PendingRequest[];

  const query = <T extends HTMLElement = HTMLElement>(cy: string): T | null =>
    fixture.nativeElement.querySelector(`[data-cy="${cy}"]`);

  const text = (cy: string): string => query(cy)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  /** Answers the most recent request, as the API would. */
  const respond = (rows: readonly CustomerOrderTotal[]): void => {
    const latest = requests[requests.length - 1].response;
    latest.next(rows);
    latest.complete();
    fixture.detectChanges();
  };

  const fail = (error: ApiError): void => {
    requests[requests.length - 1].response.error(error);
    fixture.detectChanges();
  };

  beforeEach(async () => {
    requests = [];

    // Each call returns an Observable the test answers later, so the loading
    // state and request cancellation can be observed.
    const customersApi: Pick<CustomersApiService, 'getCustomerTotals'> = {
      getCustomerTotals: (range: DateRange): Observable<readonly CustomerOrderTotal[]> => {
        const response = new Subject<readonly CustomerOrderTotal[]>();
        requests.push({ range, response });
        return response.asObservable();
      },
    };

    await TestBed.configureTestingModule({
      imports: [DashboardModule, RouterModule.forRoot([])],
      providers: [
        { provide: CustomersApiService, useValue: customersApi },
        { provide: CLOCK, useValue: () => NOW },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(DashboardPageComponent);
    fixture.detectChanges();
  });

  it('requests the last 30 UTC days on load and shows the loading state', () => {
    // Assert
    expect(requests.map((request) => request.range)).toEqual([LAST_30_DAYS]);
    expect(query('loading-state')).not.toBeNull();
    expect(query('dashboard-content')?.getAttribute('aria-busy')).toBe('true');
    expect(text('dashboard-period')).toContain('11 Oct 2026 to 9 Nov 2026');
  });

  it('shows the metrics and top customers derived from the API response', () => {
    // Act
    respond(totals);

    // Assert
    expect(query('loading-state')).toBeNull();
    expect(query('dashboard-content')?.getAttribute('aria-busy')).toBe('false');
    expect(text('stat-customers')).toContain('3');
    expect(text('stat-purchasing-customers')).toContain('2');
    expect(text('stat-without-orders')).toContain('1');
    expect(text('currency-notice')).toContain('not converted between currencies');
    // Totals may mix currencies, so nothing adds them up across customers.
    expect(fixture.nativeElement.textContent).not.toContain('310.22');
    expect(query('stat-order-value')).toBeNull();
    expect(query('stat-average')).toBeNull();

    const names = Array.from(
      fixture.nativeElement.querySelectorAll(
        '[data-cy="top-customer-name"]',
      ) as NodeListOf<HTMLElement>,
      (cell) => cell.textContent,
    );
    expect(names).toEqual(['Acme Trading', 'Globex']);
    expect(text('dashboard-announcement')).toBe('Dashboard updated for 11 Oct 2026 to 9 Nov 2026.');
  });

  it('points to customer imports when there are no customers at all', () => {
    // Act
    respond([]);

    // Assert
    expect(text('empty-state-heading')).toBe('No customers yet');
    expect(query('dashboard-go-imports')?.getAttribute('href')).toBe('/imports');
    expect(query('dashboard-stats')).toBeNull();
  });

  it('shows the counts and a no-orders message when nobody ordered in the period', () => {
    // Act
    respond([{ ...totals[2] }]);

    // Assert
    expect(text('stat-purchasing-customers')).toContain('0');
    expect(text('stat-without-orders')).toContain('1');
    expect(text('empty-state-heading')).toBe('No orders in this period');
    expect(query('top-customers-table')).toBeNull();
  });

  it('shows a retryable error with its trace reference, and recovers on retry', () => {
    // Arrange
    fail(new ApiError('server', 'The server could not complete the request.', 500, 'trace-42'));

    // Assert
    expect(text('error-state-heading')).toBe('The dashboard could not be loaded');
    expect(text('error-state-message')).toBe('The server could not complete the request.');
    expect(text('error-state-trace')).toContain('trace-42');

    // Act
    query<HTMLButtonElement>('error-state-retry')!.click();
    fixture.detectChanges();

    // Assert: the same range is requested again, and the page still works.
    expect(requests.map((request) => request.range)).toEqual([LAST_30_DAYS, LAST_30_DAYS]);
    expect(query('loading-state')).not.toBeNull();

    respond(totals);
    expect(query('error-state')).toBeNull();
    expect(query('dashboard-stats')).not.toBeNull();
  });

  it('offers no retry for an error that repeating the request cannot fix', () => {
    // Act
    fail(new ApiError('rejected', 'fromDate cannot be later than toDate.', 400));

    // Assert
    expect(text('error-state-message')).toBe('fromDate cannot be later than toDate.');
    expect(query('error-state-retry')).toBeNull();
  });

  it('requests a new range when a preset is chosen and ignores the abandoned request', () => {
    // Arrange
    const first = requests[0];
    const presetSelect = query<HTMLSelectElement>('date-range-preset')!;

    // Act: change the range before the first request has answered.
    presetSelect.value = 'last7';
    presetSelect.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    // Assert: switchMap unsubscribed from the first request.
    expect(requests.map((request) => request.range)).toEqual([
      LAST_30_DAYS,
      { from: '2026-11-03', to: '2026-11-09' },
    ]);
    expect(first.response.observed).toBe(false);
    expect(text('dashboard-period')).toContain('3 Nov 2026 to 9 Nov 2026');

    // Act: a late answer for the old range must not appear.
    first.response.next(totals);
    fixture.detectChanges();

    // Assert
    expect(query('loading-state')).not.toBeNull();
  });

  it('requests the same range again on refresh', () => {
    // Arrange
    respond(totals);

    // Act
    query<HTMLButtonElement>('dashboard-refresh')!.click();
    fixture.detectChanges();

    // Assert
    expect(requests.map((request) => request.range)).toEqual([LAST_30_DAYS, LAST_30_DAYS]);
    expect(query('loading-state')).not.toBeNull();
  });

  it('cancels the in-flight request when the page is destroyed', () => {
    // Act
    fixture.destroy();

    // Assert: the async pipe released its subscription.
    expect(requests[0].response.observed).toBe(false);
  });
});
