import { CustomerOrderTotal } from '../../core/api/models/customer-order-total';
import { TOP_CUSTOMER_COUNT, summarizeCustomerTotals } from './dashboard-summary';

const row = (customerId: number, total: number): CustomerOrderTotal => ({
  customerId,
  legacyCustomerId: String(customerId).padStart(10, '0'),
  name: `Customer ${customerId}`,
  total,
});

describe('summarizeCustomerTotals', () => {
  it('summarises an empty customer list', () => {
    expect(summarizeCustomerTotals([])).toEqual({
      customerCount: 0,
      purchasingCustomerCount: 0,
      customersWithoutOrders: 0,
      topCustomers: [],
    });
  });

  it('counts customers with and without orders', () => {
    // Arrange: the API returns customers without orders with a total of 0.
    const rows = [row(1, 250.25), row(2, 59.97), row(3, 0), row(4, 0)];

    // Act
    const summary = summarizeCustomerTotals(rows);

    // Assert
    expect(summary).toMatchObject({
      customerCount: 4,
      purchasingCustomerCount: 2,
      customersWithoutOrders: 2,
    });
  });

  it('calculates no cross-customer sum, average or share, since totals may mix currencies', () => {
    const summary = summarizeCustomerTotals([row(1, 75), row(2, 25)]);

    expect(Object.keys(summary).sort()).toEqual([
      'customerCount',
      'customersWithoutOrders',
      'purchasingCustomerCount',
      'topCustomers',
    ]);
    expect(Object.keys(summary.topCustomers[0]).sort()).toEqual([
      'customerId',
      'legacyCustomerId',
      'name',
      'rank',
      'total',
    ]);
  });

  it('ranks the top customers in the API order, keeping each API total as it is', () => {
    const { topCustomers } = summarizeCustomerTotals([row(1, 75), row(2, 25), row(3, 0)]);

    expect(topCustomers).toEqual([
      { rank: 1, customerId: 1, legacyCustomerId: '0000000001', name: 'Customer 1', total: 75 },
      { rank: 2, customerId: 2, legacyCustomerId: '0000000002', name: 'Customer 2', total: 25 },
    ]);
  });

  it(`keeps at most ${TOP_CUSTOMER_COUNT} top customers`, () => {
    const rows = Array.from({ length: 8 }, (_, index) => row(index + 1, 100 - index));

    const { topCustomers } = summarizeCustomerTotals(rows);

    expect(topCustomers.map((customer) => customer.customerId)).toEqual([1, 2, 3, 4, 5]);
  });

  it('has no top customers when nobody ordered in the period', () => {
    const summary = summarizeCustomerTotals([row(1, 0), row(2, 0)]);

    expect(summary.topCustomers).toEqual([]);
    expect(summary.customersWithoutOrders).toBe(2);
  });
});
