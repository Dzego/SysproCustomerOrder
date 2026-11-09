import { CustomerOrderTotal } from '../../core/api/models/customer-order-total';
import { TOP_CUSTOMER_COUNT, summarizeCustomerTotals } from './dashboard-summary';

const row = (customerId: number, total: number): CustomerOrderTotal => ({
  customerId,
  legacyCustomerId: String(customerId).padStart(10, '0'),
  name: `Customer ${customerId}`,
  total,
});

describe('summarizeCustomerTotals', () => {
  it('summarises an empty customer list without dividing by zero', () => {
    // Act
    const summary = summarizeCustomerTotals([]);

    // Assert
    expect(summary).toEqual({
      customerCount: 0,
      purchasingCustomerCount: 0,
      orderValue: 0,
      averagePerPurchasingCustomer: null,
      topCustomers: [],
    });
  });

  it('counts every customer but only those with spend as purchasing', () => {
    // Arrange: the API returns customers without orders with a total of 0.
    const rows = [row(1, 250.25), row(2, 59.97), row(3, 0), row(4, 0)];

    // Act
    const summary = summarizeCustomerTotals(rows);

    // Assert
    expect(summary.customerCount).toBe(4);
    expect(summary.purchasingCustomerCount).toBe(2);
    expect(summary.orderValue).toBe(310.22);
    expect(summary.averagePerPurchasingCustomer).toBe(155.11);
  });

  it('adds totals in whole cents, avoiding floating-point drift', () => {
    // Act
    const summary = summarizeCustomerTotals([row(1, 0.1), row(2, 0.2)]);

    // Assert: plain addition would give 0.30000000000000004.
    expect(summary.orderValue).toBe(0.3);
  });

  it('ranks the top customers in the API order and gives each its share', () => {
    // Arrange
    const rows = [row(1, 75), row(2, 25), row(3, 0)];

    // Act
    const { topCustomers } = summarizeCustomerTotals(rows);

    // Assert
    expect(topCustomers).toEqual([
      {
        rank: 1,
        customerId: 1,
        legacyCustomerId: '0000000001',
        name: 'Customer 1',
        total: 75,
        share: 0.75,
      },
      {
        rank: 2,
        customerId: 2,
        legacyCustomerId: '0000000002',
        name: 'Customer 2',
        total: 25,
        share: 0.25,
      },
    ]);
  });

  it(`keeps at most ${TOP_CUSTOMER_COUNT} top customers`, () => {
    // Arrange
    const rows = Array.from({ length: 8 }, (_, index) => row(index + 1, 100 - index));

    // Act
    const { topCustomers } = summarizeCustomerTotals(rows);

    // Assert
    expect(topCustomers).toHaveLength(TOP_CUSTOMER_COUNT);
    expect(topCustomers.map((customer) => customer.customerId)).toEqual([1, 2, 3, 4, 5]);
  });

  it('has no top customers when nobody ordered in the period', () => {
    // Act
    const summary = summarizeCustomerTotals([row(1, 0), row(2, 0)]);

    // Assert
    expect(summary.customerCount).toBe(2);
    expect(summary.topCustomers).toEqual([]);
    expect(summary.averagePerPurchasingCustomer).toBeNull();
  });
});
