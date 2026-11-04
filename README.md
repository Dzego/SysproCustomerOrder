# SYSPRO Legacy Customer Import & Order API

A .NET 8 backend application that imports customer data from a legacy fixed-width file into SQL Server and exposes REST API endpoints for creating, retrieving and aggregating customer orders.

The solution was developed as part of the SYSPRO Intermediate .NET Developer take-home assessment.

The project demonstrates:

- Fixed-width legacy data parsing
- Repeatable and idempotent-style customer imports
- Row-level import error handling
- Repository Pattern for the legacy import workflow
- SQL Server persistence using Entity Framework Core
- Entity Framework Core migrations
- REST API design using ASP.NET Core Controllers
- Order creation using either internal or legacy customer identifiers
- Order retrieval
- Computed order and line totals
- Customer order aggregation over a date range
- Basic validation and error handling
- Automated testing using xUnit

---

# Technology Stack

- C#
- .NET 8
- ASP.NET Core Web API
- Entity Framework Core
- SQL Server 2022
- Docker / Docker Compose
- xUnit
- EF Core InMemory provider for isolated service tests

---

# Project Structure

```text
SysproCustomerOrder/
│
├── Syspro.Api/
│   ├── BackgroundJobs/
│   │   ├── CustomerImportWorker.cs
│   │   └── CustomerImportWorkerOptions.cs
│   │
│   ├── Controllers/
│   │   ├── CustomersController.cs
│   │   ├── ImportController.cs
│   │   └── OrdersController.cs
│   │
│   ├── Data/
│   │   └── AppDbContext.cs
│   │
│   ├── DTOs/
│   │
│   ├── LegacyData/
│   │   └── customers_legacy.dat
│   │
│   ├── ExceptionHandling/
│   │   ├── GlobalExceptionHandler.cs
│   │   └── ProblemDetailsCustomization.cs
│   │
│   ├── Migrations/
│   │
│   ├── Models/
│   │
│   ├── Repositories/
│   │   ├── ICustomerRepository.cs
│   │   ├── CustomerRepository.cs
│   │   ├── IImportRepository.cs
│   │   └── ImportRepository.cs
│   │
│   ├── Services/
│   │   ├── BusinessRuleException.cs
│   │   ├── ResourceNotFoundException.cs
│   │   ├── CustomerImportJobRunner.cs
│   │   ├── CustomerImportJobService.cs
│   │   ├── CustomerImportService.cs
│   │   ├── LegacyCustomerFile.cs
│   │   ├── ICustomerImportService.cs
│   │   ├── LegacyCustomerParser.cs
│   │   ├── LegacyCustomerParseException.cs
│   │   ├── IOrderService.cs
│   │   └── OrderService.cs
│   │
│   └── Program.cs
│
├── Syspro.Tests/
│
├── Syspro.IntegrationTests/
│
├── docs/
│   └── DEPLOYMENT_RUNBOOK.md
│
├── docker-compose.yml
├── README.md
├── SOLUTION.md
└── SysproCustomerOrder.sln
```

> `OrdersApiTests.cs` represents the lightweight API integration test required by the assessment. If this test has not yet been added, remove it from the structure until it exists.

---

# Architecture

The application uses a lightweight layered architecture.

```text
Client / Postman
        |
        v
ASP.NET Core Controllers
        |
        v
Application Services
        |
        +----------------------------+
        |                            |
        v                            v
Legacy Import                  Order Logic
        |                            |
        v                            |
Repository Interfaces                |
        |                            |
        v                            |
Repository Implementations           |
        |                            |
        +-------------+--------------+
                      |
                      v
                 AppDbContext
                      |
                      v
                  SQL Server
```

The Repository Pattern is used specifically for the legacy customer import workflow.

The order workflow currently uses `AppDbContext` directly from `OrderService`. This was an intentional scope and complexity decision rather than introducing repository abstractions purely for architectural symmetry.

The detailed architecture decisions and trade-offs are documented in `SOLUTION.md`.

---

## Background Imports

Customer imports run as durable jobs in SQL Server, so nothing is lost if an instance stops.

```text
POST /api/import/customers
   saves the job and a copy of the file in one transaction, then returns 202
        |
        v
CustomerImportWorker, on any instance, polls SQL Server
   claims the next job atomically, with a lease and a new fencing token
        |
        v
CustomerImportService reads the stored copy in 1 MiB chunks, then for each batch:
   BEGIN
     check the fencing token and renew the lease, locking the job's row
     look up the batch's customers as a set, then insert or update them
     insert row errors
     add to the counters, which advances the checkpoint
     on the last batch: mark Completed and delete the stored copy
   COMMIT
```

| Component | Responsibility |
| --- | --- |
| `ImportController` and `CustomerImportJobService` | Accept jobs and report their status |
| `CustomerImportWorker` | Hosted service that polls for work and survives failures |
| `CustomerImportJobRunner` | Claims a job, runs it, and decides between retrying, failing and releasing it |
| `CustomerImportService` | Reads lines, applies the import rules, and commits each batch |
| `ImportRepository` and `CustomerRepository` | SQL Server access, including claims and fencing |

See [Deployment and Rollback Runbook](docs/DEPLOYMENT_RUNBOOK.md) before deploying.

---

# Running the Project Locally

## Prerequisites

Make sure the following are installed:

- .NET 8 SDK
- Docker Desktop
- Git

Verify the .NET installation:

```bash
dotnet --version
```

---

## 1. Clone the Repository

```bash
git clone https://github.com/Dzego/SysproCustomerOrder.git
cd SysproCustomerOrder
```

---

## 2. Start SQL Server

SQL Server runs locally using Docker Compose.

From the repository root:

```bash
docker compose up -d
```

Confirm that the container is running:

```bash
docker ps
```

---

## 3. Configure the Database Connection

The application expects a SQL Server connection string named:

```text
DefaultConnection
```

Database credentials should not be committed to source control.

For local development, .NET User Secrets can be used.

From the API project:

```bash
cd Syspro.Api
dotnet user-secrets init
```

Configure the connection string:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
"Server=localhost,1433;Database=SysproCustomerOrderDb;User Id=sa;Password=<YOUR_PASSWORD>;TrustServerCertificate=True;"
```

Replace `<YOUR_PASSWORD>` with the password configured for your local SQL Server Docker container.

---

## 4. Apply Database Migrations

From the `Syspro.Api` directory:

```bash
dotnet ef database update
```

This creates the database using the included Entity Framework Core migrations.

The main application tables are:

```text
Customers
Orders
OrderItems
ImportLogs
ImportErrors
```

---

## 5. Run the API

From the `Syspro.Api` directory:

```bash
dotnet run
```

The terminal will display the local URL.

For example:

```text
http://localhost:5204
```

If a different port is displayed, use the URL shown in the terminal.

---

# Legacy Customer Import

The initial customer data is loaded from the supplied fixed-width legacy file.

The file is located at:

```text
Syspro.Api/LegacyData/customers_legacy.dat
```

Each line represents one customer.

---

## Fixed-Width Format

The parser reads each field according to its fixed character positions.

```text
Positions 1-10   LegacyCustomerId
Positions 11-40  FullName
Positions 41-70  Email
Positions 71-78  SignupDate (yyyyMMdd)
Positions 79-80  Tier
```

Example:

```text
0000012345John Smith                    john.smith@example.com        20210315A 
0000012346Jane Doe                      jane.doe@example.com          20201228B 
0000012347Acme Corp                     ops@acmecorp.example          20190501C 
```

Spaces are significant because the parser extracts fields according to character positions.

`LegacyCustomerId` is stored as a string so identifiers such as:

```text
0000012345
```

retain their leading zeros.

---

# Run the Customer Import

## Endpoint

```http
POST /api/import/customers
```

No request body is required.

Example:

```text
POST http://localhost:5204/api/import/customers
```

Imports run in the background. The request saves a job and returns straight away, without waiting for the import.

> **Breaking change:** this endpoint used to process the file during the request and return `200 OK` with the counts. It now returns `202 Accepted`, and clients read the counts from the job. A failed import no longer returns `500`; the job reports `Failed` instead.

## Example Response

```http
HTTP/1.1 202 Accepted
Location: /api/import/jobs/12
```

```json
{
  "id": 12,
  "status": "Pending",
  "createdAt": "2026-11-04T08:15:00.1234567Z",
  "startedAt": null,
  "completedAt": null,
  "processed": 0,
  "created": 0,
  "updated": 0,
  "failed": 0,
  "failureReason": null
}
```

If the legacy file does not exist, the endpoint returns `404 Not Found` and creates no job. If it is larger than `CustomerImport:MaxInputBytes`, the endpoint returns `413 Payload Too Large` and creates no job.

## Check an Import Job

```http
GET /api/import/jobs/{id}
```

```json
{
  "id": 12,
  "status": "Completed",
  "createdAt": "2026-11-04T08:15:00.1234567Z",
  "startedAt": "2026-11-04T08:15:03.2345678Z",
  "completedAt": "2026-11-04T08:15:03.9876543Z",
  "processed": 7,
  "created": 2,
  "updated": 3,
  "failed": 2,
  "failureReason": null
}
```

The exact `created` and `updated` values depend on the current state of the database. The counts show what has been committed so far, so they grow while the job runs. An unknown id returns `404 Not Found`.

The `failed` count covers invalid rows, which are stored in `ImportErrors`. They do not make a job fail. A job is `Failed` only when the import as a whole could not finish, and `failureReason` then holds a safe description. Exception details go only to the application logs.

> **A `Failed` job may have changed some customers.** Lines are committed in batches, and earlier batches stay applied when a later one fails. Nothing undoes them. The counts and `failureReason` state how far the job got, for example: "The import failed unexpectedly. Lines 1 to 2000 were committed and remain applied. Processing stopped at line 2001." Sending a new request re-imports the file from the start, which is safe because customers are matched by `LegacyCustomerId`.

## Job Lifecycle

| Status | Meaning |
| --- | --- |
| `Pending` | Saved and waiting for the worker |
| `Processing` | Claimed by the worker, and committing batches |
| `Completed` | Every line was processed |
| `Failed` | Stopped permanently. Batches committed before that remain applied |

```text
Pending -> Processing -> Completed or Failed
Processing -> Pending, when interrupted work is queued to run again
```

The job is stored in `ImportLogs`, where the database enforces valid status and timestamp combinations. Its input is captured when it is accepted (see "Job Input Snapshot").

## Restarts and Interruptions

Each batch, 1,000 lines by default, is one transaction containing its customers, row errors and counters. `ProcessedCount` is the checkpoint, so it can never disagree with what was committed. The last batch also sets `Completed` and removes the stored input.

A job that runs again resumes at the first uncommitted line. Committed lines are not applied again, and their errors are not recorded twice. If a connection fails around a commit, the worker cannot tell whether it succeeded, so it rereads the job from the database before deciding what to do.

This gives each line's database changes **at most one commit per job, not exactly-once execution**: a batch can be computed and rolled back several times, and other readers can see a job's earlier batches before it finishes.

| Event | Result |
| --- | --- |
| Client disconnects before the job is saved | No job is created |
| Client disconnects after `202 Accepted` | The job is unaffected |
| API stops or restarts with jobs `Pending` | They run after the restart |
| Graceful shutdown during an import | The batch in progress rolls back and the job returns to `Pending`, not counted as a failure. It resumes after restart |
| Crash during an import | The job stays `Processing` until its lease expires, then a worker reclaims it and it resumes |
| Temporary database failure, such as a deadlock, timeout or lost connection | The batch rolls back and the job retries from its checkpoint |
| `MaxAttempts` executions in a row with no batch committed | The job becomes `Failed`, which stops a job that crashes the process or keeps failing from looping forever |
| Any other failure in a batch | That batch rolls back and the job becomes `Failed`. Earlier batches remain applied. Send a new request to try again |

## Job Counters and Attempts

| Column | Meaning |
| --- | --- |
| `ProcessedCount` | Lines processed. The database requires this to equal `CreatedCount + UpdatedCount + FailedCount` |
| `AttemptCount` | Lifetime executions of the job, for reporting |
| `ConsecutiveFailureCount` | Executions since the job last committed a batch. `MaxAttempts` applies to this. It is counted when a job starts, because a crash cannot record anything, and undone by a graceful shutdown |

`ImportErrors` allows one error for each line of a job.

The `AddImportCountersAndRetryTracking` migration checks existing rows against these rules first. If any row violates them, it stops with an error naming the rows and changes nothing. Correct those rows and run it again. When applying a generated SQL script with `sqlcmd`, use `-b` so that the script stops at that error.

## Configuration

| Setting | Default | Purpose |
| --- | --- | --- |
| `CustomerImport:MaxInputBytes` | 52428800 (50 MiB) | Largest file accepted. Up to 1 GiB. A larger file returns `413` |
| `CustomerImport:BatchSize` | 1000 | Lines per transaction, from 1 to 10,000. Larger batches hold locks for longer |
| `CustomerImport:LeaseDuration` | 00:02:00 | How long a crashed worker's job waits before another takes over. Up to one hour. Keep it well above the time one batch takes |
| `CustomerImportWorker:Enabled` | true | Set to false on instances that should not process imports |
| `CustomerImportWorker:PollInterval` | 00:00:05 | How often an idle worker looks for work. Up to five minutes |
| `CustomerImportWorker:MaxAttempts` | 3 | Executions in a row with no batch committed before a job becomes `Failed` |

```json
"CustomerImportWorker": {
  "Enabled": true,
  "PollInterval": "00:00:05",
  "MaxAttempts": 3
}
```

`PollInterval` must be above zero and at most five minutes. A new job can wait up to one interval before it starts.

## Multiple Instances

Any number of API instances can run with the worker enabled. Only one import job runs at a time across all of them, in the order the jobs were accepted, and the others wait. This keeps results predictable, because import order decides each customer's final data.

- **Claiming:** one atomic SQL transaction picks the next job and starts its lease once it holds the row, and a unique index allows only one `Processing` job.
- **Leases:** the claiming worker holds a lease, timed by the SQL Server clock so that clock differences between servers do not matter. Each batch renews it.
- **Crashes:** another worker takes over once the lease expires, after `LeaseDuration` at most plus one poll, and resumes from the checkpoint.
- **Graceful shutdown:** the job is released at once, so another worker can continue straight away.
- **Fencing:** every claim raises the job's `FencingToken`. Each batch checks it first, and so does every other change to the job. A worker whose job was reclaimed holds an old token, so it stops without writing anything.

If a batch is committing when its lease runs out, it still succeeds, because it holds the job's row and its token is still current. The reclaim waits for that commit and then resumes after it. This keeps a job from running on two workers at once in a way that can apply the same line twice, though a slow worker's in-progress work can be thrown away.

**Known limitation: a frozen worker can hold up a takeover.** A lease expiring is not enough for a takeover if the worker froze during a batch, for example a suspended process or VM. It still holds the job's row in an open SQL Server transaction, so a reclaim waits for that lock, however long that takes. This is what stops two workers writing at once, so it costs time rather than correctness. A worker frozen between batches holds no lock and is taken over soon after its lease expires. See [Operational Troubleshooting](#operational-troubleshooting) for the recovery steps.

`LeaseDuration` should be well above the time one batch takes. Too short a lease does not lose data, but healthy workers lose their jobs and repeat work.

A rolling or blue-green deployment is safe once every instance runs this version. **Moving to this version from an earlier one is not:** stop every worker on the old version before applying the `AddImportJobLeasing` migration. The migration returns any `Processing` job to `Pending`, and the old code's claims are then rejected by the database.

## Job Input Snapshot

When a job is accepted, the complete legacy file is copied into `ImportJobInputs` in the same transaction as the job, so a job can never exist without its input. The worker imports only that stored copy and never reads the file.

| If the file... | Then... |
| --- | --- |
| Is replaced after the job is accepted | The job still imports what was accepted |
| Is deleted after the job is accepted | The job still completes |
| Changes before a retry | The retry uses the same input as the interrupted attempt |

A job with no stored input becomes `Failed` with an explanation, and is never run from the current file. This applies to any job queued before snapshots were introduced. Send a new request for those.

The snapshot captures the file as it is when the request arrives, so a file that is still being written is captured partly written, and a cut-off row counts as one bad row in a `Completed` job. Move a fully written file into place rather than copying over the existing one.

### Snapshot Retention

| Job status | Stored input |
| --- | --- |
| `Pending` | Kept |
| `Processing`, including after a crash or failed commit | Kept, because a retry needs it. A retry reads the committed lines again to rebuild its duplicate check |
| Requeued after an interruption | Kept |
| `Completed` | Deleted in the same transaction as the results |
| `Failed` | Deleted in the same transaction as the status |

No scheduled cleanup is needed, because a finished job removes its own input. Reverting the `AddImportJobInputs` migration drops all stored inputs. Row-level failures stay in `ImportErrors`, where `RawData` keeps up to 80 characters of each failed row.

### Maximum Input Size

```json
"CustomerImport": {
  "MaxInputBytes": 52428800,
  "BatchSize": 1000,
  "LeaseDuration": "00:02:00"
}
```

The default is 50 MiB, and the maximum allowed setting is 1 GiB. A larger file returns `413 Payload Too Large` and creates no job. The whole file is held in memory while it is accepted. The worker reads it back from SQL Server in 1 MiB chunks and tracks one batch at a time. It also keeps every `LegacyCustomerId` seen so far in memory to detect duplicates, so its memory use still grows with the number of customers in the file.

`BatchSize` must be between 1 and 10,000. Larger batches hold locks for longer. `LeaseDuration` must be above zero and at most one hour.

---

# Operational Troubleshooting

To see the state of unfinished jobs, use the query in the [runbook](docs/DEPLOYMENT_RUNBOOK.md#recovery-and-troubleshooting).

| Symptom | Likely cause | What to do |
| --- | --- | --- |
| Job stays `Pending` | No worker is enabled and running, or another job is active. Only one runs at a time | Check `CustomerImportWorker:Enabled` and the logs. Check for a `Processing` job |
| Job stays `Processing` with counts not moving | Its worker crashed, or it is blocked | Wait for `LeaseExpiresAt`, then another worker takes over. It needs a running worker. If `LeaseExpiresAt` keeps moving, the worker is alive and committing |
| Takeover seems stuck after a worker froze, and other workers log `worker cycle failed` with a timeout about every 30 seconds | The frozen process holds the job's row in an open transaction, and each claim waits behind it until it times out. No data is at risk, but nothing can take over until the lock is released | Stop the frozen process, or end its SQL session with `KILL` (see the [runbook](docs/DEPLOYMENT_RUNBOOK.md#recovery-and-troubleshooting)). SQL Server rolls back the batch and another worker resumes from the last checkpoint. If the process resumes instead, it may finish its batch, and it carries on only if no other worker took the job. Otherwise it stops without writing anything more |
| `Failed` with "Lines 1 to N were committed" | A batch failed permanently. Earlier batches remain applied | Fix the cause from the logs, using the job's id, then submit a new import |
| `Failed` with "interrupted too many times" | The job crashed its worker, or lost it, `MaxAttempts` times with no progress | Look for out-of-memory kills or crashes. Lower `BatchSize` if batches are the problem |
| `Failed` with "kept failing on a temporary database problem" | Deadlocks, timeouts or lost connections repeated with no progress | Check SQL Server health, then submit a new import |
| `Failed` with "no stored input" | The job was queued before stored inputs existed | Submit a new import |
| Many row errors with "only printable ASCII" | The file contains IDs that `varchar` cannot store safely | Correct the file. These rows change nothing |
| `413` from the import endpoint | The file is above `MaxInputBytes` | Raise the limit, considering memory, or split the file |
| Worker logs an error every poll | The database is unreachable or not migrated | Fix the connection or apply migrations. The API keeps running, and the worker retries |
| `Migration stopped and nothing was changed` | Existing rows violate the new constraints | Correct the rows it names, then run it again |

For a worker error, search the logs for the job's id, which appears as `JobId`. Exception details appear only there, never in `failureReason`.

---

# Import Behaviour

The import is designed to be repeatable.

Customers are matched using `LegacyCustomerId`.

If the customer does not exist:

```text
Create Customer
```

If the customer already exists:

```text
Update Customer
```

This means running the same legacy file repeatedly does not create duplicate customers.

A unique database constraint on `LegacyCustomerId` provides an additional database-level safeguard against duplicates.

---

# Repository Pattern for Import Persistence

The legacy customer import uses focused repository abstractions:

```text
ICustomerRepository
IImportRepository
```

with concrete implementations:

```text
CustomerRepository
ImportRepository
```

The flow is:

```text
CustomerImportService
        |
        +----------------------+
        |                      |
        v                      v
ICustomerRepository      IImportRepository
        |                      |
        v                      v
CustomerRepository       ImportRepository
        |                      |
        +----------+-----------+
                   |
                   v
              AppDbContext
                   |
                   v
               SQL Server
```

This keeps `CustomerImportService` focused on the import workflow rather than Entity Framework Core query details.

The service is responsible for:

- Parsing legacy records
- Determining whether customers should be created or updated
- Maintaining import counts
- Handling invalid legacy rows
- Coordinating the import workflow

The repositories are responsible for persistence operations.

Both repositories share one scoped `AppDbContext`, so `IImportRepository.ExecuteInTransactionAsync` gives each batch one explicit transaction across them, and clears change tracking afterwards.

A generic `IRepository<T>` abstraction was deliberately avoided. The repositories expose focused operations required by the application rather than wrapping every EF Core CRUD operation.

The Repository Pattern is currently used for the import workflow only.

`OrderService` uses `AppDbContext` directly because the current order persistence operations are relatively focused and straightforward.

Adding `IOrderRepository` and `OrderRepository` purely for structural consistency would introduce additional abstraction without currently removing meaningful complexity.

More detail about this trade-off is provided in `SOLUTION.md`.

---

# Invalid Legacy Rows

A malformed row does not stop the complete import.

Instead:

```text
Invalid row
    |
    v
Record ImportError
    |
    v
Increment FailedCount
    |
    v
Continue with next row
```

Every import execution is represented by an `ImportLog`, which also acts as its background job.

The log records:

```text
Status
AttemptCount
FailureReason
CreatedAt
ProcessedCount
CreatedCount
UpdatedCount
FailedCount
StartedAt
CompletedAt
```

Individual failed rows are stored in `ImportErrors`.

A `LegacyCustomerId` that appears more than once in a file is also a row-level failure: the first valid occurrence is processed, and later occurrences are recorded as errors and change nothing. Customers already in the database are still updated by that first occurrence.

`RawData` holds at most 80 characters. A longer row is truncated when stored, and `Reason` records its original length.

Each error records:

```text
LineNumber
RawData
Reason
```

A `LegacyCustomerId` must be 1 to 10 printable ASCII characters. A blank ID, or one with other characters, is recorded as a row error. The column is `varchar`, where unsupported characters become `?`, so different IDs could otherwise match the same row and update the wrong customer.

Example parsing errors include:

```text
Invalid signup date: 20241340
```

and:

```text
Invalid tier: Z
```

The supported legacy tiers are:

```text
A
B
C
```

Parsing failures are represented by the custom:

```text
LegacyCustomerParseException
```

This allows the import workflow to distinguish expected legacy parsing failures from unrelated application failures.

---

# Order API

Orders can only be created for customers that already exist in the database.

A customer can be identified using either:

- Internal `CustomerId`
- `LegacyCustomerId`

An order must contain at least one item.

---

# Create an Order Using LegacyCustomerId

## Endpoint

```http
POST /api/orders
```

## Example Request

```json
{
  "legacyCustomerId": "0000012346",
  "currency": "ZAR",
  "status": "Created",
  "items": [
    {
      "sku": "SKU-003",
      "description": "Monitor",
      "unitPrice": 2500.00,
      "quantity": 1
    },
    {
      "sku": "SKU-004",
      "description": "USB-C Cable",
      "unitPrice": 250.00,
      "quantity": 2
    }
  ]
}
```

---

# Create an Order Using Internal CustomerId

The same endpoint can identify a customer using the application's internal database ID.

## Example Request

```json
{
  "customerId": 1003,
  "currency": "ZAR",
  "status": "Created",
  "items": [
    {
      "sku": "SKU-020",
      "description": "External Hard Drive",
      "unitPrice": 1450.00,
      "quantity": 1
    },
    {
      "sku": "SKU-021",
      "description": "USB Hub",
      "unitPrice": 350.00,
      "quantity": 2
    }
  ]
}
```

## Example Response — 201 Created

```json
{
  "id": 1,
  "customerId": 1003,
  "orderDate": "2026-08-18T20:03:44.335706Z",
  "currency": "ZAR",
  "status": "Created",
  "total": 2150.00,
  "items": [
    {
      "id": 1,
      "sku": "SKU-020",
      "description": "External Hard Drive",
      "unitPrice": 1450.00,
      "quantity": 1,
      "lineTotal": 1450.00
    },
    {
      "id": 2,
      "sku": "SKU-021",
      "description": "USB Hub",
      "unitPrice": 350.00,
      "quantity": 2,
      "lineTotal": 700.00
    }
  ]
}
```

Order timestamps are generated by the backend in UTC.

---

# Order Total Calculation

Order totals are calculated by the backend rather than supplied by the client.

For each item:

```text
LineTotal = UnitPrice × Quantity
```

The complete order total is:

```text
OrderTotal = Sum of all LineTotals
```

For example:

```text
External Hard Drive
R1450 × 1 = R1450

USB Hub
R350 × 2 = R700

Order Total = R2150
```

This ensures the total returned by the API is derived from the actual order items.

---

# Retrieve an Order by Id

## Endpoint

```http
GET /api/orders/{id}
```

Example:

```text
GET http://localhost:5204/api/orders/1
```

## Example Response — 200 OK

```json
{
  "id": 1,
  "customerId": 1003,
  "orderDate": "2026-08-18T20:03:44.335706Z",
  "currency": "ZAR",
  "status": "Created",
  "total": 2150.00,
  "items": [
    {
      "id": 1,
      "sku": "SKU-020",
      "description": "External Hard Drive",
      "unitPrice": 1450.00,
      "quantity": 1,
      "lineTotal": 1450.00
    },
    {
      "id": 2,
      "sku": "SKU-021",
      "description": "USB Hub",
      "unitPrice": 350.00,
      "quantity": 2,
      "lineTotal": 700.00
    }
  ]
}
```

The response contains both individual line totals and the computed complete order total.

If the order does not exist, the API returns:

```text
404 Not Found
```

---

# Customer Order Totals

The API returns customers with their aggregate order totals over a specified date range.

## Endpoint

```http
GET /api/customers/totals?fromDate={fromDate}&toDate={toDate}
```

Example:

```text
GET http://localhost:5204/api/customers/totals?fromDate=2026-08-18&toDate=2026-08-19
```

Both dates are supplied as query parameters.

## Example Response

```json
[
  {
    "customerId": 2,
    "legacyCustomerId": "0000012346",
    "name": "Jane Doe",
    "total": 5050.00
  },
  {
    "customerId": 1003,
    "legacyCustomerId": "0000012349",
    "name": "Tech Solutions",
    "total": 2150.00
  },
  {
    "customerId": 1,
    "legacyCustomerId": "0000012345",
    "name": "John Smith",
    "total": 1250.00
  }
]
```

If a customer has multiple orders within the requested range, those orders are aggregated.

The results are ordered by total amount in descending order.

---

# Validation and Error Responses

The API performs basic validation for order creation and date-range queries.

Validation includes:

- Customer must exist.
- An order must contain at least one item.
- Item quantity must be greater than zero.
- Unit price cannot be negative.
- SKU is required.
- Currency must contain three characters.
- `fromDate` cannot be later than `toDate`.

---

## Error Response Format

All error responses use RFC 7807 ProblemDetails with the content type `application/problem+json`:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Not Found",
  "status": 404,
  "detail": "Customer was not found.",
  "instance": "/api/orders",
  "traceId": "00-f40e0c4c30a61a4097eb42240769929d-6cd4541bdfebfff0-00"
}
```

Validation failures also contain an `errors` object with the messages for each field.

| Condition | Status |
| --- | --- |
| Validation failure, malformed request or `BusinessRuleException` | 400 |
| Missing resource or `ResourceNotFoundException` | 404 |
| Request rejected by the server, such as an oversized body | The status carried by `BadHttpRequestException`, such as 413 |
| Any other exception | 500 |

Outside the `Development` environment, a 500 response contains only a generic message and the `traceId`. Use the `traceId` to find the full exception in the application logs.

---

## Customer Not Found

An order cannot be created for a customer that does not exist.

Example request:

```json
{
  "legacyCustomerId": "9999999999",
  "currency": "ZAR",
  "status": "Created",
  "items": [
    {
      "sku": "SKU-001",
      "description": "Test Item",
      "unitPrice": 100.00,
      "quantity": 1
    }
  ]
}
```

Expected response:

```text
404 Not Found
```

---

## Order With No Items

Example:

```json
{
  "legacyCustomerId": "0000012345",
  "currency": "ZAR",
  "status": "Created",
  "items": []
}
```

Expected response:

```text
400 Bad Request
```

---

## Invalid Quantity

An item quantity must be greater than zero.

Example:

```json
{
  "legacyCustomerId": "0000012345",
  "currency": "ZAR",
  "status": "Created",
  "items": [
    {
      "sku": "SKU-BAD",
      "description": "Invalid Item",
      "unitPrice": 100.00,
      "quantity": 0
    }
  ]
}
```

Expected response:

```text
400 Bad Request
```

---

## Invalid Unit Price

Unit prices cannot be negative.

Example:

```json
{
  "legacyCustomerId": "0000012345",
  "currency": "ZAR",
  "status": "Created",
  "items": [
    {
      "sku": "SKU-BAD",
      "description": "Invalid Item",
      "unitPrice": -100.00,
      "quantity": 1
    }
  ]
}
```

Expected response:

```text
400 Bad Request
```

---

## Order Not Found

Request:

```http
GET /api/orders/999999
```

Expected response:

```text
404 Not Found
```

---

## Invalid Date Range

Example:

```text
GET /api/customers/totals?fromDate=2026-12-31&toDate=2026-01-01
```

Expected response:

```text
400 Bad Request
```

---

# Automated Tests

The project uses xUnit for automated testing.

There are two test projects:

| Project | Runs against | Needs Docker |
| --- | --- | --- |
| `Syspro.Tests` | Fakes, EF Core InMemory and the in-process API host | No |
| `Syspro.IntegrationTests` | A real SQL Server container, using Testcontainers | Yes |

Run these commands from the repository root:

```bash
# Everything. Without Docker, integration tests are reported as skipped.
dotnet test

# Unit tests only
dotnet test --filter "Category!=Integration"

# SQL Server integration tests only
dotnet test --filter "Category=Integration"

# Only the tests that run the API as separate processes and kill them
dotnet test --filter "Suite=RealProcess"

# Where integration tests must run, such as CI. A missing Docker daemon fails.
SYSPRO_REQUIRE_DOCKER=true dotnet test
```

## SQL Server Integration Tests

These tests cover behaviour that EF Core InMemory cannot reproduce: migrations, unique indexes, foreign keys, delete rules, column lengths, decimal precision, string comparison rules, transaction rollback and SQL translation of queries.

Requirements:

- A running Docker daemon with at least 2 GB of memory available to containers.
- About 5 GB of free disk space for the first run, mostly for the SQL Server image.
- On Apple Silicon, the image is amd64 only and runs under emulation. Pull it once with the platform set:

```bash
docker pull --platform linux/amd64 mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04
```

One container is shared by the whole run, and each test gets its own freshly migrated database, which is dropped afterwards. Connection strings come only from that container, and the API runs in a `Testing` environment that does not load user secrets, so these tests cannot reach another database.

A test is skipped only when no Docker daemon is reachable. If Docker is running, any failure, including a container that will not start, fails the test.

---

## Real Process Tests

`RealProcessRecoveryTests` starts the real API as separate operating system processes against the test's disposable database, with nothing simulated. It uses `kill -9` during an import, `SIGTERM` during a batch, and freezes a worker with `SIGSTOP`. The frozen-worker tests check SQL Server's lock and session views to confirm where the worker stopped: between batches, it is taken over while still frozen, and while holding the job's row, nothing can take over until the lock is released. They need a Unix-like system, because they send signals.

---

## Legacy Customer Parser Tests

`LegacyCustomerParserTests` verifies the fixed-width parsing logic.

Coverage includes:

- Valid fixed-width legacy customer line
- Invalid signup date
- Invalid tier
- Invalid line length
- Empty input

Invalid parsing scenarios are expected to throw:

```text
LegacyCustomerParseException
```

---

## Order Service Tests

`OrderServiceTests` verifies order business behaviour.

Coverage includes:

- Correct total for an order containing multiple items
- Correct individual line totals
- Customer lookup using `LegacyCustomerId`
- Currency normalization
- Unknown customer handling
- Empty order items
- Invalid item quantities

The service tests use the EF Core InMemory provider.

Each test creates an isolated database using:

```csharp
.UseInMemoryDatabase(Guid.NewGuid().ToString())
```

This prevents tests from sharing database state and allows service tests to run without a local SQL Server instance.

---

## API Integration Test

The assessment requires at least one API pathway test.

The selected pathway is:

```text
Seed existing customer
        |
        v
POST /api/orders
        |
        v
201 Created
        |
        v
Read returned Order Id
        |
        v
GET /api/orders/{id}
        |
        v
200 OK
        |
        v
Verify items and computed totals
```

The lightweight integration test uses `WebApplicationFactory<Program>` to exercise the real ASP.NET Core HTTP pipeline while using an isolated test database.

This verifies more than calling a controller method directly because it exercises routing, dependency injection, JSON serialization and the controller/service integration together.

> Remove this subsection until the integration test has been implemented and is passing.

---

# Database Design

The main relationships are:

```text
Customer
   |
   | 1
   |
   | *
 Order
   |
   | 1
   |
   | *
OrderItem


ImportLog
   |
   | 1
   |
   | *
ImportError
```

A customer can have many orders.

An order belongs to one customer and contains one or more order items.

An import log represents one execution of the legacy customer import and can contain multiple import errors.

---

# Design Decisions

## LegacyCustomerId

`LegacyCustomerId` is stored separately from the application's internal customer `Id`.

It is stored as a string because leading zeros are significant.

For example:

```text
Internal Id:       1003
LegacyCustomerId:  0000012349
```

A unique database constraint is used to prevent duplicate legacy customer identifiers.

---

## Fixed-Width Parsing

Legacy records are parsed according to their character positions rather than using delimiters.

Parsing is separated from persistence so that:

- The parser has a single responsibility.
- Parsing can be tested independently.
- The parser does not require database access.
- Import orchestration remains separate from file-format concerns.

---

## Repeatable Imports

`LegacyCustomerId` determines whether an imported customer should be created or updated.

This allows the same legacy file to be processed repeatedly without creating duplicate customers.

---

## Import Error Handling

A malformed legacy row does not terminate the complete import.

The error is recorded and processing continues with the next line.

This allows valid customers later in the file to still be imported.

---

## Repository Pattern

The Repository Pattern is used for the legacy customer import workflow.

`CustomerImportService` depends on:

```text
ICustomerRepository
IImportRepository
```

rather than querying `AppDbContext` directly.

The concrete repository implementations encapsulate EF Core persistence.

This provides a clear separation between:

```text
Import orchestration
        |
Repository abstraction
        |
Persistence implementation
```

The repository interfaces are domain-focused rather than using a generic `IRepository<T>` abstraction.

This keeps repository methods aligned with actual application requirements.

### Why repositories are not used for orders

The Repository Pattern was not applied mechanically across every service.

`OrderService` currently uses `AppDbContext` directly because the order persistence requirements are relatively small and straightforward.

Adding an `IOrderRepository` purely to make the architecture symmetrical would introduce another layer without currently removing significant complexity.

If the order domain grows to contain more complex persistence rules or queries, a focused order repository can be introduced later.

This trade-off is discussed in greater detail in `SOLUTION.md`.

---

## Order Totals

Order totals are derived from the order items:

```text
UnitPrice × Quantity
```

Totals are calculated by the backend rather than accepted from the client.

This avoids trusting client-supplied totals and prevents duplicated total state from becoming inconsistent with the underlying order items.

---

## UTC Timestamps

Order timestamps are generated by the backend using UTC.

```csharp
DateTime.UtcNow
```

UTC provides consistent timezone-independent persistence.

Conversion to a user's local timezone is considered a presentation concern.

---

## EF Core Migrations

Entity Framework Core migrations are used to create and evolve the database schema.

This was selected instead of maintaining a separate SQL creation script because the schema remains closely aligned with the entity configuration and can be reproduced locally using:

```bash
dotnet ef database update
```

---

# Architecture and Design Documentation

More detailed information about the architecture, persistence decisions, Repository Pattern trade-offs and testing strategy is available in:

```text
SOLUTION.md
```

Deployment, rollback and recovery steps are in:

```text
docs/DEPLOYMENT_RUNBOOK.md
```

---

# Scope

The implementation intentionally focuses on the requirements of the assessment.

The solution avoids introducing unnecessary infrastructure such as:

```text
CQRS
MediatR
Generic repositories
Message queues
Caching
Authentication
Frontend application
Product catalogue
```

These patterns and technologies may be useful in a larger system, but introducing them here without a demonstrated requirement would increase complexity without improving the core migration and order workflows.

---

# Stopping the Local Environment

Stop SQL Server while keeping the container:

```bash
docker compose stop
```

To stop and remove the container:

```bash
docker compose down
```

The SQL Server data remains persisted using the configured Docker volume.