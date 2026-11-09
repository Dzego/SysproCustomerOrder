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
