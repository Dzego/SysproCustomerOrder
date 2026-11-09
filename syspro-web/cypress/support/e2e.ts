import './commands';

// Frontend isolation guard. Intercepts are matched newest first, so the
// fixtures a test registers take precedence over this catch-all. Any request
// that reaches it was not stubbed, and the test fails instead of silently
// calling a real backend.
if (Cypress.expose('suite') === 'frontend') {
  beforeEach(() => {
    cy.intercept({ url: '/api/**' }, (req) => {
      throw new Error(`Unstubbed API request in frontend suite: ${req.method} ${req.url}`);
    });
  });
}

// Full-stack guard: these specs must exercise the real API, so stubbing is not
// available at all, and the suite stops at once if the API is not running.
if (Cypress.expose('suite') === 'fullstack') {
  Cypress.Commands.overwrite('intercept', () => {
    throw new Error(
      'cy.intercept is not allowed in the full-stack suite: it must use the real API.',
    );
  });

  before(() => {
    cy.request({
      url: '/api/import/jobs/2147483647',
      failOnStatusCode: false,
      timeout: 10000,
    }).then((response) => {
      if (response.status !== 404) {
        throw new Error(
          `The API is not reachable through the dev-server proxy (GET /api/import/jobs/2147483647 returned ${response.status}). ` +
            'Start it with scripts/learning-env/api-start.sh.',
        );
      }
    });
  });
}
