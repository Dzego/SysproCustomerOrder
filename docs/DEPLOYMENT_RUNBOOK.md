# Deployment and Rollback Runbook

For whoever deploys this API or responds to an import problem. It covers moving from any earlier version to the version with durable background imports and multi-instance workers, later deployments, rollback, and recovery.

## Before you start

| Check | Why |
| --- | --- |
| A tested backup exists | Rolling back after customers have changed means restoring it |
| No import is expected to run | The cutover stops all workers |
| The `CustomerImport` and `CustomerImportWorker` settings are reviewed | See [Configuration](../README.md#configuration) |
| Clients are ready for `202 Accepted` | If you are upgrading from before background imports, `POST /api/import/customers` no longer returns counts |

## Migrations in this release

| Migration | Effect |
| --- | --- |
| `AddImportJobTracking` | Makes `ImportLogs` the job table. Existing logs become `Completed` |
| `AddImportJobInputs` | Stores each job's input |
| `AddImportCountersAndRetryTracking` | Enforces consistent counters and one error per line. **Stops, changing nothing, if existing rows violate this** |
| `AddImportJobLeasing` | Adds leases and fencing, and returns any `Processing` job to `Pending` |

Apply them in order. Do not edit them.

## First deployment: the cutover

Earlier versions recover by treating every `Processing` job as abandoned, so they must not run alongside this version.

### 1. Back up

```sql
BACKUP DATABASE [SysproCustomerOrderDb]
TO DISK = '/var/opt/mssql/backup/before-import-leasing.bak'
WITH CHECKSUM, INIT;

RESTORE VERIFYONLY
FROM DISK = '/var/opt/mssql/backup/before-import-leasing.bak'
WITH CHECKSUM;
```

If `msdb` is unavailable, this reports Msg 3009 because it cannot record history, but the backup itself is still valid if `RESTORE VERIFYONLY` passes. Scripts that stop on any error would treat this as a failure, so check the verification instead.

Do not restart a stopped SQL Server container to take a backup if it stopped abnormally, because starting it writes to its files. Copy its volume first, using a read-only mount:

```bash
docker run --rm --network none --platform linux/amd64 --user root --entrypoint sh \
  -v <volume-name>:/src:ro -v "$PWD":/out \
  mcr.microsoft.com/mssql/server:2022-latest \
  -c 'tar czf /out/sql-data.tgz -C /src .'
```

### 2. Check existing data

These are read-only, and each result should be 0. They apply to a database that has at least `InitialCreate`.

```sql
SELECT COUNT(*) AS InconsistentCounters
FROM ImportLogs
WHERE ProcessedCount <> CreatedCount + UpdatedCount + FailedCount;

SELECT COUNT(*) AS DuplicateRowErrors
FROM (SELECT ImportLogId, LineNumber
      FROM ImportErrors
      GROUP BY ImportLogId, LineNumber
      HAVING COUNT(*) > 1) AS Duplicates;
```

The migration runs the same checks and stops if they fail. Review and correct those rows yourself: nothing corrects them automatically.

These do not block the migration, but they find customers that later imports treat differently:

```sql
-- Blank legacy IDs, which are no longer accepted from a file.
SELECT COUNT(*) FROM Customers WHERE LTRIM(RTRIM(LegacyCustomerId)) = '';

-- Possibly corrupted IDs, where unsupported characters were stored as '?'.
SELECT COUNT(*) FROM Customers WHERE LegacyCustomerId LIKE '%?%';
```

### 3. Stop every old instance

Stop all API instances running an earlier version, and confirm that none remain. This includes instances that autoscaling could start, and any previous deployment slot.

### 4. Apply the migrations

Generate a script once, review it, and apply it with `-b` so that it stops at the first error:

```bash
dotnet ef migrations script --idempotent --project Syspro.Api -o migrate.sql
sqlcmd -S <server> -d SysproCustomerOrderDb -I -b -i migrate.sql
```

It is safe to run again, and it skips migrations that are already applied. `-I` is required, because the filtered index needs `QUOTED_IDENTIFIER ON`.

### 5. Verify the schema

```sql
SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;

SELECT Status, COUNT(*) FROM ImportLogs GROUP BY Status;

SELECT name FROM sys.indexes WHERE name = 'UX_ImportLogs_SingleProcessing';
SELECT name FROM sys.check_constraints WHERE name IN ('CK_ImportLogs_Lease', 'CK_ImportLogs_Counters');
```

No job should be `Processing` at this point. A `Pending` job queued before `AddImportJobInputs` has no stored input, so it will become `Failed` with an explanation. Submit a new import for those.

### 6. Start the new version

Start one instance, and check:

- The log shows `Customer import worker started`.
- `GET /api/import/jobs/0` returns `404` as ProblemDetails, which shows routing and the database both work.
- A small test import through `POST /api/import/customers` reaches `Completed`.

Then start the remaining instances.

## Later deployments

Once every instance runs this version, rolling and blue-green deployments need no special steps. Graceful shutdown releases the running job, and any instance can continue it. Give instances enough shutdown time to stop cleanly: if one is killed instead, its job waits for its lease to expire.

To keep an instance from processing imports, set `CustomerImportWorker:Enabled` to `false`. It still accepts jobs and reports status.

## Rollback

Decide using this table. Reverting a migration is not the same as restoring: nothing undoes customer changes that imports have already committed.

| Situation | Action |
| --- | --- |
| The new version has a problem, and the schema is fine | Deploy a fixed version. This is the preferred route |
| Must return to the previous version, and no imports ran since migrating | Revert the migrations (below), or restore the backup |
| Must return to the previous version, and imports ran | Restore the backup. Customers and orders created since are lost, so export them first if needed |

### Reverting migrations

Stop every instance first, because old and new versions must not overlap in either direction. Then, for example, to return to the version before leasing:

```bash
dotnet ef database update AddImportCountersAndRetryTracking --project Syspro.Api --connection "<connection string>"
```

| Reverting | Consequence |
| --- | --- |
| `AddImportJobLeasing` | Removes lease columns. Jobs keep their checkpoints |
| `AddImportCountersAndRetryTracking` | Removes the constraint, the unique index and `ConsecutiveFailureCount` |
| `AddImportJobInputs` | **Drops all stored inputs.** Jobs that have not finished can no longer run from their own input |
| `AddImportJobTracking` | Removes job status. Unfinished jobs remain as incomplete log rows |

Application code that expects a newer schema will fail against an older one, so deploy the matching version.

## Recovery and troubleshooting

See [Troubleshooting](../README.md#operational-troubleshooting) for symptoms and causes. To check the state of all unfinished jobs:

```sql
SELECT Id, Status, ProcessedCount, AttemptCount, ConsecutiveFailureCount, FencingToken,
       LeaseOwner, LeaseExpiresAt, SYSUTCDATETIME() AS DatabaseTimeUtc
FROM ImportLogs
WHERE Status IN ('Pending', 'Processing')
ORDER BY CreatedAt, Id;
```

### A takeover is blocked by a frozen worker

A lease expiring does not guarantee a takeover. If a worker froze during a batch, for example a suspended process or VM, it holds the job's row in an open transaction and every claim waits behind it. The data is safe, but the job makes no progress until the lock is released. Other workers log `worker cycle failed` with a timeout about every 30 seconds.

To find the session holding the lock, run this while connected to the application database:

```sql
SELECT r.session_id AS WaitingSession, r.blocking_session_id AS HoldingSession,
       r.wait_type, r.wait_time AS WaitMilliseconds,
       holder.host_name AS HolderHost, holder.host_process_id AS HolderProcessId,
       holder.open_transaction_count AS HolderOpenTransactions
FROM sys.dm_exec_requests r
JOIN sys.dm_exec_sessions holder ON holder.session_id = r.blocking_session_id
WHERE r.database_id = DB_ID() AND r.blocking_session_id <> 0 AND r.wait_type LIKE 'LCK%';
```

This needs the `VIEW SERVER STATE` permission. Confirm that the holder is a frozen API instance, then do one of these:

1. Stop that process, which is the preferred fix. Its connection closes and SQL Server rolls back the batch.
2. If you cannot reach it, end the session with `KILL <HoldingSession>`, which needs the `ALTER ANY CONNECTION` permission. If the process wakes later, it cannot change the job.

Another worker then resumes from the last checkpoint, normally within a poll interval. Rolled-back work is repeated, and nothing is applied twice. A process that resumes by itself needs no action.

### Other recovery notes

Do not edit job rows by hand while any worker is running. To retry a `Failed` job, submit a new import: it runs from the start, and customers are matched by `LegacyCustomerId`, so earlier batches are updated and not duplicated.
