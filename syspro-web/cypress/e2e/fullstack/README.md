# Full-stack Cypress suite

These specs call the real ASP.NET Core API through the dev-server proxy, backed by the
isolated learning database. `cy.intercept` is disabled here.

Run them with `scripts/learning-env/verify-fullstack.sh`. Setup and safety rules are in
[docs/FULLSTACK_TESTING.md](../../../../docs/FULLSTACK_TESTING.md). Never point this suite
at the original `syspro-sqlserver` container.
