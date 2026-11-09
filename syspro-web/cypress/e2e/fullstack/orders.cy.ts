import { RUN_ID, ensureFixtureCustomers, todayUtc } from '../../support/fullstack/api';

interface OrderResponse {
  id: number;
  customerId: number;
  orderDate: string;
  currency: string;
  status: string;
  total: number;
  items: {
    id: number;
    sku: string;
    description: string;
    unitPrice: number;
    quantity: number;
    lineTotal: number;
  }[];
}

describe('Orders (real API and SQL Server)', () => {
  before(() => ensureFixtureCustomers());

  const line = (
    index: number,
    sku: string,
    description: string,
    quantity: string,
    price: string,
  ): void => {
    cy.getByCy('order-item')
      .eq(index)
      .within(() => {
        cy.getByCy('item-sku').type(sku);
        cy.getByCy('item-description').type(description);
        cy.getByCy('item-quantity').clear().type(quantity);
        cy.getByCy('item-unit-price').type(price);
      });
  };

  it('creates an order with several lines, and it can be read back unchanged', () => {
    const status = `E2E ${RUN_ID}`;

    cy.visit('/orders/new');
    cy.getByCy('legacy-customer-id').type('E2E0000002');
    cy.getByCy('customer-match').should('have.text', 'E2E Bravo Traders');
    cy.getByCy('currency').clear().type('eur');
    cy.getByCy('status').clear().type(status);
    line(0, `${RUN_ID}-A`, 'Widget', '3', '19.99');
    cy.getByCy('item-add').click();
    line(1, `${RUN_ID}-B`, 'Washer', '3', '0.10');
    cy.getByCy('item-add').click();
    line(2, `${RUN_ID}-C`, 'Large item', '2', '1234567.89');
    cy.getByCy('order-total').should('contain.text', '2,469,196.05 EUR');
    cy.getByCy('create-order-submit').click();

    cy.location('pathname').should('match', /^\/orders\/\d+$/);
    cy.getByCy('order-created').should('be.visible');

    cy.location('pathname').then((path) => {
      const id = Number(path.split('/').pop());

      // What the page shows came from GET /api/orders/{id}: check it against the API directly.
      cy.request<OrderResponse>(`/api/orders/${id}`).then(({ body }) => {
        expect(body.id).to.eq(id);
        expect(body.currency).to.eq('EUR');
        expect(body.status).to.eq(status);
        expect(
          body.items.map(({ sku, description, quantity, unitPrice, lineTotal }) => ({
            sku,
            description,
            quantity,
            unitPrice,
            lineTotal,
          })),
        ).to.deep.eq([
          {
            sku: `${RUN_ID}-A`,
            description: 'Widget',
            quantity: 3,
            unitPrice: 19.99,
            lineTotal: 59.97,
          },
          {
            sku: `${RUN_ID}-B`,
            description: 'Washer',
            quantity: 3,
            unitPrice: 0.1,
            lineTotal: 0.3,
          },
          {
            sku: `${RUN_ID}-C`,
            description: 'Large item',
            quantity: 2,
            unitPrice: 1234567.89,
            lineTotal: 2469135.78,
          },
        ]);
        expect(body.total).to.eq(2469196.05);
        // Read back from the database, the date is UTC but has no offset.
        expect(body.orderDate.startsWith(todayUtc())).to.eq(true);
      });

      cy.getByCy('order-item-row').should('have.length', 3);
      cy.getByCy('order-item-line-total').then((cells) => {
        expect([...cells].map((cell) => cell.textContent?.trim())).to.deep.eq([
          '59.97',
          '0.30',
          '2,469,135.78',
        ]);
      });
      cy.getByCy('order-items-total').should('have.text', '2,469,196.05');

      // Persisted: a fresh visit loads the same order from SQL Server.
      cy.visit(`/orders/${id}`);
      cy.getByCy('order-created').should('not.exist');
      cy.getByCy('order-status').should('have.text', status);
      cy.getByCy('order-date').should(
        'contain.text',
        new Date().toLocaleDateString('en-GB', {
          day: 'numeric',
          month: 'short',
          year: 'numeric',
          timeZone: 'UTC',
        }),
      );

      // And it can be found through the lookup form.
      cy.visit('/orders');
      cy.getByCy('order-lookup-id').type(String(id));
      cy.getByCy('order-lookup-submit').click();
      cy.getByCy('order-id').should('have.text', String(id));
    });
  });

  it('identifies the customer by customer number as well', () => {
    cy.request<{ customerId: number; legacyCustomerId: string }[]>({
      url: '/api/customers/totals',
      qs: { fromDate: '2000-01-01T00:00:00Z', toDate: `${todayUtc()}T23:59:59Z` },
    }).then(({ body }) => {
      const customer = body.find((row) => row.legacyCustomerId === 'E2E0000004')!;

      cy.visit('/orders/new');
      cy.getByCy('customer-mode-id').check();
      cy.getByCy('customer-id').type(String(customer.customerId));
      cy.getByCy('customer-match').should('have.text', 'E2E Delta Logistics');
      line(0, `${RUN_ID}-N`, '', '1', '5');
      cy.getByCy('create-order-submit').click();

      cy.getByCy('order-customer-id').should('have.text', String(customer.customerId));
      cy.getByCy('order-item-description').should('contain.text', 'No description');
    });
  });

  it('shows the API answer when the customer does not exist', () => {
    cy.visit('/orders/new');
    cy.getByCy('legacy-customer-id').type(`NO${RUN_ID}`.slice(0, 10));
    line(0, 'X', 'Y', '1', '1');
    cy.getByCy('create-order-submit').click();

    cy.getByCy('legacy-customer-id-error').should('contain.text', 'Customer was not found.');
    cy.location('pathname').should('eq', '/orders/new');
  });

  it('returns ValidationProblemDetails with field paths the form maps to its controls', () => {
    cy.request({
      method: 'POST',
      url: '/api/orders',
      failOnStatusCode: false,
      body: {
        legacyCustomerId: 'E2E0000001',
        currency: 'EURO',
        status: '',
        items: [{ sku: '', description: 'x', unitPrice: 1.005, quantity: 0 }],
      },
    }).then(({ status, headers, body }) => {
      expect(status).to.eq(400);
      expect(headers['content-type']).to.contain('application/problem+json');
      expect(body.traceId).to.be.a('string');
      expect(body.errors).to.have.all.keys(
        'Currency',
        'Status',
        'Items[0].Sku',
        'Items[0].Quantity',
        'Items[0].UnitPrice',
      );
    });
  });

  it('rejects an order without lines', () => {
    cy.request({
      method: 'POST',
      url: '/api/orders',
      failOnStatusCode: false,
      body: { legacyCustomerId: 'E2E0000001', currency: 'ZAR', status: 'Pending', items: [] },
    }).then(({ status, body }) => {
      expect(status).to.eq(400);
      expect(body.errors.Items).to.deep.eq(['An order must contain at least one item.']);
    });
  });

  it('shows "not found" for an order number that does not exist', () => {
    cy.visit('/orders/2147483647');

    cy.getByCy('empty-state-heading').should('contain.text', 'Order not found');
    cy.getByCy('empty-state-message').should(
      'contain.text',
      'Order with id 2147483647 was not found.',
    );
  });
});
