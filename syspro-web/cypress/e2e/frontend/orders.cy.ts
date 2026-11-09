import { problem } from '../../support/problem';

describe('Orders (stubbed API)', () => {
  beforeEach(() => {
    cy.intercept('GET', '/api/customers/totals*', { fixture: 'customer-totals.json' });
  });

  const fillLine = (
    index: number,
    sku: string,
    quantity: string,
    price: string,
    description = '',
  ): void => {
    cy.getByCy('order-item')
      .eq(index)
      .within(() => {
        cy.getByCy('item-sku').type(sku);
        if (description) {
          cy.getByCy('item-description').type(description);
        }
        cy.getByCy('item-quantity').clear().type(quantity);
        cy.getByCy('item-unit-price').type(price);
      });
  };

  const fillValidOrder = (): void => {
    cy.getByCy('legacy-customer-id').type('T000000011');
    cy.getByCy('currency').clear().type('usd');
    cy.getByCy('status').clear().type('Confirmed');
    fillLine(0, 'SKU-1', '3', '19.99', 'Widget');
    cy.getByCy('item-add').click();
    fillLine(1, 'SKU-2', '3', '0.1', 'Washer');
  };

  it('creates an order with several lines and shows it as read back from the API', () => {
    cy.intercept('POST', '/api/orders', { statusCode: 201, fixture: 'order-created.json' }).as(
      'create',
    );
    cy.intercept('GET', '/api/orders/501', { fixture: 'order-created.json' }).as('read');

    cy.visit('/orders/new');
    fillValidOrder();
    cy.getByCy('item-line-total').eq(1).should('have.text', ' 0.30 USD ');
    cy.getByCy('order-total').should('contain.text', '60.27 USD');
    cy.getByCy('create-order-submit').click();

    cy.wait('@create')
      .its('request.body')
      .should('deep.equal', {
        legacyCustomerId: 'T000000011',
        currency: 'USD',
        status: 'Confirmed',
        items: [
          { sku: 'SKU-1', description: 'Widget', quantity: 3, unitPrice: 19.99 },
          { sku: 'SKU-2', description: 'Washer', quantity: 3, unitPrice: 0.1 },
        ],
      });
    cy.wait('@read');
    cy.location('pathname').should('eq', '/orders/501');
    cy.getByCy('order-created').should('contain.text', 'Order 501 was created.');
    cy.getByCy('order-item-row').should('have.length', 2);
    cy.getByCy('order-items-total').should('have.text', '60.27');
  });

  it('sends one request when Create is clicked repeatedly', () => {
    cy.intercept('POST', '/api/orders', {
      statusCode: 201,
      fixture: 'order-created.json',
      delay: 800,
    }).as('create');
    cy.intercept('GET', '/api/orders/501', { fixture: 'order-created.json' });

    cy.visit('/orders/new');
    fillValidOrder();
    cy.getByCy('create-order-submit').click().click({ force: true }).click({ force: true });

    cy.getByCy('create-order-submit').should('contain.text', 'Saving…');
    cy.location('pathname').should('eq', '/orders/501');
    cy.get('@create.all').should('have.length', 1);
  });

  it('checks the form before sending anything', () => {
    cy.intercept('POST', '/api/orders').as('create');
    cy.visit('/orders/new');

    cy.getByCy('currency').clear().type('US');
    cy.getByCy('create-order-submit').click();

    cy.getByCy('create-order-invalid').should('be.visible');
    cy.getByCy('legacy-customer-id-error').should('contain.text', 'required');
    cy.getByCy('currency-error').should('contain.text', 'three-letter');
    cy.getByCy('legacy-customer-id').should('have.attr', 'aria-invalid', 'true');
    cy.get('@create.all').should('have.length', 0);
  });

  it('shows the API validation errors next to the fields', () => {
    cy.intercept('POST', '/api/orders', { statusCode: 400, fixture: 'validation-problem.json' });

    cy.visit('/orders/new');
    fillValidOrder();
    cy.getByCy('create-order-submit').click();

    cy.getByCy('create-order-error-summary').should(
      'contain.text',
      'Some of the values were not accepted.',
    );
    cy.getByCy('currency-error').should(
      'have.text',
      ' Currency must be a 3-letter ISO 4217 code. ',
    );
    cy.getByCy('item-quantity-error')
      .eq(0)
      .should('contain.text', "'Quantity' must be greater than '0'.");
  });

  it('marks the customer when the API cannot find them', () => {
    cy.intercept('POST', '/api/orders', problem(404, 'Customer was not found.'));

    cy.visit('/orders/new');
    fillValidOrder();
    cy.getByCy('create-order-submit').click();

    cy.getByCy('legacy-customer-id-error').should('contain.text', 'Customer was not found.');
  });

  it('keeps the form after a server error so the order can be sent again', () => {
    cy.intercept('POST', '/api/orders', problem(500, 'stack trace that must not be shown'));

    cy.visit('/orders/new');
    fillValidOrder();
    cy.getByCy('create-order-submit').click();

    cy.getByCy('error-state-message').should(
      'have.text',
      'The server could not complete the request.',
    );
    cy.contains('stack trace').should('not.exist');
    cy.getByCy('create-order-maybe-saved').should('contain.text', 'may create a duplicate order');
    cy.getByCy('legacy-customer-id').should('have.value', 'T000000011');
  });

  it('looks up an order and shows "not found" for an unknown number', () => {
    cy.intercept('GET', '/api/orders/777', problem(404, 'Order with id 777 was not found.'));

    cy.visit('/orders');
    cy.getByCy('order-lookup-id').type('777');
    cy.getByCy('order-lookup-submit').click();

    cy.location('pathname').should('eq', '/orders/777');
    cy.getByCy('empty-state-message').should('contain.text', 'Order with id 777 was not found.');
  });

  it('fits a phone screen without horizontal scrolling', () => {
    cy.viewport('iphone-x');
    cy.visit('/orders/new');
    cy.getByCy('item-add').click();

    cy.document().then((doc) => {
      expect(doc.documentElement.scrollWidth).to.be.at.most(doc.documentElement.clientWidth);
    });
  });
});
