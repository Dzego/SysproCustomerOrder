declare global {
  namespace Cypress {
    interface Chainable {
      /** Selects elements by their data-cy attribute, the only selector tests rely on. */
      getByCy(
        selector: string,
        options?: Partial<Loggable & Timeoutable & Withinable & Shadow>,
      ): Chainable<JQuery<HTMLElement>>;
    }
  }
}

Cypress.Commands.add(
  'getByCy',
  (
    selector: string,
    options?: Partial<Cypress.Loggable & Cypress.Timeoutable & Cypress.Withinable & Cypress.Shadow>,
  ) => cy.get(`[data-cy="${selector}"]`, options),
);

export {};
