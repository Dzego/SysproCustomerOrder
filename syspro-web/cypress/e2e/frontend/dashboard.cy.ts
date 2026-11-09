// Frontend suite: every /api request is answered by a fixture. The clock is
// fixed (Date only, so timers still run) to make the default period stable.

const NOW = Date.UTC(2026, 10, 9, 12, 0, 0);
const totalsRoute = { method: 'GET', pathname: '/api/customers/totals' } as const;

const utcRange = (from: string, to: string) => ({
  fromDate: `${from}T00:00:00.0000000Z`,
  toDate: `${to}T23:59:59.9999999Z`,
});

describe('Dashboard', () => {
  beforeEach(() => {
    cy.clock(NOW, ['Date']);
  });

  context('with customer spending data', () => {
    beforeEach(() => {
      cy.intercept(totalsRoute, { fixture: 'customer-totals.json' }).as('totals');
      cy.visit('/dashboard');
    });

    it('loads the last 30 UTC days and shows the metrics and top customers', () => {
      cy.wait('@totals')
        .its('request.query')
        .should('deep.equal', utcRange('2026-10-11', '2026-11-09'));

      cy.getByCy('date-range-preset').should('have.value', 'last30');
      cy.getByCy('dashboard-period').should('contain.text', '11 Oct 2026 to 9 Nov 2026');

      cy.getByCy('stat-order-value').should('contain.text', '3,105.72');
      cy.getByCy('stat-purchasing-customers')
        .should('contain.text', '6')
        .and('contain.text', 'of 8 customers on file');
      cy.getByCy('stat-average').should('contain.text', '517.62');

      cy.getByCy('top-customer-row').should('have.length', 5);
      cy.getByCy('top-customer-name').first().should('have.text', 'Test Customer Alpha');
      cy.getByCy('top-customer-total').first().should('have.text', '1,520.50');
      cy.getByCy('top-customer-share').first().should('have.text', '49%');
    });

    it('applies a preset as soon as it is chosen', () => {
      cy.wait('@totals');

      cy.getByCy('date-range-preset').select('Last 7 days');

      cy.wait('@totals')
        .its('request.query')
        .should('deep.equal', utcRange('2026-11-03', '2026-11-09'));
      cy.getByCy('dashboard-period').should('contain.text', '3 Nov 2026 to 9 Nov 2026');
    });

    it('applies a custom range from the date fields', () => {
      cy.wait('@totals');

      cy.getByCy('date-range-from').clear().type('2026-09-01');
      cy.getByCy('date-range-preset').should('have.value', 'custom');
      cy.getByCy('date-range-to').clear().type('2026-09-30');
      cy.getByCy('date-range-apply').click();

      cy.wait('@totals')
        .its('request.query')
        .should('deep.equal', utcRange('2026-09-01', '2026-09-30'));
      cy.getByCy('dashboard-period').should('contain.text', '1 Sep 2026 to 30 Sep 2026');
    });

    it('rejects a start date after the end date without calling the API', () => {
      cy.wait('@totals');

      cy.getByCy('date-range-from').clear().type('2026-11-20');
      cy.getByCy('date-range-apply').click();

      cy.getByCy('date-range-error')
        .should('be.visible')
        .and('have.text', ' The start date must be on or before the end date. ');
      cy.getByCy('date-range-from').should('have.attr', 'aria-invalid', 'true');
      cy.get('@totals.all').should('have.length', 1);
    });

    it('requests the same period again on refresh', () => {
      cy.wait('@totals');

      cy.getByCy('dashboard-refresh').click();

      cy.wait('@totals')
        .its('request.query')
        .should('deep.equal', utcRange('2026-10-11', '2026-11-09'));
      cy.get('@totals.all').should('have.length', 2);
    });

    it('links to the full customer spending page', () => {
      cy.getByCy('dashboard-view-all').click();

      cy.location('pathname').should('eq', '/customers');
    });
  });

  it('shows a loading state until the API answers', () => {
    cy.intercept(totalsRoute, { fixture: 'customer-totals.json', delay: 800 }).as('totals');
    cy.visit('/dashboard');

    cy.getByCy('loading-state')
      .should('be.visible')
      .and('have.attr', 'role', 'status')
      .and('contain.text', 'Loading customer spending…');
    cy.getByCy('dashboard-content').should('have.attr', 'aria-busy', 'true');

    cy.wait('@totals');
    cy.getByCy('loading-state').should('not.exist');
    cy.getByCy('dashboard-stats').should('be.visible');
  });

  it('shows a server error with its reference, and recovers on retry', () => {
    // Intercepts match newest first: the one-off failure answers the first request.
    cy.intercept(totalsRoute, { fixture: 'customer-totals.json' }).as('totals');
    cy.intercept(
      { ...totalsRoute, times: 1 },
      {
        statusCode: 500,
        headers: { 'content-type': 'application/problem+json' },
        body: {
          type: 'about:blank',
          title: 'Internal Server Error',
          status: 500,
          detail: 'System.InvalidOperationException: stack trace that must not be shown',
          traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00',
        },
      },
    ).as('failure');
    cy.visit('/dashboard');
    cy.wait('@failure');

    cy.getByCy('error-state').should('have.attr', 'role', 'alert');
    cy.getByCy('error-state-message').should(
      'have.text',
      'The server could not complete the request.',
    );
    cy.getByCy('error-state').should('not.contain.text', 'InvalidOperationException');
    cy.getByCy('error-state-trace').should('contain.text', '00-4bf92f3577b34da6a3ce929d0e0e4736');

    cy.getByCy('error-state-retry').click();

    cy.wait('@totals');
    cy.getByCy('error-state').should('not.exist');
    cy.getByCy('dashboard-stats').should('be.visible');
  });

  it('explains when the server cannot be reached', () => {
    cy.intercept(totalsRoute, { forceNetworkError: true }).as('totals');
    cy.visit('/dashboard');

    cy.getByCy('error-state-message').should(
      'have.text',
      'The server could not be reached. Check your connection and try again.',
    );
    cy.getByCy('error-state-retry').should('be.visible');
  });

  it('points to customer imports when there are no customers yet', () => {
    cy.intercept(totalsRoute, { body: [] }).as('totals');
    cy.visit('/dashboard');
    cy.wait('@totals');

    cy.getByCy('empty-state-heading').should('contain.text', 'No customers yet');
    cy.getByCy('dashboard-stats').should('not.exist');

    cy.getByCy('dashboard-go-imports').click();
    cy.location('pathname').should('eq', '/imports');
  });

  it('says so when no customer ordered in the period', () => {
    cy.intercept(totalsRoute, {
      body: [
        { customerId: 17, legacyCustomerId: 'T000000017', name: 'Test Customer Golf', total: 0 },
      ],
    }).as('totals');
    cy.visit('/dashboard');
    cy.wait('@totals');

    cy.getByCy('stat-purchasing-customers').should('contain.text', 'of 1 customer on file');
    cy.getByCy('empty-state-heading').should('contain.text', 'No orders in this period');
    cy.getByCy('top-customers-table').should('not.exist');
  });

  context('on a phone', () => {
    beforeEach(() => {
      cy.viewport('iphone-x');
      cy.intercept(totalsRoute, { fixture: 'customer-totals.json' }).as('totals');
      cy.visit('/dashboard');
      cy.wait('@totals');
    });

    it('fits the screen without horizontal page scroll', () => {
      cy.getByCy('top-customers-table').should('be.visible');
      cy.document().then((doc) => {
        expect(doc.documentElement.scrollWidth).to.be.at.most(doc.documentElement.clientWidth);
      });
    });

    it('keeps the period filter usable', () => {
      cy.getByCy('date-range-preset').select('Year to date');

      cy.wait('@totals')
        .its('request.query')
        .should('deep.equal', utcRange('2026-01-01', '2026-11-09'));
    });
  });
});
