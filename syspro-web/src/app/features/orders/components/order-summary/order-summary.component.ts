import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

import { OrderResponse } from '../../../../core/api/models/order';
import { formatAmount } from '../../../../shared/money/money';
import { parseUtc } from '../../../../shared/time/utc';

/** Presentational: an order's header details and its lines, as the API returned them. */
@Component({
  selector: 'app-order-summary',
  templateUrl: './order-summary.component.html',
  styleUrl: './order-summary.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class OrderSummaryComponent {
  @Input({ required: true }) order!: OrderResponse;

  readonly formatAmount = formatAmount;

  get orderDate(): Date {
    return parseUtc(this.order.orderDate);
  }

  get unitCount(): number {
    return this.order.items.reduce((sum, item) => sum + item.quantity, 0);
  }
}
