using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Syspro.Api.Models;
using Syspro.IntegrationTests.Infrastructure;

namespace Syspro.IntegrationTests;

public sealed class ImportConstraintTests(SqlServerFixture fixture) : SqlServerTestBase(fixture)
{
    private const string InitialCreate = "20260816212823_InitialCreate";
    private const string InputSnapshots = "20261104004137_AddImportJobInputs";
    private const string CountersAndRetryTracking = "20261104185804_AddImportCountersAndRetryTracking";

    private const int SqlUniqueIndexViolation2601 = 2601;
    private const int MigrationValidationError = 50000;

    private async Task MigrateToAsync(string? target)
    {
        await using var dbContext = CreateDbContext();

        await dbContext.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task ExecuteAsync(FormattableString sql)
    {
        await using var dbContext = CreateDbContext();

        await dbContext.Database.ExecuteSqlAsync(sql);
    }

    private Task<List<string>> AppliedMigrationsAsync()
    {
        return QueryAsync(async context =>
            (await context.Database.GetAppliedMigrationsAsync()).ToList());
    }

    private async Task<bool> ColumnExistsAsync()
    {
        await using var dbContext = CreateDbContext();

        var count = await dbContext.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'ImportLogs' AND COLUMN_NAME = 'ConsecutiveFailureCount'")
            .SingleAsync();

        return count == 1;
    }

    private async Task<List<string>> IndexNamesAsync()
    {
        await using var dbContext = CreateDbContext();

        return await dbContext.Database
            .SqlQuery<string>($"SELECT [name] AS [Value] FROM sys.indexes WHERE [object_id] = OBJECT_ID('ImportErrors') AND [name] LIKE 'IX_%'")
            .ToListAsync();
    }

    private async Task<int> CountConstraintAsync()
    {
        await using var dbContext = CreateDbContext();

        return await dbContext.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE [name] = 'CK_ImportLogs_Counters'")
            .SingleAsync();
    }

    // Uses only the columns that exist before this migration.
    private Task InsertLogBeforeMigrationAsync(int processed, int created, int updated, int failed)
    {
        var at = new DateTime(2026, 1, 1, 10, 0, 0);

        return ExecuteAsync(
            $"""
             INSERT INTO [ImportLogs]
                 ([Status], [CreatedAt], [StartedAt], [CompletedAt], [AttemptCount],
                  [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount])
             VALUES ('Completed', {at}, {at}, {at}, 3, {processed}, {created}, {updated}, {failed});
             """);
    }

    private Task InsertErrorAsync(int logId, int lineNumber, string reason = "Invalid tier: Z")
    {
        return ExecuteAsync(
            $"""
             INSERT INTO [ImportErrors] ([ImportLogId], [LineNumber], [RawData], [Reason])
             VALUES ({logId}, {lineNumber}, 'raw row', {reason});
             """);
    }

    private Task<int> FirstLogIdAsync()
    {
        return QueryAsync(context => context.ImportLogs.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync());
    }

    private async Task<SqlException> AssertMigrationStopsAndChangesNothingAsync()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() => MigrateToAsync(null));

        Assert.Equal(MigrationValidationError, exception.Number);
        Assert.Contains("nothing was changed", exception.Message);

        Assert.DoesNotContain(CountersAndRetryTracking, await AppliedMigrationsAsync());
        Assert.False(await ColumnExistsAsync());
        Assert.Equal(0, await CountConstraintAsync());
        Assert.Equal(new[] { "IX_ImportErrors_ImportLogId" }, await IndexNamesAsync());

        return exception;
    }

    // ---------- Fresh database ----------

    [SqlServerFact]
    public async Task FreshDatabase_HasEveryMigrationAndTheNewSchema()
    {
        await using var dbContext = CreateDbContext();

        Assert.Equal(dbContext.Database.GetMigrations(), await AppliedMigrationsAsync());
        Assert.Equal(1, await CountConstraintAsync());
        Assert.True(await ColumnExistsAsync());
        Assert.Equal(new[] { "IX_ImportErrors_ImportLogId_LineNumber" }, await IndexNamesAsync());

        var columns = await dbContext.Database
            .SqlQuery<string>(
                $"""
                 SELECT CONCAT(c.[name], ':', ic.[key_ordinal], ':', i.[is_unique], ':', i.[has_filter]) AS [Value]
                 FROM sys.indexes i
                 JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                 JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                 WHERE i.[name] = 'IX_ImportErrors_ImportLogId_LineNumber'
                 ORDER BY ic.[key_ordinal]
                 """)
            .ToListAsync();

        Assert.Equal(new[] { "ImportLogId:1:1:0", "LineNumber:2:1:0" }, columns);
    }

    [SqlServerFact]
    public async Task ConsecutiveFailureCount_IsRequiredWithADefaultOfZero()
    {
        await using var dbContext = CreateDbContext();

        var definition = await dbContext.Database
            .SqlQuery<string>(
                $"""
                 SELECT CONCAT(DATA_TYPE, ':', IS_NULLABLE, ':', COLUMN_DEFAULT) AS [Value]
                 FROM INFORMATION_SCHEMA.COLUMNS
                 WHERE TABLE_NAME = 'ImportLogs' AND COLUMN_NAME = 'ConsecutiveFailureCount'
                 """)
            .SingleAsync();

        Assert.Equal("int:NO:((0))", definition);
    }

    // ---------- Existing data ----------

    [SqlServerFact]
    public async Task Migration_PreservesHistoryAndGivesExistingLogsZeroFailures()
    {
        await MigrateToAsync(InputSnapshots);

        await InsertLogBeforeMigrationAsync(processed: 8, created: 5, updated: 0, failed: 3);

        var logId = await FirstLogIdAsync();

        await InsertErrorAsync(logId, 6);
        await InsertErrorAsync(logId, 7);
        await InsertErrorAsync(logId, 8);

        await MigrateToAsync(null);

        var log = await QueryAsync(context => context.ImportLogs.Include(x => x.Errors).SingleAsync());

        Assert.Equal(0, log.ConsecutiveFailureCount);

        // AttemptCount is history, so it must not be disturbed.
        Assert.Equal(3, log.AttemptCount);
        Assert.Equal(ImportJobStatus.Completed, log.Status);
        Assert.Equal(8, log.ProcessedCount);
        Assert.Equal(5, log.CreatedCount);
        Assert.Equal(3, log.FailedCount);
        Assert.Equal(new[] { 6, 7, 8 }, log.Errors.Select(x => x.LineNumber).Order());
    }

    // The path a database from before job tracking takes through every migration.
    [SqlServerFact]
    public async Task Migration_FromInitialCreateWithHistory_Succeeds()
    {
        await MigrateToAsync(InitialCreate);

        var started = new DateTime(2026, 1, 1, 10, 0, 0);

        await ExecuteAsync(
            $"""
             INSERT INTO [ImportLogs]
                 ([StartedAt], [CompletedAt], [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount])
             VALUES ({started}, {started}, 8, 0, 5, 3);
             """);

        await InsertErrorAsync(await FirstLogIdAsync(), 6);

        await MigrateToAsync(null);

        var log = await QueryAsync(context => context.ImportLogs.Include(x => x.Errors).SingleAsync());

        Assert.Equal(ImportJobStatus.Completed, log.Status);
        Assert.Equal(1, log.AttemptCount);
        Assert.Equal(0, log.ConsecutiveFailureCount);
        Assert.Equal(5, log.UpdatedCount);
        Assert.Single(log.Errors);
    }

    // ---------- Invalid history ----------

    [SqlServerFact]
    public async Task Migration_WithInconsistentCounters_StopsNamingTheRows()
    {
        await MigrateToAsync(InputSnapshots);

        await InsertLogBeforeMigrationAsync(processed: 8, created: 5, updated: 0, failed: 3);
        await InsertLogBeforeMigrationAsync(processed: 9, created: 5, updated: 0, failed: 3);

        var badId = await QueryAsync(context =>
            context.ImportLogs.Where(x => x.ProcessedCount == 9).Select(x => x.Id).SingleAsync());

        var exception = await AssertMigrationStopsAndChangesNothingAsync();

        Assert.Contains("CreatedCount + UpdatedCount + FailedCount: 1", exception.Message);
        Assert.Contains($"first Ids: {badId})", exception.Message);

        // The data was left exactly as it was.
        var processed = await QueryAsync(context =>
            context.ImportLogs.OrderBy(x => x.Id).Select(x => x.ProcessedCount).ToListAsync());

        Assert.Equal(new[] { 8, 9 }, processed);
    }

    [SqlServerFact]
    public async Task Migration_WithDuplicateRowErrors_StopsNamingTheRows()
    {
        await MigrateToAsync(InputSnapshots);

        await InsertLogBeforeMigrationAsync(processed: 8, created: 5, updated: 0, failed: 3);

        var logId = await FirstLogIdAsync();

        await InsertErrorAsync(logId, 6);
        await InsertErrorAsync(logId, 6);

        var exception = await AssertMigrationStopsAndChangesNothingAsync();

        Assert.Contains("per ImportLogId and LineNumber: 1", exception.Message);
        Assert.Contains($"log {logId} line 6", exception.Message);
        Assert.Equal(2, await QueryAsync(context => context.ImportErrors.CountAsync()));
    }

    [SqlServerFact]
    public async Task Migration_WithBothKindsOfViolation_ReportsBoth()
    {
        await MigrateToAsync(InputSnapshots);

        await InsertLogBeforeMigrationAsync(processed: 9, created: 5, updated: 0, failed: 3);

        var logId = await FirstLogIdAsync();

        await InsertErrorAsync(logId, 6);
        await InsertErrorAsync(logId, 6);

        var exception = await AssertMigrationStopsAndChangesNothingAsync();

        Assert.Contains("CreatedCount + UpdatedCount + FailedCount: 1", exception.Message);
        Assert.Contains("per ImportLogId and LineNumber: 1", exception.Message);
    }

    // Exceptionally long samples must not hide the problem.
    [SqlServerFact]
    public async Task Migration_WithManyViolations_ReportsTheFullCountAndASample()
    {
        await MigrateToAsync(InputSnapshots);

        for (var i = 0; i < 7; i++)
        {
            await InsertLogBeforeMigrationAsync(processed: 1, created: 0, updated: 0, failed: 0);
        }

        var exception = await AssertMigrationStopsAndChangesNothingAsync();

        Assert.Contains("FailedCount: 7", exception.Message);
    }

    [SqlServerFact]
    public async Task Migration_AfterTheViolationsAreCorrected_Succeeds()
    {
        await MigrateToAsync(InputSnapshots);

        await InsertLogBeforeMigrationAsync(processed: 9, created: 5, updated: 0, failed: 3);

        await AssertMigrationStopsAndChangesNothingAsync();

        await ExecuteAsync($"UPDATE [ImportLogs] SET [ProcessedCount] = 8");

        await MigrateToAsync(null);

        Assert.Contains(CountersAndRetryTracking, await AppliedMigrationsAsync());
        Assert.Equal(1, await CountConstraintAsync());
    }

    // ---------- Reverting ----------

    [SqlServerFact]
    public async Task Migration_RevertsAndReappliesKeepingHistory()
    {
        await MigrateToAsync(InputSnapshots);

        await InsertLogBeforeMigrationAsync(processed: 8, created: 5, updated: 0, failed: 3);
        await InsertErrorAsync(await FirstLogIdAsync(), 6);

        await MigrateToAsync(null);
        await MigrateToAsync(InputSnapshots);

        Assert.False(await ColumnExistsAsync());
        Assert.Equal(0, await CountConstraintAsync());
        Assert.Equal(new[] { "IX_ImportErrors_ImportLogId" }, await IndexNamesAsync());

        await MigrateToAsync(null);

        var log = await QueryAsync(context => context.ImportLogs.Include(x => x.Errors).SingleAsync());

        Assert.Equal(5, log.CreatedCount);
        Assert.Single(log.Errors);
    }

    // ---------- Counters constraint ----------

    [SqlServerFact]
    public async Task CountersConstraint_RejectsUpdateThatBreaksTheTotal()
    {
        await AddPendingJobAsync();

        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync($"UPDATE [ImportLogs] SET [ProcessedCount] = 1"));

        Assert.Equal(SqlConstraintViolation, exception.Number);
        Assert.Contains("CK_ImportLogs_Counters", exception.Message);
    }

    [SqlServerTheory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public async Task CountersConstraint_RejectsCounterChangedWithoutProcessedCount(
        int created,
        int updated,
        int failed)
    {
        await AddPendingJobAsync();

        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync(
                $"UPDATE [ImportLogs] SET [CreatedCount] = {created}, [UpdatedCount] = {updated}, [FailedCount] = {failed}"));

        Assert.Equal(SqlConstraintViolation, exception.Number);
    }

    [SqlServerFact]
    public async Task CountersConstraint_AllowsCountersThatAddUp()
    {
        await AddPendingJobAsync();

        await ExecuteAsync(
            $"UPDATE [ImportLogs] SET [ProcessedCount] = 6, [CreatedCount] = 3, [UpdatedCount] = 2, [FailedCount] = 1");

        var job = await QueryAsync(context => context.ImportLogs.SingleAsync());

        Assert.Equal(6, job.ProcessedCount);
    }

    // ---------- Unique row errors ----------

    [SqlServerFact]
    public async Task ImportErrors_RejectsSecondErrorForTheSameLine()
    {
        var job = await AddPendingJobAsync();

        await InsertErrorAsync(job.Id, 5);

        var exception = await Assert.ThrowsAsync<SqlException>(
            () => InsertErrorAsync(job.Id, 5, "Different reason"));

        Assert.Equal(SqlUniqueIndexViolation2601, exception.Number);
        Assert.Equal(1, await QueryAsync(context => context.ImportErrors.CountAsync()));
    }

    [SqlServerFact]
    public async Task ImportErrors_AllowsDifferentLinesAndTheSameLineInAnotherJob()
    {
        var first = await AddPendingJobAsync();
        var second = await AddPendingJobAsync();

        await InsertErrorAsync(first.Id, 5);
        await InsertErrorAsync(first.Id, 6);
        await InsertErrorAsync(second.Id, 5);

        Assert.Equal(3, await QueryAsync(context => context.ImportErrors.CountAsync()));
    }

    // ---------- ConsecutiveFailureCount ----------

    // How a row is inserted by an instance still running the previous code.
    [SqlServerFact]
    public async Task InsertWithoutTheNewColumn_DefaultsToZero()
    {
        var at = new DateTime(2026, 1, 1);

        await ExecuteAsync(
            $"""
             INSERT INTO [ImportLogs]
                 ([Status], [CreatedAt], [AttemptCount], [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount])
             VALUES ('Pending', {at}, 0, 0, 0, 0, 0);
             """);

        var job = await QueryAsync(context => context.ImportLogs.SingleAsync());

        Assert.Equal(0, job.ConsecutiveFailureCount);
    }

    [SqlServerFact]
    public async Task ConsecutiveFailureCount_IsStoredIndependentlyOfAttemptCount()
    {
        await using (var dbContext = CreateDbContext())
        {
            dbContext.ImportLogs.Add(new ImportLog
            {
                Status = ImportJobStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                AttemptCount = 7,
                ConsecutiveFailureCount = 2
            });

            await dbContext.SaveChangesAsync();
        }

        var job = await QueryAsync(context => context.ImportLogs.SingleAsync());

        Assert.Equal(7, job.AttemptCount);
        Assert.Equal(2, job.ConsecutiveFailureCount);
    }

    // Nothing updates the column until Stage 3, so a retried job must leave it alone.
    [SqlServerFact]
    public async Task ClaimingAJob_DoesNotChangeConsecutiveFailureCount()
    {
        await AddPendingJobAsync();

        await using (var dbContext = CreateDbContext())
        {
            await new Syspro.Api.Repositories.ImportRepository(dbContext)
                .ClaimNextPendingJobAsync(CancellationToken.None);
        }

        var job = await QueryAsync(context => context.ImportLogs.SingleAsync());

        Assert.Equal(1, job.AttemptCount);
        Assert.Equal(0, job.ConsecutiveFailureCount);
    }
}
