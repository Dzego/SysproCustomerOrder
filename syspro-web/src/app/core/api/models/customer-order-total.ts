/**
 * One row of GET /api/customers/totals (C# CustomerOrderTotalResponse).
 *
 * The API returns every customer, including those with no orders in the range
 * (total 0), sorted by total, highest first.
 */
export interface CustomerOrderTotal {
  readonly customerId: number;
  readonly legacyCustomerId: string;
  readonly name: string;
  /**
   * Sum of UnitPrice × Quantity for the customer's order lines in the range.
   * C# decimal arrives as a JSON number. Orders in different currencies are
   * added together as stored: the API does not convert or group by currency.
   */
  readonly total: number;
}
