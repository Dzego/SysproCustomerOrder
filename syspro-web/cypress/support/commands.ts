declare global {
  namespace Cypress {
    interface Chainable {
      /** Selects elements by their data-cy attribute, the only selector tests rely on. */
      getByCy(selector: string): Chainable<JQuery<HTMLElement>>;
    }
  }
}

Cypress.Commands.add('getByCy', (selector: string) => cy.get(`[data-cy="${selector}"]`));

export {};
