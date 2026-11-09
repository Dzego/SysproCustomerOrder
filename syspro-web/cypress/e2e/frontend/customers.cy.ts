import { problem } from '../../support/problem';

describe('Customer spending (stubbed API)', () => {
  beforeEach(() => {
    cy.intercept('GET', '/api/customers/totals*', { fixture: 'customer-totals.json' }).as('totals');
  });

  it('lists every customer, including those without orders', () => {
    cy.visit('/customers');

    cy.getByCy('customer-row').should('have.length', 8);
    cy.getByCy('customers-count').should('contain.text', 'Showing 1–8 of 8');
    cy.getByCy('customer-total').last().should('contain.text', 'No orders');
  });

  it('searches, hides zero spending and sorts without new requests', () => {
    cy.visit('/customers');
    cy.wait('@totals');

    cy.getByCy('customers-search').type('golf');
    cy.getByCy('customer-name').should('have.length', 1).and('have.text', 'Test Customer Golf');

    cy.getByCy('customers-search').clear();
    cy.getByCy('customers-hide-zero').check();
    cy.getByCy('customer-row').should('have.length', 6);

    cy.getByCy('customers-sort').select('Lowest spending first');
    cy.getByCy('customer-name').first().should('have.text', 'Test Customer Foxtrot');

    cy.get('@totals.all').should('have.length', 1);
  });

  it('starts a new order for a customer from their row', () => {
    cy.visit('/customers');

    cy.getByCy('customer-new-order').first().click();

    cy.location('pathname').should('eq', '/orders/new');
    cy.getByCy('legacy-customer-id').should('have.value', 'T000000011');
    cy.getByCy('customer-match').should('have.text', 'Test Customer Alpha');
  });

  it('explains when nothing matches the search', () => {
    cy.visit('/customers');

    cy.getByCy('customers-search').type('nobody');

    cy.getByCy('empty-state-heading').should('contain.text', 'No matching customers');
    cy.getByCy('customers-clear-filters').click();
    cy.getByCy('customer-row').should('have.length', 8);
  });

  it('shows a rejected range without offering a pointless retry', () => {
    cy.intercept(
      'GET',
      '/api/customers/totals*',
      problem(400, 'fromDate cannot be later than toDate.'),
    );

    cy.visit('/customers');

    cy.getByCy('error-state-message').should('have.text', 'fromDate cannot be later than toDate.');
    cy.getByCy('error-state-retry').should('not.exist');
  });
});
