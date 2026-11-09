// Frontend suite: no backend required. The support file fails the test if any
// /api request is made without a fixture.

const pages = [
  { nav: 'dashboard', path: '/dashboard', page: 'dashboard-page', heading: 'Dashboard' },
  { nav: 'customers', path: '/customers', page: 'customers-page', heading: 'Customer spending' },
  { nav: 'orders', path: '/orders', page: 'orders-page', heading: 'Orders' },
  { nav: 'imports', path: '/imports', page: 'imports-page', heading: 'Customer imports' },
] as const;

describe('Application navigation', () => {
  // The dashboard loads customer totals when it renders. An empty list is
  // enough here; dashboard.cy.ts covers the data itself.
  beforeEach(() => {
    cy.intercept({ method: 'GET', pathname: '/api/customers/totals' }, { body: [] });
  });

  context('desktop', () => {
    it('redirects the root URL to the dashboard', () => {
      cy.visit('/');

      cy.location('pathname').should('eq', '/dashboard');
      cy.getByCy('page-heading').should('have.text', 'Dashboard');
      cy.title().should('eq', 'Dashboard · SYSPRO Orders');
    });

    it('navigates to every feature from the sidebar and marks the active link', () => {
      cy.visit('/dashboard');

      for (const { nav, path, page, heading } of pages) {
        cy.getByCy(`nav-${nav}`).click();

        cy.location('pathname').should('eq', path);
        cy.getByCy(page).should('be.visible');
        cy.getByCy('page-heading').should('have.text', heading);
        cy.getByCy(`nav-${nav}`).should('have.attr', 'aria-current', 'page');
        cy.title().should('eq', `${heading} · SYSPRO Orders`);
      }
    });

    it('loads a feature directly from its URL', () => {
      cy.visit('/imports');

      cy.getByCy('imports-page').should('be.visible');
      cy.getByCy('nav-imports').should('have.attr', 'aria-current', 'page');
    });

    it('shows the not-found page for an unknown URL and links back to the dashboard', () => {
      cy.visit('/no-such-page');

      cy.getByCy('not-found').should('be.visible');
      cy.getByCy('not-found-home').click();
      cy.location('pathname').should('eq', '/dashboard');
    });

    it('hides the mobile menu button when the sidebar is permanently visible', () => {
      cy.visit('/dashboard');

      cy.getByCy('nav-toggle').should('not.be.visible');
      cy.getByCy('nav-orders').should('be.visible');
    });
  });

  context('mobile', () => {
    beforeEach(() => {
      cy.viewport('iphone-x');
      cy.visit('/dashboard');
    });

    it('keeps the drawer closed and its links out of reach until it is opened', () => {
      cy.getByCy('nav-toggle').should('have.attr', 'aria-expanded', 'false');
      cy.getByCy('nav-orders').should('not.be.visible');
    });

    it('opens the drawer, navigates and closes it again', () => {
      cy.getByCy('nav-toggle').click();
      cy.getByCy('nav-toggle').should('have.attr', 'aria-expanded', 'true');

      cy.getByCy('nav-orders').should('be.visible').click();

      cy.location('pathname').should('eq', '/orders');
      cy.getByCy('nav-toggle').should('have.attr', 'aria-expanded', 'false');
      cy.getByCy('nav-orders').should('not.be.visible');
      cy.focused().should('have.attr', 'data-cy', 'main-content');
    });

    it('closes the drawer with the backdrop or the Escape key', () => {
      cy.getByCy('nav-toggle').click();
      cy.getByCy('nav-backdrop').click({ force: true });
      cy.getByCy('nav-toggle').should('have.attr', 'aria-expanded', 'false');

      cy.getByCy('nav-toggle').click();
      cy.get('body').type('{esc}');
      cy.getByCy('nav-toggle').should('have.attr', 'aria-expanded', 'false');
    });

    it('has no horizontal page scroll at phone width', () => {
      cy.document().then((doc) => {
        expect(doc.documentElement.scrollWidth).to.be.at.most(doc.documentElement.clientWidth);
      });
    });
  });
});
