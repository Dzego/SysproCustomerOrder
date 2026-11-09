/** Body of POST /api/orders (C# CreateOrderRequest). Provide customerId or legacyCustomerId. */
export interface CreateOrderRequest {
  readonly customerId?: number | null;
  readonly legacyCustomerId?: string | null;
  /** Three-letter ISO 4217 code. The API stores it upper-case. */
  readonly currency: string;
  /** Free text, at most 30 characters. The API defines no fixed set of statuses. */
  readonly status: string;
  readonly items: readonly CreateOrderItemRequest[];
}

export interface CreateOrderItemRequest {
  readonly sku: string;
  readonly description: string;
  readonly unitPrice: number;
  readonly quantity: number;
}

/** Response of POST /api/orders and GET /api/orders/{id} (C# OrderResponse). */
export interface OrderResponse {
  readonly id: number;
  readonly customerId: number;
  /**
   * The order's UTC timestamp as an ISO 8601 string. The value is always UTC,
   * but only the create response carries a trailing "Z": a value read back from
   * the database has no offset, so parse it as UTC rather than local time.
   */
  readonly orderDate: string;
  readonly currency: string;
  readonly status: string;
  readonly total: number;
  readonly items: readonly OrderItemResponse[];
}

export interface OrderItemResponse {
  readonly id: number;
  readonly sku: string;
  readonly description: string;
  readonly unitPrice: number;
  readonly quantity: number;
  readonly lineTotal: number;
}
