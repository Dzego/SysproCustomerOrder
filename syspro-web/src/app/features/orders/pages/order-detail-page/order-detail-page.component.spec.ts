import { Location } from '@angular/common';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, ParamMap, RouterModule, convertToParamMap } from '@angular/router';
import { BehaviorSubject, Subject } from 'rxjs';

import { ApiError } from '../../../../core/api/api-error';
import { OrderResponse } from '../../../../core/api/models/order';
import { OrdersApiService } from '../../../../core/api/orders-api.service';
import { OrdersModule } from '../../orders.module';
import { OrderDetailPageComponent, parseOrderId } from './order-detail-page.component';

const order = (id: number): OrderResponse => ({
  id,
  customerId: 3,
  // As read back from the database: UTC, but without an offset.
  orderDate: '2026-11-09T22:30:00.1234567',
  currency: 'USD',
  status: 'Shipped',
  total: 60.27,
  items: [
    { id: 1, sku: 'SKU-1', description: 'Widget', unitPrice: 19.99, quantity: 3, lineTotal: 59.97 },
    { id: 2, sku: 'SKU-2', description: '', unitPrice: 0.1, quantity: 3, lineTotal: 0.3 },
  ],
});

describe('parseOrderId', () => {
  it.each([
    ['7', 7],
    ['2147483647', 2147483647],
    ['0', null],
    ['2147483648', null],
    ['-1', null],
    ['1.5', null],
    ['abc', null],
    [null, null],
  ])('%p gives %p', (input, expected) => {
    expect(parseOrderId(input)).toBe(expected);
  });
});

describe('OrderDetailPageComponent', () => {
  let fixture: ComponentFixture<OrderDetailPageComponent>;
  let params: BehaviorSubject<ParamMap>;
  let requests: { id: number; response: Subject<OrderResponse> }[];

  const query = <T extends HTMLElement = HTMLElement>(cy: string): T =>
    fixture.nativeElement.querySelector(`[data-cy="${cy}"]`) as T;
  const queryAll = (cy: string): HTMLElement[] =>
    Array.from(fixture.nativeElement.querySelectorAll(`[data-cy="${cy}"]`));
  const text = (cy: string): string => query(cy)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  function setUp(id: string, state: unknown = null): void {
    params = new BehaviorSubject(convertToParamMap({ id }));
    requests = [];

    TestBed.configureTestingModule({
      imports: [OrdersModule, RouterModule.forRoot([])],
      providers: [
        { provide: ActivatedRoute, useValue: { paramMap: params } },
        {
          provide: OrdersApiService,
          useValue: {
            getOrder: (orderId: number) => {
              const response = new Subject<OrderResponse>();
              requests.push({ id: orderId, response });
              return response.asObservable();
            },
          },
        },
      ],
    });

    jest.spyOn(TestBed.inject(Location), 'getState').mockReturnValue(state);
    fixture = TestBed.createComponent(OrderDetailPageComponent);
    fixture.detectChanges();
  }

  const respond = (index: number, value: OrderResponse): void => {
    requests[index].response.next(value);
    requests[index].response.complete();
    fixture.detectChanges();
  };

  it('loads the order named in the URL and shows its details and lines', () => {
    // Arrange
    setUp('5');
    expect(query('loading-state')).not.toBeNull();

    // Act
    respond(0, order(5));

    // Assert
    expect(requests.map((r) => r.id)).toEqual([5]);
    expect(text('order-id')).toBe('5');
    expect(text('order-customer-id')).toBe('3');
    expect(text('order-status')).toBe('Shipped');
    expect(text('order-currency')).toBe('USD');
    expect(text('order-total-value')).toBe('60.27 USD');
    // Read as UTC even though the API sent no offset.
    expect(text('order-date')).toBe('9 Nov 2026, 22:30');
    expect(queryAll('order-item-row')).toHaveLength(2);
    expect(queryAll('order-item-line-total').map((el) => el.textContent?.trim())).toEqual([
      '59.97',
      '0.30',
    ]);
    expect(text('order-items-total')).toBe('60.27');
    expect(queryAll('order-item-description')[1].textContent).toContain('No description');
    expect(query('order-created')).toBeNull();
  });

  it('confirms creation when opened straight after saving', () => {
    setUp('9', { created: true });
    respond(0, order(9));

    expect(text('order-created')).toContain('Order 9 was created.');
  });

  it('shows "not found" for an unknown order', () => {
    setUp('404');

    requests[0].response.error(new ApiError('not-found', 'Order with id 404 was not found.', 404));
    fixture.detectChanges();

    expect(text('empty-state-heading')).toBe('Order not found');
    expect(text('empty-state-message')).toBe('Order with id 404 was not found.');
  });

  it('does not call the API for an id that is not an order number', () => {
    setUp('abc');

    expect(requests).toHaveLength(0);
    expect(text('empty-state-message')).toBe('"abc" is not a valid order number.');
  });

  it('retries after a server error', () => {
    setUp('5');
    requests[0].response.error(
      new ApiError('server', 'The server could not complete the request.', 500),
    );
    fixture.detectChanges();

    query('error-state-retry').click();
    fixture.detectChanges();
    respond(1, order(5));

    expect(requests).toHaveLength(2);
    expect(text('order-id')).toBe('5');
  });

  it('shows the newest order when the URL changes before the old response arrives (switchMap)', () => {
    // Arrange
    setUp('1');

    // Act
    params.next(convertToParamMap({ id: '2' }));
    fixture.detectChanges();
    requests[0].response.next(order(1));
    respond(1, order(2));

    // Assert
    expect(requests[0].response.observed).toBe(false);
    expect(text('order-id')).toBe('2');
  });
});
