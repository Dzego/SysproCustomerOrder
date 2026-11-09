import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { RouterModule } from '@angular/router';
import { Observable, Subject } from 'rxjs';

import { ApiError } from '../../../../core/api/api-error';
import { CustomersApiService } from '../../../../core/api/customers-api.service';
import { CustomerOrderTotal } from '../../../../core/api/models/customer-order-total';
import { CLOCK } from '../../../../core/time/clock';
import { DateRange } from '../../../../shared/date-range/date-range';
import { CustomersModule } from '../../customers.module';
import { CustomerSpendingPageComponent } from './customer-spending-page.component';

const rows: CustomerOrderTotal[] = [
  { customerId: 1, legacyCustomerId: '0000012346', name: 'Acme Trading', total: 1250.5 },
  { customerId: 2, legacyCustomerId: '0000012345', name: 'Globex', total: 59.97 },
  { customerId: 3, legacyCustomerId: '0000012347', name: 'Initech', total: 0 },
];

describe('CustomerSpendingPageComponent', () => {
  let fixture: ComponentFixture<CustomerSpendingPageComponent>;
  let requests: { range: DateRange; response: Subject<readonly CustomerOrderTotal[]> }[];

  const query = <T extends HTMLElement = HTMLElement>(cy: string): T =>
    fixture.nativeElement.querySelector(`[data-cy="${cy}"]`) as T;
  const queryAll = (cy: string): HTMLElement[] =>
    Array.from(fixture.nativeElement.querySelectorAll(`[data-cy="${cy}"]`));
  const text = (cy: string): string => query(cy)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const names = (): string[] => queryAll('customer-name').map((el) => el.textContent?.trim() ?? '');

  const respond = (data: readonly CustomerOrderTotal[], index = requests.length - 1): void => {
    requests[index].response.next(data);
    requests[index].response.complete();
    fixture.detectChanges();
  };

  const select = (cy: string, index: number): void => {
    const element = query<HTMLSelectElement>(cy);
    element.selectedIndex = index;
    element.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  };

  beforeEach(() => {
    requests = [];

    TestBed.configureTestingModule({
      imports: [CustomersModule, RouterModule.forRoot([])],
      providers: [
        {
          provide: CustomersApiService,
          useValue: {
            getCustomerTotals: (range: DateRange): Observable<readonly CustomerOrderTotal[]> => {
              const response = new Subject<readonly CustomerOrderTotal[]>();
              requests.push({ range, response });
              return response.asObservable();
            },
          },
        },
        { provide: CLOCK, useValue: () => new Date('2026-11-09T12:00:00Z') },
      ],
    });

    fixture = TestBed.createComponent(CustomerSpendingPageComponent);
    fixture.detectChanges();
  });

  it('requests the last 30 UTC days and shows a loading state', () => {
    expect(requests.map((r) => r.range)).toEqual([{ from: '2026-10-11', to: '2026-11-09' }]);
    expect(query('loading-state')).not.toBeNull();
  });

  it('lists every customer, including those without orders', () => {
    // Act
    respond(rows);

    // Assert
    expect(names()).toEqual(['Acme Trading', 'Globex', 'Initech']);
    expect(queryAll('customer-total').map((el) => el.textContent?.trim())).toEqual([
      '1,250.50',
      '59.97',
      'No orders',
    ]);
    expect(text('customers-count')).toBe('Showing 1–3 of 3');
    expect(text('customers-announcement')).toBe('3 customers shown.');
  });

  it('links each customer to a new order for them', () => {
    respond(rows);

    expect(queryAll('customer-new-order')[1].getAttribute('href')).toBe(
      '/orders/new?customer=0000012345',
    );
  });

  it('filters by search text after a pause in typing, without a new request', fakeAsync(() => {
    // Arrange
    respond(rows);
    const search = query<HTMLInputElement>('customers-search');

    // Act
    search.value = 'glob';
    search.dispatchEvent(new Event('input'));
    tick(100);
    fixture.detectChanges();
    const beforePause = names();
    tick(150);
    fixture.detectChanges();

    // Assert
    expect(beforePause).toHaveLength(3);
    expect(names()).toEqual(['Globex']);
    expect(text('customers-count')).toBe('Showing 1–1 of 1 (filtered from 3)');
    expect(requests).toHaveLength(1);
  }));

  it('hides customers without orders and can clear the filters again', fakeAsync(() => {
    respond(rows);

    query<HTMLInputElement>('customers-hide-zero').click();
    fixture.detectChanges();
    expect(names()).toEqual(['Acme Trading', 'Globex']);

    const search = query<HTMLInputElement>('customers-search');
    search.value = 'initech';
    search.dispatchEvent(new Event('input'));
    tick(250);
    fixture.detectChanges();
    expect(text('empty-state-heading')).toBe('No matching customers');

    query('customers-clear-filters').click();
    tick(250);
    fixture.detectChanges();
    expect(names()).toEqual(['Acme Trading', 'Globex', 'Initech']);
  }));

  it('sorts by name', () => {
    respond(rows);

    select('customers-sort', 3);

    expect(names()).toEqual(['Initech', 'Globex', 'Acme Trading']);
  });

  it('pages long lists and returns to the first page when a filter changes', () => {
    // Arrange
    const many = Array.from({ length: 30 }, (_, i) => ({
      customerId: i + 1,
      legacyCustomerId: String(i + 1).padStart(10, '0'),
      name: `Customer ${i + 1}`,
      total: 100 - i,
    }));
    respond(many);

    // Act
    query('customers-next').click();
    fixture.detectChanges();

    // Assert
    expect(text('customers-page-status')).toBe('Page 2 of 2');
    expect(text('customers-count')).toBe('Showing 26–30 of 30');

    select('customers-sort', 2);
    expect(text('customers-page-status')).toBe('Page 1 of 2');
  });

  it('shows an empty state when there are no customers at all', () => {
    respond([]);

    expect(text('empty-state-heading')).toBe('No customers yet');
    expect(query('customers-go-imports')).not.toBeNull();
  });

  it('shows the error with its reference and retries the same range', () => {
    requests[0].response.error(
      new ApiError('server', 'The server could not complete the request.', 500, 'abc'),
    );
    fixture.detectChanges();

    expect(text('error-state-trace')).toContain('abc');
    query('error-state-retry').click();
    fixture.detectChanges();

    expect(requests).toHaveLength(2);
    expect(requests[1].range).toEqual(requests[0].range);
  });

  it('does not offer a retry for a rejected range', () => {
    requests[0].response.error(
      new ApiError('rejected', 'fromDate cannot be later than toDate.', 400),
    );
    fixture.detectChanges();

    expect(text('error-state-message')).toBe('fromDate cannot be later than toDate.');
    expect(query('error-state-retry')).toBeNull();
  });

  it('cancels the request for an older range when a new one is applied (switchMap)', () => {
    // Act
    select('date-range-preset', 0);
    respond(rows);

    // Assert
    expect(requests).toHaveLength(2);
    expect(requests[0].response.observed).toBe(false);
    expect(requests[1].range).toEqual({ from: '2026-11-03', to: '2026-11-09' });
  });
});
