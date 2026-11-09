import { problem } from '../../support/problem';

describe('Dashboard (stubbed API)', () => {
  it('summarises customer spending and lists the top customers', () => {
    cy.intercept('GET', '/api/customers/totals*', { fixture: 'customer-totals.json' }).as('totals');

    cy.visit('/dashboard');

    cy.wait('@totals').its('request.query').should('include.keys', 'fromDate', 'toDate');
    cy.getByCy('stat-order-value').should('contain.text', '3,105.72');
    cy.getByCy('stat-purchasing-customers')
      .should('contain.text', '6')
      .and('contain.text', 'of 8 customers');
    cy.getByCy('top-customer-row').should('have.length', 5);
    cy.getByCy('top-customer-name').first().should('have.text', 'Test Customer Alpha');
    cy.getByCy('stat-order-value').should('contain.text', 'without conversion');
  });

  it('sends the chosen period as whole UTC days', () => {
    cy.intercept('GET', '/api/customers/totals*', { fixture: 'customer-totals.json' }).as('totals');
    cy.visit('/dashboard');
    cy.wait('@totals');

    cy.getByCy('date-range-from').clear().type('2026-01-01');
    cy.getByCy('date-range-to').clear().type('2026-01-31');
    cy.getByCy('date-range-apply').click();

    cy.wait('@totals').its('request.query').should('deep.equal', {
      fromDate: '2026-01-01T00:00:00.0000000Z',
      toDate: '2026-01-31T23:59:59.9999999Z',
    });
  });

  it('rejects a start date after the end date without calling the API', () => {
    cy.intercept('GET', '/api/customers/totals*', { fixture: 'customer-totals.json' }).as('totals');
    cy.visit('/dashboard');
    cy.wait('@totals');

    cy.getByCy('date-range-from').clear().type('2026-02-01');
    cy.getByCy('date-range-to').clear().type('2026-01-01');
    cy.getByCy('date-range-apply').click();

    cy.getByCy('date-range-error').should('contain.text', 'on or before');
    cy.get('@totals.all').should('have.length', 1);
  });

  it('shows a loading state, then the empty state when nobody ordered', () => {
    cy.intercept('GET', '/api/customers/totals*', { body: [], delay: 400 });

    cy.visit('/dashboard');

    cy.getByCy('loading-state').should('be.visible');
    cy.getByCy('empty-state-heading').should('have.text', ' No customers yet ');
  });

  it('shows the error with its reference, and recovers on retry', () => {
    let calls = 0;
    cy.intercept('GET', '/api/customers/totals*', (req) => {
      calls++;
      req.reply(calls === 1 ? problem(500, 'boom') : { fixture: 'customer-totals.json' });
    });

    cy.visit('/dashboard');

    cy.getByCy('error-state-message').should(
      'have.text',
      'The server could not complete the request.',
    );
    cy.getByCy('error-state-trace').should('contain.text', '00-fixture-500-00');
    cy.getByCy('error-state-retry').click();
    cy.getByCy('top-customer-row').should('have.length', 5);
  });
});
