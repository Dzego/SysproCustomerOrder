import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { ActivatedRoute, Router, RouterModule, convertToParamMap } from '@angular/router';
import { Observable, Subject, of } from 'rxjs';

import { ApiError } from '../../../../core/api/api-error';
import { CustomersApiService } from '../../../../core/api/customers-api.service';
import { CustomerOrderTotal } from '../../../../core/api/models/customer-order-total';
import { CreateOrderRequest, OrderResponse } from '../../../../core/api/models/order';
import { OrdersApiService } from '../../../../core/api/orders-api.service';
import { CLOCK } from '../../../../core/time/clock';
import { OrdersModule } from '../../orders.module';
import { CreateOrderPageComponent } from './create-order-page.component';

const customers: CustomerOrderTotal[] = [
  { customerId: 1, legacyCustomerId: '0000012345', name: 'Acme Trading', total: 10 },
  { customerId: 2, legacyCustomerId: 'ABC1', name: 'Globex', total: 0 },
];

const created: OrderResponse = {
  id: 99,
  customerId: 1,
  orderDate: '2026-11-09T10:15:00Z',
  currency: 'ZAR',
  status: 'Pending',
  total: 59.97,
  items: [
    { id: 1, sku: 'SKU-1', description: 'Widget', unitPrice: 19.99, quantity: 3, lineTotal: 59.97 },
  ],
};

describe('CreateOrderPageComponent', () => {
  let fixture: ComponentFixture<CreateOrderPageComponent>;
  let createCalls: { request: CreateOrderRequest; response: Subject<OrderResponse> }[];
  let directoryRequests: number;
  let navigate: jest.SpyInstance;

  const query = <T extends HTMLElement = HTMLElement>(cy: string): T =>
    fixture.nativeElement.querySelector(`[data-cy="${cy}"]`) as T;
  const queryAll = (cy: string): HTMLElement[] =>
    Array.from(fixture.nativeElement.querySelectorAll(`[data-cy="${cy}"]`));
  const text = (cy: string): string => query(cy)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  const type = (element: HTMLInputElement, value: string): void => {
    element.value = value;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };

  const fillValidOrder = (): void => {
    type(query('legacy-customer-id'), '0000012345');
    type(query('item-sku'), 'SKU-1');
    type(query('item-description'), 'Widget');
    type(query('item-quantity'), '3');
    type(query('item-unit-price'), '19.99');
  };

  const submit = (): void => {
    query<HTMLFormElement>('create-order-form').dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  };

  function setUp(
    queryParams: Record<string, string> = {},
    directory: Observable<readonly CustomerOrderTotal[]> = of(customers),
  ) {
    createCalls = [];
    directoryRequests = 0;

    TestBed.configureTestingModule({
      imports: [OrdersModule, RouterModule.forRoot([])],
      providers: [
        {
          provide: OrdersApiService,
          useValue: {
            createOrder: (request: CreateOrderRequest) => {
              const response = new Subject<OrderResponse>();
              createCalls.push({ request, response });
              return response.asObservable();
            },
          },
        },
        {
          provide: CustomersApiService,
          useValue: {
            getCustomerTotals: () => {
              directoryRequests++;
              return directory;
            },
          },
        },
        { provide: CLOCK, useValue: () => new Date('2026-11-09T12:00:00Z') },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } },
        },
      ],
    });

    navigate = jest.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(CreateOrderPageComponent);
    fixture.detectChanges();
  }

  it('shows no errors before the user interacts', async () => {
    setUp();

    expect(query('legacy-customer-id-error')).toBeNull();
    expect(query('create-order-invalid')).toBeNull();
  });

  it('does not send an invalid order, and shows every field error after submit', async () => {
    // Arrange
    setUp();

    // Act
    submit();

    // Assert
    expect(createCalls).toHaveLength(0);
    expect(query('create-order-invalid')).not.toBeNull();
    expect(text('legacy-customer-id-error')).toBe('This field is required.');
    expect(text('item-sku-error')).toBe('This field is required.');
    expect(text('item-unit-price-error')).toBe('This field is required.');
  });

  it('adds and removes lines with FormArray, keeping at least one', async () => {
    setUp();

    expect(query<HTMLButtonElement>('item-remove').disabled).toBe(true);

    query('item-add').click();
    query('item-add').click();
    fixture.detectChanges();
    expect(queryAll('order-item')).toHaveLength(3);
    expect(text('item-count')).toBe('3 lines');

    queryAll('item-remove')[1].click();
    fixture.detectChanges();
    expect(queryAll('order-item')).toHaveLength(2);
  });

  it('previews line totals and the order total with exact decimal arithmetic', async () => {
    // Arrange
    setUp();
    fillValidOrder();
    query('item-add').click();
    fixture.detectChanges();

    // Act: 0.1 × 3, which a plain number multiplication gets wrong.
    type(queryAll('item-sku')[1] as HTMLInputElement, 'SKU-2');
    type(queryAll('item-quantity')[1] as HTMLInputElement, '3');
    type(queryAll('item-unit-price')[1] as HTMLInputElement, '0.1');

    // Assert
    expect(queryAll('item-line-total').map((el) => el.textContent?.trim())).toEqual([
      '59.97 ZAR',
      '0.30 ZAR',
    ]);
    expect(text('order-total')).toBe('60.27 ZAR');
  });

  it('shows no total while a line is invalid', async () => {
    setUp();
    fillValidOrder();

    type(query('item-unit-price'), '1.005');

    expect(text('order-total')).toContain('Not available');
  });

  it('sends the trimmed request once and opens the saved order', async () => {
    // Arrange
    setUp();
    fillValidOrder();
    type(query('status'), '  Confirmed ');
    type(query('currency'), 'usd');

    // Act
    submit();
    createCalls[0].response.next(created);
    createCalls[0].response.complete();
    fixture.detectChanges();

    // Assert
    expect(createCalls.map((call) => call.request)).toEqual([
      {
        legacyCustomerId: '0000012345',
        currency: 'USD',
        status: 'Confirmed',
        items: [{ sku: 'SKU-1', description: 'Widget', quantity: 3, unitPrice: 19.99 }],
      },
    ]);
    expect(navigate).toHaveBeenCalledWith(['/orders', 99], { state: { created: true } });
  });

  it('ignores repeated submits while the first request is in flight (exhaustMap)', async () => {
    // Arrange
    setUp();
    fillValidOrder();

    // Act
    submit();
    submit();
    submit();

    // Assert
    expect(createCalls).toHaveLength(1);
    expect(text('create-order-submit')).toBe('Saving…');
    expect(
      query<HTMLFieldSetElement>('create-order-form').querySelector('fieldset')!.disabled,
    ).toBe(true);
  });

  it('accepts a new submit after the previous one failed', async () => {
    setUp();
    fillValidOrder();

    submit();
    createCalls[0].response.error(
      new ApiError('server', 'The server could not complete the request.', 500, 'trace-1'),
    );
    fixture.detectChanges();

    expect(text('error-state-message')).toBe('The server could not complete the request.');
    expect(text('error-state-trace')).toContain('trace-1');

    query('error-state-retry').click();
    fixture.detectChanges();
    expect(createCalls).toHaveLength(2);
  });

  it('shows API validation errors on the fields they belong to', async () => {
    // Arrange
    setUp();
    fillValidOrder();
    submit();

    // Act
    createCalls[0].response.error(
      new ApiError('validation', 'One or more validation errors occurred.', 400, 't', {
        Currency: ['Currency must be a 3-letter ISO 4217 code.'],
        'Items[0].Quantity': ["'Quantity' must be greater than '0'."],
        Other: ['Something else.'],
      }),
    );
    fixture.detectChanges();

    // Assert
    expect(text('create-order-error-message')).toBe('One or more validation errors occurred.');
    expect(text('currency-error')).toBe('Currency must be a 3-letter ISO 4217 code.');
    expect(text('item-quantity-error')).toBe("'Quantity' must be greater than '0'.");
    expect(queryAll('create-order-form-error').map((el) => el.textContent?.trim())).toEqual([
      'Something else.',
    ]);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('marks the customer field when the API cannot find the customer (404)', async () => {
    setUp();
    fillValidOrder();
    submit();

    createCalls[0].response.error(new ApiError('not-found', 'Customer was not found.', 404));
    fixture.detectChanges();

    expect(text('legacy-customer-id-error')).toBe('Customer was not found.');
  });

  it('names the customer an identifier matches, using one shared directory request', fakeAsync(() => {
    // Arrange
    setUp();

    // Act
    type(query('legacy-customer-id'), 'abc1');
    tick(200);
    fixture.detectChanges();

    // Assert
    expect(text('customer-match')).toBe('Globex');
    expect(directoryRequests).toBe(1);
  }));

  it('suggests customers by legacy id or name, and fills the field from a suggestion', fakeAsync(() => {
    setUp();

    type(query('legacy-customer-id'), 'acme');
    tick(200);
    fixture.detectChanges();
    const suggestions = queryAll('customer-suggestion');
    expect(suggestions.map((el) => el.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      '0000012345, Acme Trading',
    ]);

    suggestions[0].click();
    tick(200);
    fixture.detectChanges();
    expect(query<HTMLInputElement>('legacy-customer-id').value).toBe('0000012345');
  }));

  it('says when no customer has the identifier', fakeAsync(() => {
    setUp();

    query<HTMLInputElement>('customer-mode-id').click();
    fixture.detectChanges();
    type(query('customer-id'), '777');
    tick(200);
    fixture.detectChanges();

    expect(query('customer-unknown')).not.toBeNull();
  }));

  it('still allows manual entry when the customer list cannot be loaded', fakeAsync(() => {
    const failing = new Observable<readonly CustomerOrderTotal[]>((subscriber) =>
      subscriber.error(new ApiError('server', 'down', 500)),
    );
    setUp({}, failing);
    tick(200);
    fixture.detectChanges();

    expect(query('customer-directory-error')).not.toBeNull();
    expect(query<HTMLInputElement>('legacy-customer-id').disabled).toBe(false);
  }));

  it('pre-fills the legacy id passed from the customer spending page', async () => {
    setUp({ customer: 'ABC1' });

    expect(query<HTMLInputElement>('legacy-customer-id').value).toBe('ABC1');
  });

  it('switches between customer number and legacy id fields', async () => {
    setUp();

    query<HTMLInputElement>('customer-mode-id').click();
    fixture.detectChanges();

    expect(query('legacy-customer-id')).toBeNull();
    expect(query('customer-id')).not.toBeNull();
  });
});
