import { CustomerOrderTotal } from '../../core/api/models/customer-order-total';

export const TOP_CUSTOMER_COUNT = 5;

export interface RankedCustomer {
  readonly rank: number;
  readonly customerId: number;
  readonly legacyCustomerId: string;
  readonly name: string;
  /** The API's figure for this customer: order lines in every currency, unconverted. */
  readonly total: number;
}

/**
 * What the dashboard shows, derived only from GET /api/customers/totals.
 *
 * The API returns one total per customer with no currency, adding order lines in
 * every currency together. Any figure built by combining those totals (a sum
 * across customers, an average, a percentage share) could mix rand, dollars and
 * euros, so none is calculated. Only counts, which are currency-free, are
 * summarised. A per-currency API contract is proposed in
 * docs/BACKEND_CHANGE_PROPOSALS.md.
 */
export interface DashboardSummary {
  /** Every customer the API knows, whether or not they ordered in the period. */
  readonly customerCount: number;
  /** Customers with at least one order line in the period. */
  readonly purchasingCustomerCount: number;
  readonly customersWithoutOrders: number;
  readonly topCustomers: readonly RankedCustomer[];
}

/**
 * Builds the summary. Rows arrive sorted by total, highest first (an ordering
 * the API's integration tests assert), so the top customers are the first rows.
 */
export function summarizeCustomerTotals(rows: readonly CustomerOrderTotal[]): DashboardSummary {
  const purchasing = rows.filter((row) => row.total > 0);

  return {
    customerCount: rows.length,
    purchasingCustomerCount: purchasing.length,
    customersWithoutOrders: rows.length - purchasing.length,
    topCustomers: purchasing.slice(0, TOP_CUSTOMER_COUNT).map((row, index) => ({
      rank: index + 1,
      customerId: row.customerId,
      legacyCustomerId: row.legacyCustomerId,
      name: row.name,
      total: row.total,
    })),
  };
}
