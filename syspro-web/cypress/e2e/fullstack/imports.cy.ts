import {
  FIXTURE_CUSTOMERS,
  FIXTURE_LINES,
  ImportJob,
  getTotals,
  todayUtc,
} from '../../support/fullstack/api';

describe('Customer imports (real API and SQL Server)', () => {
  beforeEach(() => {
    cy.window().then((win) => win.sessionStorage.clear());
  });

  it('starts a real import, follows it to completion and stores the customers', () => {
    cy.visit('/imports');
    cy.getByCy('import-start').click();

    cy.getByCy('import-job-id')
      .invoke('text')
      .then((idText) => {
        const id = Number(idText.trim());
        expect(id).to.be.greaterThan(0);

        // The UI keeps polling until the job is terminal.
        cy.getByCy('import-job-status', { timeout: 60000 }).should('have.text', 'Completed');
        cy.getByCy('import-job-live').should('not.exist');
        cy.getByCy('import-job-processed').should('have.text', String(FIXTURE_LINES.total));
        cy.getByCy('import-job-failed').should('have.text', String(FIXTURE_LINES.rejected));

        // The same job, read straight from the API, agrees with the page.
        cy.request<ImportJob>(`/api/import/jobs/${id}`).then(({ body }) => {
          expect(body.status).to.eq('Completed');
          expect(body.processed).to.eq(FIXTURE_LINES.total);
          expect(body.created + body.updated).to.eq(FIXTURE_LINES.valid);
          expect(body.failed).to.eq(FIXTURE_LINES.rejected);
          expect(body.failureReason).to.eq(null);
          cy.getByCy('import-job-created').should('have.text', String(body.created));
          cy.getByCy('import-job-updated').should('have.text', String(body.updated));
        });

        // A new tab has no session list, but can find the job by its id.
        cy.window().then((win) => win.sessionStorage.clear());
        cy.visit('/imports');
        cy.getByCy('import-lookup-id').type(String(id));
        cy.getByCy('import-lookup-submit').click();
        cy.getByCy('import-job-status').should('have.text', 'Completed');
      });

    // The imported customers exist, with the names from the file, and rejected lines do not.
    getTotals('2000-01-01', todayUtc()).then((rows) => {
      for (const fixture of FIXTURE_CUSTOMERS) {
        const row = rows.find(
          (candidate) => candidate.legacyCustomerId === fixture.legacyCustomerId,
        );
        expect(row, fixture.legacyCustomerId).not.to.equal(undefined);
        expect(row?.name).to.eq(fixture.name);
      }
      expect(rows.map((row) => row.legacyCustomerId)).not.to.include.members([
        'E2E0000007',
        'E2E0000008',
      ]);
      // The duplicate line did not overwrite the first one.
      expect(rows.find((row) => row.legacyCustomerId === 'E2E0000001')?.name).to.eq(
        'E2E Alpha Holdings',
      );
    });
  });

  it('reports a job id that does not exist', () => {
    cy.visit('/imports');
    cy.getByCy('import-lookup-id').type('2147483647');
    cy.getByCy('import-lookup-submit').click();

    cy.getByCy('import-lookup-not-found').should(
      'contain.text',
      'Import job with id 2147483647 was not found.',
    );
  });
});
