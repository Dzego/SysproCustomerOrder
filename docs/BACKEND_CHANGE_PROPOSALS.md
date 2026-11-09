# Backend change proposals

For the developers and reviewers of `Syspro.Api`. Each proposal fixes a
production-readiness gap that the frontend cannot fix on its own. **None of them is
implemented.** Each needs approval before any backend code changes.

| # | Proposal | Why | Priority |
|---|---|---|---|
| 1 | Authentication and authorization | Every endpoint is anonymous | **Blocker** |
| 2 | Per-currency customer totals | Totals add different currencies together | High |
| 3 | Idempotent order creation | A retry after a lost response can create a duplicate order | High |
| 4 | Customer search and paged totals | The only customer list returns every customer at once | Medium |

## 1. Authentication and authorization (security blocker)

### What the code does today

Checked in `Syspro.Api`: there is no `AddAuthentication`, `AddAuthorization`,
`UseAuthorization`, `[Authorize]` or `[AllowAnonymous]`, and no authentication
package. Anyone who can reach the API can:

- read every customer's name, legacy ID and spending (`GET /api/customers/totals`);
- create orders for any customer (`POST /api/orders`);
- start imports that rewrite customer data from the server's file (`POST /api/import/customers`);
- read any order or import job by guessing its number.

### Other security findings

| Finding | Risk | Fix |
|---|---|---|
| In `Development`, 500 responses put the full exception and stack trace in `detail` | Leaks internals if production ever runs as Development | Deploy with `ASPNETCORE_ENVIRONMENT=Production`; add a startup check that refuses Development outside localhost |
| No rate limiting | Anyone can flood order creation or imports | `AddRateLimiter`, stricter on `POST` endpoints |
| Order and job IDs are sequential integers | Easy to enumerate once authenticated | Resource-level authorization (below) |
| `AllowedHosts: "*"` | Host header spoofing is easier | Set the real host names |
| The development connection string uses `sa` | Too much privilege | A SQL login with only the rights the app needs |

### Proposed design

- **Authentication:** OpenID Connect sign-in through the organisation's identity
  provider (for example Microsoft Entra ID), with the API validating JWT bearer
  tokens (`Microsoft.AspNetCore.Authentication.JwtBearer`).
- **Authorization:** a fallback policy requiring an authenticated user, so a new
  endpoint is protected by default, plus role policies:

| Endpoint | Policy |
|---|---|
| `GET /api/customers/totals` | `Orders.Read` |
| `GET /api/orders/{id}` | `Orders.Read` |
| `POST /api/orders` | `Orders.Write` |
| `POST /api/import/customers` | `Imports.Run` (administrators only) |
| `GET /api/import/jobs/{id}` | `Imports.Run` |

- **Responses:** 401 and 403 as ProblemDetails, like every other error.
- **Frontend work once this exists:** a sign-in flow, an `HttpInterceptor` in
  `CoreModule` that adds the token (the module already registers
  `withInterceptorsFromDi()` for it), handling for 401 (sign in again) and 403
  ("you do not have access"), and hiding actions the user's role cannot use.

## 2. Per-currency customer totals

### Problem

`GET /api/customers/totals` returns one `total` per customer that adds
`UnitPrice × Quantity` across orders in every currency, and does not say which. An
order in ZAR and one in USD for the same customer produce a single meaningless
number.

What the frontend does now: the dashboard shows only currency-free counts and a
notice. Each customer's figure is labelled "Recorded value (unconverted)". No sums,
averages or shares across customers are calculated.

### Proposed contract

A new endpoint, so the existing one keeps working for current clients:

```http
GET /api/customers/totals/by-currency?fromDate=...&toDate=...
```

```json
[
  {
    "customerId": 1,
    "legacyCustomerId": "0000012345",
    "name": "Acme Trading",
    "totals": [
      { "currency": "USD", "total": 59.97, "orderCount": 1 },
      { "currency": "ZAR", "total": 1250.50, "orderCount": 3 }
    ]
  },
  { "customerId": 2, "legacyCustomerId": "0000012346", "name": "Globex", "totals": [] }
]
```

- The query groups by `CustomerId, Currency`, so totals are only ever added within
  one currency.
- `totals` is empty for customers without orders in the range, so they still appear.
- Sorting needs a defined meaning, for example by a `currency` parameter
  (`?sortCurrency=ZAR`), because "highest spender" has no meaning across currencies.

With that, the dashboard can show sums, averages and shares **per currency**.

**Alternative:** convert to a reporting currency on the server. That needs a source
of exchange rates, and a rule for which date's rate applies (order date or report
date). It is a business decision, not just a technical one.

## 3. Idempotent order creation

### Problem

`POST /api/orders` creates a new order every time. The frontend prevents double
clicks with `exhaustMap` and a disabled form. But if the request reaches the server
and the response is lost (a timeout, dropped connection or proxy error), the user
cannot tell whether the order was saved. Sending it again then creates a duplicate.

What the frontend does now: after a network error, timeout or 5xx, it says the order
**may** have been saved and that sending again may create a duplicate. It does not
pretend a retry is safe.

### Proposed design

1. The client sends `Idempotency-Key: <UUID>` with `POST /api/orders`. The frontend
   creates one key per order being entered, keeps it for every retry of that order,
   and creates a new key after a success or when the user changes the order.
2. A new table records each key:

| Column | Type | Notes |
|---|---|---|
| `Key` | `uniqueidentifier` | Primary key |
| `RequestHash` | `binary(32)` | SHA-256 of the normalized request body |
| `OrderId` | `int` | Foreign key to `Orders` |
| `CreatedAt` | `datetime2` | For expiry |

3. In the same transaction as the order insert, the API inserts the key row. The
   primary key makes a concurrent duplicate fail.
4. Replaying a key:
   - with the same body returns `201` and the original order, without creating another;
   - with a different body returns `422` with a ProblemDetails explaining the key was
     reused;
   - while the first request is still running returns `409`, and the client retries
     shortly.
5. Keys expire after, for example, 24 hours, removed by a cleanup job.
6. A missing key keeps today's behaviour, so existing clients keep working. The
   frontend sends one once the endpoint supports it.

The same pattern applies to `POST /api/import/customers` if starting a duplicate
import ever becomes a problem. Imports are already safe to repeat, because customers
are matched by legacy ID.

## 4. Customer search and paged totals

### Problem

The API's only way to list customers is `GET /api/customers/totals`, which returns
**every** customer in one response, each with a sum over their orders. Three places
in the frontend depend on it:

| Page | Use | Cost today |
|---|---|---|
| Dashboard | Counts and the top five | Downloads and sums every customer to show five rows |
| Customer spending | Table with search, sort and paging in the browser | Whole list in memory |
| New order | Name hint and suggestions for the identifier | Whole list for all dates, loaded on the first keystroke |

With 60,000 customers the response is several megabytes, and the server sums every
order of every customer for each request.

What the frontend does now: the new-order page only loads the list once an
identifier is typed, not when it opens, and the customer page renders one page of
rows at a time. These reduce the cost but do not remove it.

### Proposed contracts

```http
GET /api/customers?search=acme&page=1&pageSize=20
```

Returns `{ items: [{ customerId, legacyCustomerId, name, tier }], page, pageSize, totalCount }`.
`search` matches a prefix of the legacy ID or part of the name. This needs an index on
`Name`; `LegacyCustomerId` is already uniquely indexed.

```http
GET /api/customers/by-legacy-id/{legacyCustomerId}
```

Returns one customer or 404. The new-order page would call it, debounced, instead of
downloading the whole list.

```http
GET /api/customers/totals/by-currency?fromDate=&toDate=&search=&sortCurrency=ZAR&sort=total-desc&page=1&pageSize=25&hideZero=true
```

The per-currency totals from proposal 2, paged, sorted and filtered on the server,
returning `totalCount`. The dashboard asks for `pageSize=5`.

Frontend changes once these exist: the customer page sends its search, sort, filter
and page to the API (`switchMap` already cancels stale requests). The new-order page
replaces the directory with a debounced `by-legacy-id` lookup and a `search` call for
suggestions.
