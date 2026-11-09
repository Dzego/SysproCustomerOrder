import {
  RUN_ID,
  ZERO_SPEND_CUSTOMER,
  cents,
  customerByLegacyId,
  daysAgoUtc,
  ensureFixtureCustomers,
  getTotals,
  todayUtc,
} from '../../support/fullstack/api';

describe('Customer spending and dashboard (real API and SQL Server)', () => {
  before(() => ensureFixtureCustomers());

  it('adds a new order to the customer total for today, and not for an earlier period', () => {
    const today = todayUtc();

    getTotals(today, today).then((before) => {
      const previous = customerByLegacyId(before, 'E2E0000003').total;

      // 12.34 × 2 + 0.01 × 7 = 24.75
      cy.request('POST', '/api/orders', {
        legacyCustomerId: 'E2E0000003',
        currency: 'ZAR',
        status: `E2E ${RUN_ID}`,
        items: [
          { sku: `${RUN_ID}-T1`, description: 'Totals check', unitPrice: 12.34, quantity: 2 },
          { sku: `${RUN_ID}-T2`, description: 'Totals check', unitPrice: 0.01, quantity: 7 },
        ],
      })
        .its('status')
        .should('eq', 201);

      getTotals(today, today).then((after) => {
        expect(cents(customerByLegacyId(after, 'E2E0000003').total) - cents(previous)).to.eq(2475);
      });

      // The page shows the API's figure for the same range.
      getTotals(daysAgoUtc(29), today).then((rows) => {
        const expected = customerByLegacyId(rows, 'E2E0000003').total;

        cy.visit('/customers');
        cy.getByCy('customers-search').type('E2E0000003');
        cy.getByCy('customer-row').should('have.length', 1);
        cy.getByCy('customer-total').should(
          'have.text',
          ` ${expected.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} `,
        );
      });

      // A period that ended before the order shows nothing for it.
      cy.getByCy('date-range-from').clear().type('2000-01-01');
      cy.getByCy('date-range-to').clear().type('2000-01-31');
      cy.getByCy('date-range-apply').click();
      cy.getByCy('customers-search').clear().type('E2E0000003');
      cy.getByCy('customer-total').should('contain.text', 'No orders');
    });
  });

  it('lists customers with no spending, and can hide them', () => {
    cy.visit('/customers');
    cy.getByCy('customers-search').type(ZERO_SPEND_CUSTOMER.legacyCustomerId);

    cy.getByCy('customer-name').should('have.text', ZERO_SPEND_CUSTOMER.name);
    cy.getByCy('customer-total').should('contain.text', 'No orders');

    cy.getByCy('customers-hide-zero').check();
    cy.getByCy('empty-state-heading').should('contain.text', 'No matching customers');
  });

  it('shows dashboard figures that match the API for the same period', () => {
    const today = todayUtc();
    cy.request('POST', '/api/orders', {
      legacyCustomerId: 'E2E0000005',
      currency: 'ZAR',
      status: `E2E ${RUN_ID}`,
      items: [{ sku: `${RUN_ID}-D`, description: 'Dashboard check', unitPrice: 100, quantity: 1 }],
    });

    getTotals(daysAgoUtc(29), today).then((rows) => {
      const purchasing = rows.filter((row) => row.total > 0);
      const sum = purchasing.reduce((total, row) => total + cents(row.total), 0) / 100;

      cy.visit('/dashboard');
      cy.getByCy('stat-order-value').should(
        'contain.text',
        sum.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
      );
      cy.getByCy('stat-purchasing-customers').should('contain.text', String(purchasing.length));
      cy.getByCy('top-customer-name').first().should('have.text', purchasing[0].name);
    });
  });

  it('rejects a reversed date range with a ProblemDetails body', () => {
    cy.request({
      url: '/api/customers/totals',
      qs: { fromDate: '2026-02-01T00:00:00Z', toDate: '2026-01-01T00:00:00Z' },
      failOnStatusCode: false,
    }).then(({ status, body }) => {
      expect(status).to.eq(400);
      expect(body.detail).to.eq('fromDate cannot be later than toDate.');
      expect(body.instance).to.eq('/api/customers/totals');
      expect(body.traceId).to.be.a('string');
    });
  });
});
