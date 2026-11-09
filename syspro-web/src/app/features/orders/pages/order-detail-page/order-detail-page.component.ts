import { ChangeDetectionStrategy, Component } from '@angular/core';
import { Location } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { BehaviorSubject, Observable, combineLatest, map, of, switchMap } from 'rxjs';

import { API_ERROR_MESSAGES, ApiError, isRetryable } from '../../../../core/api/api-error';
import { LoadState, toLoadState } from '../../../../core/api/load-state';
import { OrderResponse } from '../../../../core/api/models/order';
import { OrdersApiService } from '../../../../core/api/orders-api.service';

export interface OrderDetailViewModel {
  readonly orderId: string;
  readonly state: LoadState<OrderResponse>;
}

/** A positive C# int, which is all GET /api/orders/{id:int} accepts. */
export function parseOrderId(value: string | null): number | null {
  if (value === null || !/^\d{1,10}$/.test(value)) {
    return null;
  }

  const id = Number(value);

  return id >= 1 && id <= 2_147_483_647 ? id : null;
}

/** One order, read from GET /api/orders/{id}. */
@Component({
  selector: 'app-order-detail-page',
  templateUrl: './order-detail-page.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class OrderDetailPageComponent {
  readonly isRetryable = isRetryable;

  /** True when this page was opened straight after the order was created. */
  readonly justCreated: boolean;

  /** Re-emits to load the same order again after a failure. */
  private readonly reloads = new BehaviorSubject<void>(undefined);

  readonly vm$: Observable<OrderDetailViewModel>;

  constructor(route: ActivatedRoute, ordersApi: OrdersApiService, location: Location) {
    const state = location.getState() as { created?: unknown } | null;
    this.justCreated = state?.created === true;

    // switchMap: opening another order (or retrying) cancels a request still in
    // flight, so a slow response can never show the wrong order.
    this.vm$ = combineLatest([route.paramMap, this.reloads]).pipe(
      map(([params]) => params.get('id') ?? ''),
      switchMap((orderId) => {
        const id = parseOrderId(orderId);
        const request: Observable<LoadState<OrderResponse>> =
          id === null
            ? of({
                status: 'error',
                error: new ApiError('not-found', `"${orderId}" is not a valid order number.`),
              })
            : ordersApi.getOrder(id).pipe(toLoadState());

        return request.pipe(map((loadState) => ({ orderId, state: loadState })));
      }),
    );
  }

  reload(): void {
    this.reloads.next();
  }

  notFoundMessage(error: ApiError): string {
    return error.message || API_ERROR_MESSAGES.notFound;
  }
}
