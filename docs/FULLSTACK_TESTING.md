# Full-stack testing

For developers who want to run the Angular app, the real ASP.NET Core API and a real
SQL Server together, and run the full-stack Cypress suite against them.

## Isolation

Everything runs against a separate SQL Server container. The scripts refuse to touch
the original resources.

| | Original (never used) | Learning environment |
|---|---|---|
| Container | `syspro-sqlserver` | `syspro-sqlserver-learning` |
| Volume | `sysprocustomerorder_syspro_sql_data` | `syspro_learning_sql_data` |
| Host port | 1433 | 1434, bound to `127.0.0.1` only |
| Database | `SysproCustomerOrderDb` | `SysproLearningDb` |
| Credentials | `.env` and user secrets | `~/.config/syspro-learning/sql.env`, mode 600, outside Git |

Safety checks in `scripts/learning-env/common.sh`:

- The scripts only ever name the learning container, volume, port and database, and
  fail if those constants are ever changed to the original ones.
- `db-start.sh` labels the container `syspro.environment=learning`. `db-reset.sh`
  refuses to run unless that label is present and the database is `SysproLearningDb`.
- The API and `dotnet ef` get the connection string as an environment variable.
  That overrides the user secret pointing at the original database, and keeps the
  password out of the process list. Passwords are never printed.
- `db-start.sh` reuses the local `mcr.microsoft.com/mssql/server:2022-latest` image
  and fails rather than pulling one. It checks Docker, free disk space (3 GB) and
  that port 1434 is free.
- No script removes containers, volumes or images.

## Prerequisites

Docker Desktop running, .NET 8 SDK, `dotnet-ef` 8.0.30, Node.js 22, `npm ci` in
`syspro-web`, about 3 GB of free disk space and 2 GB of memory for the container.

## Run it

```bash
scripts/learning-env/create-secrets.sh   # once: random SA password, outside Git
scripts/learning-env/db-start.sh         # create or start the container, wait until healthy
scripts/learning-env/db-migrate.sh       # apply the EF Core migrations to SysproLearningDb

scripts/learning-env/api-start.sh        # terminal 1: real API on http://localhost:5204
(cd syspro-web && npm start)             # terminal 2: Angular on http://localhost:4200

scripts/learning-env/smoke-test.sh       # real endpoints, status codes and ProblemDetails
scripts/learning-env/verify-fullstack.sh # full-stack Cypress suite
```

Other commands:

| Command | What it does |
|---|---|
| `scripts/learning-env/verify-frontend.sh` | Lint, TypeScript, Prettier, Jest, production build and the stubbed Cypress suite. Needs no backend |
| `scripts/learning-env/db-reset.sh` | Drops and recreates `SysproLearningDb` only, then migrates it. Asks you to type `reset`; stop the API first |
| `docker stop syspro-sqlserver-learning` | Stops the learning container. Its data stays in its volume |

Each script stops at the first problem with a message saying what is missing.

## Test data

- The API runs with a generated content root (`.learning-env/api-content`, ignored by
  Git). It holds copies of the API's settings and
  `scripts/learning-env/fixtures/customers_legacy.dat`, so the repository's own
  `Syspro.Api/LegacyData` file is never replaced.
- The fixture has 9 lines: customers `E2E0000001` to `E2E0000006`, one invalid tier,
  one invalid date, and one duplicate id. Importing it gives 9 processed, 6 created
  or updated, and 3 rejected, however many times it runs.
- Orders use a run id in their SKU and status, so one run's data can be told apart
  from another's.
- Totals are checked as differences (total after minus total before), so the suite
  passes on a database that earlier runs have used. Reset it for a clean start.

## What the full-stack suite covers

`syspro-web/cypress/e2e/fullstack`. `cy.intercept` is disabled in this suite, and it
stops at once if the API is not reachable.

| Spec | Checks |
|---|---|
| `imports.cy.ts` | Start a real import from the page, poll to `Completed`, counts match the API, customers stored with the right names, rejected lines absent, job found by id, unknown job shows not found |
| `orders.cy.ts` | Three-line order through the form, read back unchanged from `GET /api/orders/{id}`, exact line totals (including `0.10 × 3` and amounts in the millions), persisted after reload, found by lookup, customer by number, unknown customer (404) shown on the field, `ValidationProblemDetails` field paths, empty order rejected, unknown order shows not found |
| `customer-spending.cy.ts` | A new order adds exactly its value to today's total, an earlier period excludes it, zero-spend customer listed and hidden by the filter, dashboard figures equal the API's for the same period, reversed range returns ProblemDetails |

Loading states and forced server errors are covered by the stubbed suite, because a
real API cannot be made slow or fail on demand without changing it.

A job-level `Failed` import is not reproduced: it needs a broken input file or
database, which cannot be arranged safely through the API. Rejected lines (a partly
failed import) are covered.
