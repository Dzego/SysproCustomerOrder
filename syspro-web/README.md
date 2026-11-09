# syspro-web

Angular 21 frontend for the SYSPRO Customer & Order Management API (`../Syspro.Api`).
It uses the NgModule architecture (no standalone components): `AppModule`, `CoreModule`,
`SharedModule` and lazy-loaded feature modules.

## Requirements

| Tool | Version | Notes |
|---|---|---|
| Node.js | 22.23.4 (pinned in `.nvmrc`) | Angular 21 supports `^20.19 \|\| ^22.12 \|\| ^24`; Cypress 16 needs Node 22 or later |
| Angular | 21.2.x | LTS until 2027-06 |
| Backend | `Syspro.Api` on `http://localhost:5204` | `dotnet run --launch-profile http` |

```bash
nvm use            # reads .nvmrc
npm ci
```

## Scripts

| Command | What it does |
|---|---|
| `npm start` | Dev server on http://localhost:4200. `/api` is proxied to the backend (`proxy.conf.json`), so the API needs no CORS policy |
| `npm run build` | Production build into `dist/syspro-web` |
| `npm test` | Jest unit tests (`test:watch`, `test:coverage`) |
| `npm run typecheck` | Type-checks app, specs and Cypress. Jest transpiles without type-checking, so run this too |
| `npm run lint` | ESLint (TypeScript, templates and template accessibility rules) |
| `npm run format` / `format:check` | Prettier |
| `npm run e2e` | Cypress **frontend** suite (needs `npm start` running; no backend) |
| `npm run e2e:open` | Cypress interactive runner |
| `npm run e2e:fullstack` | Cypress **full-stack** suite against the real API and the isolated learning database. See [docs/FULLSTACK_TESTING.md](../docs/FULLSTACK_TESTING.md) |

## Pages

| Page | What it does |
|---|---|
| Dashboard | Spending summary and top customers for a date range |
| Customer spending | Every customer's total for a date range, including zero spenders; search, sort, paging; start an order for a customer |
| Orders | Look up an order by number; create an order with several lines; view an order's details |
| Customer imports | Start an import, follow its progress until it finishes, find a job by id |

How it is built, why each RxJS operator was chosen and what the API does not support:
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

To run every frontend check at once: `../scripts/learning-env/verify-frontend.sh`.

## Structure

```
src/app/
  app.module.ts, app-routing.module.ts   root module and lazy route table
  core/      CoreModule: shell layout, HTTP setup, APP_CONFIG, title strategy (imported once)
  shared/    SharedModule: stateless reusable components (no services)
  features/  dashboard, customers, orders, imports, each a lazy module with its own routing module
src/styles/  design tokens (CSS custom properties, light and dark), base styles, shared component classes
cypress/e2e/frontend   deterministic tests; every /api call must be stubbed with cy.intercept
cypress/e2e/fullstack  tests against the real backend and the learning database (cy.intercept disabled)
```

## Notes

- Running Cypress from a VS Code extension host can fail with `bad option: --smoke-test`
  if `ELECTRON_RUN_AS_NODE=1` is inherited. Run `unset ELECTRON_RUN_AS_NODE` first.
- `npm audit` reports moderate/low advisories in dev-only tooling (Jest's `glob`/`minimatch`
  chain). Runtime dependencies report 0 vulnerabilities (`npm audit --omit=dev`).
