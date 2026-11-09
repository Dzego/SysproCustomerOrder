# Frontend architecture

For developers working on `syspro-web`. It explains how the application is put
together, why each RxJS operator was chosen, and what the backend API does not
support.

## Modules

| Module | Role |
|---|---|
| `AppModule` | Composes the app: `BrowserModule`, `CoreModule`, `AppRoutingModule` |
| `AppRoutingModule` | Lazy route table, `PreloadAllModules`, a not-found route |
| `CoreModule` | Imported once. Shell layout, `HttpClient`, `APP_CONFIG`, title strategy. Throws if imported twice |
| `SharedModule` | Stateless building blocks: page header, loading, empty and error states, date range filter. Provides no services |
| `DashboardModule`, `CustomersModule`, `OrdersModule`, `ImportsModule` | Lazy feature modules, each with its own routing module |

API clients (`core/api/*-api.service.ts`) are `providedIn: 'root'`, stateless and
typed against the C# DTOs. Every HTTP failure becomes an `ApiError`
(`core/api/api-error.ts`), so components never inspect `HttpErrorResponse`.

## Routes

| Route | Page | API |
|---|---|---|
| `/dashboard` | Summary, top customers, date range | `GET /api/customers/totals` |
| `/customers` | All customers with spending, search, sort, paging, zero-spend filter | `GET /api/customers/totals` |
| `/orders` | Look up an order by number | – |
| `/orders/new` | Create an order (Reactive Forms, `FormArray` lines) | `POST /api/orders`, `GET /api/customers/totals` for names |
| `/orders/:id` | Order details and lines | `GET /api/orders/{id}` |
| `/imports` | Start imports, follow progress, find a job by id | `POST /api/import/customers`, `GET /api/import/jobs/{id}` |

## State

There is no state library. Each page keeps its state in RxJS streams:

- **Inputs** are private `Subject`s or `BehaviorSubject`s (applied date range,
  page index, submissions, selected job). Only the component's own methods push
  to them.
- **Output** is one read-only `vm$` (view model) Observable, rendered with the
  `async` pipe, which subscribes and unsubscribes with the view.
- A request is turned into `LoadState<T>` (`loading`, `success`, `error`) by
  `toLoadState()`. The error becomes a value inside the inner stream, so the outer
  stream survives a failure and Retry works.
- `ImportJobsStore` is the one shared store. It is `providedIn: 'root'` so jobs
  keep being followed while the user is on another page. Its state is a private
  `BehaviorSubject`, exposed as `jobs$`.

Subscriptions made only for side effects (form mode switching, submissions that
navigate, resetting the page index) use `takeUntilDestroyed`.

## Why each RxJS operator is used

| Operator | Where | Why |
|---|---|---|
| `Observable` | Every API call, `vm$` streams | Lazy and cancellable: unsubscribing aborts the HTTP request, and the API stops its query through its `CancellationToken` |
| `Subject` | `CreateOrderPageComponent.submissions`, `ImportsPageComponent.starts` / `lookups`, `ImportJobsStore.watchRequests` | Events with no current value: "the user pressed submit" |
| `BehaviorSubject` | Applied date range (dashboard, customers), page index, submit state, selected job, store state | Needs a current value: the first request starts on subscribe, and Retry re-emits `.value` |
| `switchMap` | Date range to totals request; route `:id` to `GET /api/orders/{id}` | A newer request makes the older one stale, so it is cancelled. A slow response can never overwrite newer data |
| `exhaustMap` | Create order; start import; each poll tick | Ignores new triggers while one is in flight. A double click sends one order; a slow API never has two status requests for the same job |
| `concatMap` | Looking up import jobs by id | Each id entered is fetched in turn and added in order. `switchMap` would cancel the first lookup, `exhaustMap` would ignore the second, `mergeMap` could add them out of order |
| `mergeMap` | `ImportJobsStore` polls every followed job | Each job is independent and long-lived, so they run side by side. `switchMap` would stop polling a job as soon as another was added |
| `debounceTime` | Customer search; order customer identifier | Waits for a pause in typing before re-filtering thousands of rows or looking up the identifier |
| `distinctUntilChanged` | Same two places | Skips work when the value did not actually change (for example a trailing space added and removed) |
| `catchError` | `mapApiError()`, `toLoadState()`, the poll request | Turns failures into `ApiError` values, keeping the outer stream alive |
| `finalize` | `ImportJobsStore.poll` | Clears the job's "polling" flag however polling ends: finished, gave up, or forgotten |
| `takeUntilDestroyed` | Shell router events, form side effects, submit and lookup pipelines | Ends subscriptions that drive side effects when the component is destroyed |
| `shareReplay({ bufferSize: 1, refCount: true })` | Customer directory on the new-order page | Three template bindings use it; it is fetched once. `refCount` drops the cache when the page closes, so a later visit sees newly imported customers |
| `async` pipe | Every page | Subscribes and unsubscribes with the view; no manual subscriptions for rendered data |
| `takeWhile(…, true)`, `scan`, `timer` | Import polling | Poll until terminal (inclusive, so the final state is shown); count failed requests in a row; poll immediately then on an interval |

### Import polling

`timer(0, interval)` → `exhaustMap(GET job)` → `scan(failure count)` →
`takeWhile(keep going, inclusive)` → `finalize(clear flag)`.

- No overlapping requests: `exhaustMap` drops ticks while a request is pending.
- No unbounded queue: `concatMap` would queue ticks during a slow response; this
  was rejected for that reason.
- Polling stops on `Completed` or `Failed`, on a 404, on a non-retryable error,
  or after 5 failed requests in a row. The user can resume it.

### Duplicate submissions

`exhaustMap` and the disabled fieldset stop one browser tab from sending the same
order or import twice. **They do not make the API idempotent.** A retry after a
lost response, a second tab, or another client can still create a second order
or import. Real protection needs a backend change (see the production readiness
review).

## Forms

`features/orders/order-form/order-form.ts` holds the typed form model, the
validators and the mapping to `CreateOrderRequest`. Rules mirror the backend
validators and column sizes:

| Field | Rule (client) | Backend |
|---|---|---|
| Customer | Customer number (int ≥ 1) **or** legacy id (1–10 chars, not blank) | `CustomerId` or `LegacyCustomerId`; 404 if no customer matches |
| Currency | Three letters, sent upper-case | `^[A-Za-z]{3}\z` |
| Status | Required, ≤ 30 chars, free text | `NotEmpty`, `MaximumLength(30)` |
| Lines | At least one | `NotEmpty` |
| SKU | Required, ≤ 50 | `NotEmpty`, `MaximumLength(50)` |
| Description | Optional, ≤ 200 | `NotNull`, `MaximumLength(200)` |
| Quantity | Whole number 1 to 2,147,483,647 | `GreaterThan(0)`, C# `int` |
| Unit price | ≥ 0, at most 2 decimals, ≤ 999,999,999,999.99 | `≥ 0`, `decimal(18,2)` |

The unit price limit is stricter than the API's on purpose: a JavaScript
`number` holds about 15 significant digits exactly.

Server validation errors (`ValidationProblemDetails`) are mapped back to the
matching control, including `Items[1].Quantity` lines, by
`normalizeFieldErrors` and `applyServerErrors`. Messages that match no control
appear in the error summary.

Money is never added as floating point: `shared/money/money.ts` converts to
whole cents as `BigInt`, so `0.10 × 3` shows `0.30`.

## Errors

| Kind | Shown as | Retry? |
|---|---|---|
| `network`, `unavailable`, `server`, `unexpected` | Error state with the API's `traceId` | Yes |
| `validation` | Field errors and a summary | No |
| `not-found` | Empty state ("Order not found", "Job not found") | No |
| `rejected` (other 4xx) | The API's `detail` | No |

A 5xx `detail` is never shown: in Development the API puts the stack trace there.

## Dates

- Date ranges are UTC calendar days, sent as `T00:00:00.0000000Z` to
  `T23:59:59.9999999Z`, because the API compares `OrderDate <= toDate`.
- An order read back from the database has no offset in `orderDate`
  (`2026-11-09T10:15:00.1234567`). `parseUtc` treats it as UTC; the `date` pipe
  is always given `'UTC'`.

## What the API does not support

These are backend limitations. The frontend does not invent endpoints for them.

| Need | Current API | Backend change required |
|---|---|---|
| Correct totals across currencies | `GET /api/customers/totals` adds every currency together and returns no currency | Group totals by currency (`{ customerId, currency, total }[]`), or convert to a reporting currency |
| Scale to many customers | Totals return every customer, unpaged | Add paging, sorting and search parameters |
| Order date | Not accepted; the server stores `DateTime.UtcNow` | Add an optional `OrderDate` with validation, if back-dating is a business need |
| List or search orders | Only `GET /api/orders/{id}` | Add `GET /api/orders?customerId=&from=&to=&page=` |
| List import jobs | Only `GET /api/import/jobs/{id}`; this tab remembers ids in `sessionStorage` | Add `GET /api/import/jobs?page=` |
| Line-level import errors | Stored in `ImportErrors`, not exposed | Add `GET /api/import/jobs/{id}/errors?page=` |
| Import progress percentage | Total line count is not reported | Store and return the number of lines in the snapshot |
| Retry a failed job | Not supported; a new import starts from the beginning (customers are upserted by legacy id) | A retry endpoint that resumes from the checkpoint |
| Upload a file | The import reads a fixed file on the server | A multipart upload endpoint with size limits and malware scanning |
| Customer details or creation | Customers only come from imports | `GET /api/customers/{id}` and, if needed, create and update endpoints |
| Safe retries of `POST` | Not idempotent | An `Idempotency-Key` header stored with the order or job |
