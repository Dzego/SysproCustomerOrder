# Production readiness review

For the team deciding whether the SYSPRO Customer & Order system can go to production.
It covers the Angular frontend, the ASP.NET Core API and the SQL Server database, as of
this review.

Proposed backend fixes are in [BACKEND_CHANGE_PROPOSALS.md](BACKEND_CHANGE_PROPOSALS.md).

**Verdict:** not ready for production. The functionality is complete and well tested,
but the API has **no authentication or authorization**, which blocks any deployment
outside a trusted network. The other gaps below are smaller.

Status: ✅ in place · ⚠️ partly in place or a known limitation · ❌ missing

## Summary

| Area | Status | Main point |
|---|---|---|
| Authentication and authorization | ❌ | Every endpoint is anonymous |
| API compatibility | ✅ | Frontend types match the C# DTOs; checked against the real API by the full-stack suite |
| Data correctness | ⚠️ | Totals add different currencies together |
| Input validation | ✅ | FluentValidation on the server; the form mirrors it |
| Error handling | ✅ | ProblemDetails everywhere, with `traceId`; 5xx details hidden in the UI |
| Secrets management | ⚠️ | Fine for development; production needs a secret store |
| Frontend security | ⚠️ | No unsafe HTML; no Content Security Policy yet |
| Database integrity | ✅ | Constraints, unique indexes, transactional imports with checkpoints |
| Performance | ⚠️ | Customer totals are unpaged |
| Accessibility | ⚠️ | Built in, but not audited with an automated tool or a screen reader |
| Responsive design | ✅ | Checked at phone width in Cypress |
| Logging and observability | ⚠️ | API logs with trace ids; no health endpoint or metrics |
| CI/CD | ❌ | No pipeline in the repository |
| Deployment configuration | ⚠️ | Same-origin deployment assumed; not yet defined |
| Backup and recovery | ⚠️ | Runbook exists; the original database has an unresolved `msdb` problem |
| Operational reliability | ⚠️ | Durable imports; a frozen worker can hold up a takeover |

## Findings

### Authentication and authorization ❌ (blocker)

- Verified in the code: no `AddAuthentication`, `AddAuthorization`, `[Authorize]`,
  rate limiting or authentication package. Anyone who can reach the API can read all
  customers' spending, create orders and start imports.
- In Development, 500 responses include the stack trace. Production must never run
  as Development.
- **Backend change needed:** add OpenID Connect or JWT bearer authentication, and
  policies (for example, import requires an administrator role).
- **Frontend change then needed:** sign-in flow, an HTTP interceptor that adds the
  token, and handling for 401 and 403.

### Data correctness ⚠️

- `GET /api/customers/totals` sums order lines in every currency into one number per
  customer. The dashboard therefore shows only currency-free counts and a notice; it
  calculates no sums, averages or shares across customers. Per-customer figures are
  labelled "unconverted". **Backend change:** per-currency totals (proposal 2).
- `POST /api/orders` is not idempotent. The UI prevents double clicks, and after a
  network error or 5xx it warns that the order may already be saved instead of
  presenting a retry as safe. A retry can still create a duplicate. **Backend
  change:** an `Idempotency-Key` header (proposal 3).
- Order dates are always the server's current UTC time. This is safe, but orders
  cannot be back-dated.
- The frontend never adds money as floating point (it uses `BigInt` cents).

### Input validation and error handling ✅

- The server validates every request (FluentValidation plus database constraints);
  the client mirrors those rules for faster feedback.
- Every failure is a ProblemDetails body with a `traceId`, which the UI shows so
  users can quote it.
- In the Development environment, 500 responses contain the stack trace in `detail`.
  The UI never displays it, but **production must not run as Development**.

### Secrets management ⚠️

- Development: user secrets for the API, `.env` (ignored by Git) for Docker Compose,
  and `~/.config/syspro-learning/sql.env` (mode 600) for the learning environment.
- Production: use a secret store (for example Azure Key Vault or AWS Secrets Manager),
  and a SQL login with only the permissions the app needs, never `sa`.

### Frontend security ⚠️

- Angular escapes all bound values. The app uses no `innerHTML` and no
  `bypassSecurityTrust*`.
- `npm audit --omit=dev` reports 0 vulnerabilities in runtime dependencies.
- **To add at the hosting layer:** a Content Security Policy, HSTS,
  `X-Content-Type-Options` and `frame-ancestors`.

### Database integrity ✅

Check constraints, unique indexes, transactional batches with checkpoints, leases and
fencing tokens for imports. See [DEPLOYMENT_RUNBOOK.md](DEPLOYMENT_RUNBOOK.md).

### Performance ⚠️

- The customer totals endpoint returns every customer in one response, with a sum
  over all their orders. The customer page renders one page of rows at a time, and
  the new-order page only loads the list once an identifier is typed. That is
  acceptable for thousands of customers, not hundreds of thousands. **Backend
  change:** customer search and paged totals (proposal 4).
- The initial frontend bundle is 414 kB raw, 110 kB compressed, within the 500 kB
  budget. Features are lazy-loaded.
- Import polling stops when a job finishes, never overlaps requests, and stops at
  once (including an in-flight request) when the user removes the job.

### Accessibility ⚠️

In place: labelled controls, `aria-invalid` and error messages on fields, live regions
for loading and results, focus moved to new pages, a skip link, keyboard-operable
navigation, reduced-motion support, and Angular ESLint's template accessibility rules.

Not done: an automated audit (for example axe) and a manual screen-reader test.

### Logging and observability ⚠️

- The API logs failures with their `traceId`, which matches what users see.
- **Missing:** a health endpoint for load balancers (`/health`), structured logs
  shipped to a central store, metrics, and frontend error reporting (errors only reach
  the browser console).

### CI/CD ❌

There is no pipeline. A minimal one should run, on every pull request:

1. `dotnet build` and `dotnet test` (with Docker for the integration tests).
2. `scripts/learning-env/verify-frontend.sh` (lint, types, Prettier, Jest, production
   build, stubbed Cypress).
3. Optionally, the full-stack suite against a throwaway SQL Server service container.

### Deployment configuration ⚠️

- The production frontend calls `/api` on its own origin, so it needs a reverse proxy
  in front of both the static files and the API. The API has no CORS policy, and none
  is needed with that setup.
- The API should run with `ASPNETCORE_ENVIRONMENT=Production` behind TLS.

### Backup, recovery and operations ⚠️

- [DEPLOYMENT_RUNBOOK.md](DEPLOYMENT_RUNBOOK.md) covers backups, migrations, rollback
  and import troubleshooting.
- The original database's `msdb` has failed to upgrade since 16 August and must be
  fixed before any cutover; its backups also report Msg 3009.
- A frozen API worker holding the job's database row can hold up an import takeover
  until it is stopped (documented, with recovery steps).

## Recommended order

1. Authentication and authorization (blocker).
2. CI pipeline.
3. Per-currency totals and idempotency keys.
4. Health endpoint, central logging and a Content Security Policy.
5. Server-side paging for customer totals.
6. Accessibility audit.
