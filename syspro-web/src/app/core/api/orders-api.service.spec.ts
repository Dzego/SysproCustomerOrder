import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { APP_CONFIG, AppConfig } from '../config/app-config';
import { ApiError } from './api-error';
import { CreateOrderRequest, OrderResponse } from './models/order';
import { OrdersApiService } from './orders-api.service';

const testConfig: AppConfig = {
  production: false,
  environmentName: 'Test',
  apiBaseUrl: '/api',
  importPollIntervalMs: 1000,
};

const order: OrderResponse = {
  id: 7,
  customerId: 1,
  orderDate: '2026-11-09T10:15:00Z',
  currency: 'ZAR',
  status: 'Pending',
  total: 59.97,
  items: [
    {
      id: 11,
      sku: 'SKU-1',
      description: 'Widget',
      unitPrice: 19.99,
      quantity: 3,
      lineTotal: 59.97,
    },
  ],
};

describe('OrdersApiService', () => {
  let service: OrdersApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: APP_CONFIG, useValue: testConfig },
      ],
    });

    service = TestBed.inject(OrdersApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('posts a new order and returns the created order', () => {
    // Arrange
    const body: CreateOrderRequest = {
      legacyCustomerId: '0000012345',
      currency: 'ZAR',
      status: 'Pending',
      items: [{ sku: 'SKU-1', description: 'Widget', unitPrice: 19.99, quantity: 3 }],
    };
    let received: OrderResponse | undefined;

    // Act
    service.createOrder(body).subscribe((result) => (received = result));
    const request = http.expectOne('/api/orders');
    request.flush(order, { status: 201, statusText: 'Created' });

    // Assert
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(body);
    expect(received).toEqual(order);
  });

  it('reports field errors from a validation problem', () => {
    // Arrange
    let error: ApiError | undefined;

    // Act
    service
      .createOrder({ legacyCustomerId: '1', currency: 'ZA', status: 'Pending', items: [] })
      .subscribe({ error: (caught: ApiError) => (error = caught) });

    http.expectOne('/api/orders').flush(
      {
        status: 400,
        title: 'One or more validation errors occurred.',
        errors: { Currency: ['Currency must be a 3-letter ISO 4217 code.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    // Assert
    expect(error?.kind).toBe('validation');
    expect(error?.fieldErrors['Currency']).toEqual(['Currency must be a 3-letter ISO 4217 code.']);
  });

  it('gets an order by id', () => {
    // Arrange
    let received: OrderResponse | undefined;

    // Act
    service.getOrder(7).subscribe((result) => (received = result));
    const request = http.expectOne('/api/orders/7');
    request.flush(order);

    // Assert
    expect(request.request.method).toBe('GET');
    expect(received).toEqual(order);
  });

  it('reports an unknown order as not found', () => {
    // Arrange
    let error: ApiError | undefined;

    // Act
    service.getOrder(999).subscribe({ error: (caught: ApiError) => (error = caught) });
    http
      .expectOne('/api/orders/999')
      .flush(
        { status: 404, title: 'Not Found', detail: 'Order with id 999 was not found.' },
        { status: 404, statusText: 'Not Found' },
      );

    // Assert
    expect(error?.kind).toBe('not-found');
    expect(error?.message).toBe('Order with id 999 was not found.');
  });
});
