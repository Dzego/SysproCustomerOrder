import { problem } from '../../support/problem';

describe('Customer imports (stubbed API)', () => {
  beforeEach(() => {
    cy.window().then((win) => win.sessionStorage.clear());
  });

  it('starts an import, follows it while it runs and stops when it completes', () => {
    cy.fixture('import-job-pending.json').then((pending) => {
      const replies = [
        {
          ...pending,
          status: 'Processing',
          startedAt: '2026-11-09T10:00:01Z',
          processed: 1000,
          created: 990,
          failed: 10,
        },
        {
          ...pending,
          status: 'Completed',
          startedAt: '2026-11-09T10:00:01Z',
          completedAt: '2026-11-09T10:00:09Z',
          processed: 2000,
          created: 1980,
          failed: 20,
        },
      ];
      let call = 0;

      cy.intercept('POST', '/api/import/customers', { statusCode: 202, body: pending }).as('start');
      cy.intercept('GET', '/api/import/jobs/42', (req) => {
        req.reply(replies[Math.min(call++, replies.length - 1)]);
      }).as('poll');
    });

    cy.visit('/imports');
    cy.getByCy('import-start').click();

    cy.wait('@start');
    cy.getByCy('import-job-id').should('have.text', '42');
    cy.getByCy('import-job-status').should('have.text', 'Completed');
    cy.getByCy('import-job-processed').should('have.text', '2,000');
    cy.getByCy('import-job-failed').should('have.text', '20');
    cy.getByCy('import-job-live').should('not.exist');

    // Polling has stopped: no further requests arrive.
    cy.get('@poll.all').then((calls) => {
      const count = calls.length;
      cy.wait(2500);
      cy.get('@poll.all').should('have.length', count);
    });
  });

  it('shows the reason a job failed', () => {
    cy.fixture('import-job-pending.json').then((pending) => {
      cy.intercept('GET', '/api/import/jobs/7', {
        ...pending,
        id: 7,
        status: 'Failed',
        processed: 1000,
        failureReason:
          'Lines 1 to 1000 were committed and remain applied. Processing stopped at line 1001.',
      });
    });

    cy.visit('/imports');
    cy.getByCy('import-lookup-id').type('7');
    cy.getByCy('import-lookup-submit').click();

    cy.getByCy('import-job-failure-reason').should(
      'contain.text',
      'Processing stopped at line 1001.',
    );
    cy.getByCy('import-job-start-new').should('be.enabled');
  });

  it('reports an unknown job id', () => {
    cy.intercept(
      'GET',
      '/api/import/jobs/999',
      problem(404, 'Import job with id 999 was not found.'),
    );

    cy.visit('/imports');
    cy.getByCy('import-lookup-id').type('999');
    cy.getByCy('import-lookup-submit').click();

    cy.getByCy('import-lookup-not-found').should(
      'contain.text',
      'Import job with id 999 was not found.',
    );
  });

  it('explains when the legacy file is missing on the server', () => {
    cy.intercept(
      'POST',
      '/api/import/customers',
      problem(404, 'Legacy customer file was not found.'),
    );

    cy.visit('/imports');
    cy.getByCy('import-start').click();

    cy.getByCy('error-state-message').should('have.text', 'Legacy customer file was not found.');
  });

  it('starts only one job when the button is pressed repeatedly', () => {
    cy.fixture('import-job-pending.json').then((pending) => {
      cy.intercept('POST', '/api/import/customers', {
        statusCode: 202,
        body: pending,
        delay: 800,
      }).as('start');
      cy.intercept('GET', '/api/import/jobs/42', { ...pending, status: 'Completed' });
    });

    cy.visit('/imports');
    cy.getByCy('import-start').click().click({ force: true });

    cy.getByCy('import-job-status').should('have.text', 'Completed');
    cy.get('@start.all').should('have.length', 1);
  });
});
