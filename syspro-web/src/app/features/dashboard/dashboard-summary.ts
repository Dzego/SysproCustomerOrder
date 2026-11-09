import { CustomerOrderTotal } from '../../core/api/models/customer-order-total';

export const TOP_CUSTOMER_COUNT = 5;

export interface RankedCustomer {
  readonly rank: number;
  readonly customerId: number;
  readonly legacyCustomerId: string;
  readonly name: string;
  readonly total: number;
  /** Fraction (0–1) of the period's order value. */
  readonly share: number;
}

/**
 * Everything the dashboard shows, derived only from GET /api/customers/totals.
 * The API offers no order counts, order lists or import history, so the
 * dashboard does not show them.
 */
export interface DashboardSummary {
  /** Every customer the API knows, whether or not they ordered in the period. */
  readonly customerCount: number;
  /** Customers whose order lines in the period add up to more than zero. */
  readonly purchasingCustomerCount: number;
  /** Sum of all order lines in the period, all currencies added as stored. */
  readonly orderValue: number;
  /** orderValue / purchasingCustomerCount, or null when nobody purchased. */
  readonly averagePerPurchasingCustomer: number | null;
  readonly topCustomers: readonly RankedCustomer[];
}

/**
 * Builds the summary. Rows arrive sorted by total, highest first (an ordering
 * the API's integration tests assert), so the top customers are the first rows.
 */
export function summarizeCustomerTotals(rows: readonly CustomerOrderTotal[]): DashboardSummary {
  const purchasing = rows.filter((row) => row.total > 0);

  // Totals are decimal(18,2) on the server. Adding whole cents avoids binary
  // floating-point drift such as 0.1 + 0.2 = 0.30000000000000004.
  const totalCents = purchasing.reduce((sum, row) => sum + Math.round(row.total * 100), 0);
  const orderValue = totalCents / 100;

  return {
    customerCount: rows.length,
    purchasingCustomerCount: purchasing.length,
    orderValue,
    averagePerPurchasingCustomer:
      purchasing.length > 0 ? Math.round(totalCents / purchasing.length) / 100 : null,
    topCustomers: purchasing.slice(0, TOP_CUSTOMER_COUNT).map((row, index) => ({
      rank: index + 1,
      customerId: row.customerId,
      legacyCustomerId: row.legacyCustomerId,
      name: row.name,
      total: row.total,
      share: row.total / orderValue,
    })),
  };
}
