# Full-stack Cypress suite (opt-in)

Specs in this folder call the real ASP.NET Core API. They never run with `npm run e2e`;
use `npm run e2e:fullstack` instead.

Rules:

- Run them only against a **separate development database**, never the original
  `syspro-sqlserver` container or its `sysprocustomerorder_syspro_sql_data` volume.
- Do not use `cy.intercept` stubs here. These specs check the real contract.
- Each spec creates the data it needs and must not depend on other specs or on existing rows.

The specs and the disposable database setup are added in Phase 5.
