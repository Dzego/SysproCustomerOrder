import { HttpClient } from '@angular/common/http';
import { Inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { APP_CONFIG, AppConfig } from '../config/app-config';
import { mapApiError } from './api-error';
import { CreateOrderRequest, OrderResponse } from './models/order';

/** Client for OrdersController. The API has no endpoint that lists orders. */
@Injectable({ providedIn: 'root' })
export class OrdersApiService {
  private readonly baseUrl: string;

  constructor(
    private readonly http: HttpClient,
    @Inject(APP_CONFIG) config: AppConfig,
  ) {
    this.baseUrl = `${config.apiBaseUrl}/orders`;
  }

  /** POST /api/orders. Field errors arrive as an ApiError of kind 'validation'. */
  createOrder(request: CreateOrderRequest): Observable<OrderResponse> {
    return this.http.post<OrderResponse>(this.baseUrl, request).pipe(mapApiError());
  }

  /** GET /api/orders/{id}. An unknown id arrives as an ApiError of kind 'not-found'. */
  getOrder(id: number): Observable<OrderResponse> {
    return this.http.get<OrderResponse>(`${this.baseUrl}/${id}`).pipe(mapApiError());
  }
}
