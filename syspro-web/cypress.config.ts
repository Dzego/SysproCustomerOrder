import { defineConfig } from 'cypress';

/**
 * Two suites share this configuration and are selected with CYPRESS_SUITE:
 *
 * - frontend (default): deterministic tests. Every /api request is answered by
 *   cy.intercept fixtures, and any unstubbed API call fails the test.
 * - fullstack (opt-in): runs against a real backend and a separate development
 *   database. It is never run by default.
 */
const suite = process.env['CYPRESS_SUITE'] === 'fullstack' ? 'fullstack' : 'frontend';

export default defineConfig({
  e2e: {
    baseUrl: process.env['CYPRESS_BASE_URL'] ?? 'http://localhost:4200',
    specPattern: `cypress/e2e/${suite}/**/*.cy.ts`,
    supportFile: 'cypress/support/e2e.ts',
    fixturesFolder: 'cypress/fixtures',
    viewportWidth: 1280,
    viewportHeight: 800,
    video: false,
    screenshotOnRunFailure: true,
    expose: { suite },
  },
});
