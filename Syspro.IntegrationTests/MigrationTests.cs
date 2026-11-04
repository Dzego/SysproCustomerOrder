using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Net.Http.Json;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.Api.Validators;
using Syspro.IntegrationTests.Infrastructure;

namespace Syspro.IntegrationTests;

public sealed class MigrationTests(SqlServerFixture fixture) : SqlServerTestBase(fixture)
{
    private sealed record ColumnDefinition(
        string DataType,
        int? MaxLength,
        int? Precision,
        int? Scale);

    private async Task<ColumnDefinition> ReadColumnAsync(string table, string column)
    {
        await using var dbContext = CreateDbContext();

        var definitions = await dbContext.Database
            .SqlQuery<ColumnRow>(
                $"""
                 SELECT
                     DATA_TYPE AS DataType,
                     CHARACTER_MAXIMUM_LENGTH AS MaxLength,
                     CAST(NUMERIC_PRECISION AS int) AS NumericPrecision,
                     NUMERIC_SCALE AS NumericScale
                 FROM INFORMATION_SCHEMA.COLUMNS
                 WHERE TABLE_NAME = {table} AND COLUMN_NAME = {column}
                 """)
            .ToListAsync();

        var row = Assert.Single(definitions);

        return new ColumnDefinition(row.DataType, row.MaxLength, row.NumericPrecision, row.NumericScale);
    }

    private sealed class ColumnRow
    {
        public required string DataType { get; init; }

        public int? MaxLength { get; init; }

        public int? NumericPrecision { get; init; }

        public int? NumericScale { get; init; }
    }

    [SqlServerFact]
    public async Task Migrate_AppliesEveryMigrationToAnEmptyDatabase()
    {
        await using var dbContext = CreateDbContext();

        var defined = dbContext.Database.GetMigrations().ToList();
        var applied = await dbContext.Database.GetAppliedMigrationsAsync();
        var pending = await dbContext.Database.GetPendingMigrationsAsync();

        Assert.NotEmpty(defined);
        Assert.Equal(defined, applied);
        Assert.Empty(pending);
    }

    [SqlServerFact]
    public async Task Migrations_MatchTheCurrentModel()
    {
        await using var dbContext = CreateDbContext();

        Assert.False(
            dbContext.Database.HasPendingModelChanges(),
            "AppDbContext has changes with no migration. Run 'dotnet ef migrations add'.");
    }

    // ValidatorDatabaseConstraintTests compares the validators with the EF model.
    // This compares them with the columns the migrations actually created.
    [SqlServerFact]
    public async Task MigratedColumns_MatchTheValidatorLimits()
    {
        Assert.Equal(
            new ColumnDefinition("varchar", CreateOrderRequestValidator.LegacyCustomerIdMaxLength, null, null),
            await ReadColumnAsync("Customers", "LegacyCustomerId"));

        Assert.Equal(
            new ColumnDefinition("nvarchar", CreateOrderRequestValidator.StatusMaxLength, null, null),
            await ReadColumnAsync("Orders", "Status"));

        Assert.Equal(
            new ColumnDefinition("nvarchar", CreateOrderItemRequestValidator.SkuMaxLength, null, null),
            await ReadColumnAsync("OrderItems", "Sku"));

        Assert.Equal(
            new ColumnDefinition("nvarchar", CreateOrderItemRequestValidator.DescriptionMaxLength, null, null),
            await ReadColumnAsync("OrderItems", "Description"));

        Assert.Equal(
            new ColumnDefinition(
                "decimal",
                null,
                CreateOrderItemRequestValidator.UnitPricePrecision,
                CreateOrderItemRequestValidator.UnitPriceScale),
            await ReadColumnAsync("OrderItems", "UnitPrice"));
    }

    // ---------- Job tracking migration ----------

    private const string InitialCreateMigration = "20260816212823_InitialCreate";

    private async Task MigrateToAsync(string? targetMigration)
    {
        await using var dbContext = CreateDbContext();

        await dbContext.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private async Task ExecuteSqlAsync(FormattableString sql)
    {
        await using var dbContext = CreateDbContext();

        await dbContext.Database.ExecuteSqlAsync(sql);
    }

    [SqlServerFact]
    public async Task JobTrackingMigration_ClassifiesExistingImportLogs()
    {
        await MigrateToAsync(InitialCreateMigration);

        var started = new DateTime(2026, 1, 1, 10, 0, 0);
        var completed = started.AddMinutes(1);

        await ExecuteSqlAsync(
            $"""
             INSERT INTO [ImportLogs]
                 ([StartedAt], [CompletedAt], [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount])
             VALUES ({started}, {completed}, 7, 5, 0, 2);
             """);

        await ExecuteSqlAsync(
            $"""
             INSERT INTO [ImportLogs]
                 ([StartedAt], [CompletedAt], [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount])
             VALUES ({started}, NULL, 0, 0, 0, 0);
             """);

        await MigrateToAsync(null);

        var logs = await QueryAsync(context => context.ImportLogs.OrderBy(x => x.Id).ToListAsync());

        Assert.Equal(ImportJobStatus.Completed, logs[0].Status);
        Assert.Equal(started, logs[0].CreatedAt);
        Assert.Equal(completed, logs[0].CompletedAt);
        Assert.Equal(1, logs[0].AttemptCount);
        Assert.Equal(5, logs[0].CreatedCount);
        Assert.Null(logs[0].FailureReason);

        Assert.Equal(ImportJobStatus.Failed, logs[1].Status);
        Assert.Equal(started, logs[1].CompletedAt);
        Assert.NotNull(logs[1].FailureReason);
    }

    // Everything a database from before job tracking would hold, inserted using only
    // the columns that existed then.
    private async Task SeedHistoricalDataAsync()
    {
        var started = new DateTime(2026, 1, 1, 10, 0, 0);
        var completed = started.AddMinutes(1);
        var signup = new DateTime(2021, 3, 15);

        await ExecuteSqlAsync(
            $"""
             INSERT INTO [ImportLogs]
                 ([StartedAt], [CompletedAt], [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount])
             VALUES ({started}, {completed}, 7, 5, 0, 2);
             """);

        await ExecuteSqlAsync(
            $"""
             INSERT INTO [ImportErrors] ([ImportLogId], [LineNumber], [RawData], [Reason])
             SELECT [Id], 6, 'raw row', 'Invalid signup date: 20241340' FROM [ImportLogs];
             """);

        await ExecuteSqlAsync(
            $"""
             INSERT INTO [Customers] ([LegacyCustomerId], [Name], [Email], [Tier], [SignupDate])
             VALUES ('0000012345', 'Historical Name', 'old@example.com', 'A', {signup});
             """);
    }

    private async Task AssertHistoricalDataIsIntactAsync()
    {
        var log = await QueryAsync(context => context.ImportLogs.Include(x => x.Errors).SingleAsync());

        Assert.Equal(ImportJobStatus.Completed, log.Status);
        Assert.Equal(7, log.ProcessedCount);
        Assert.Equal(5, log.CreatedCount);
        Assert.Equal(2, log.FailedCount);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0), log.StartedAt);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 1, 0), log.CompletedAt);

        var error = Assert.Single(log.Errors);

        Assert.Equal(6, error.LineNumber);
        Assert.Equal("raw row", error.RawData);
        Assert.Equal("Invalid signup date: 20241340", error.Reason);

        var customer = await QueryAsync(context => context.Customers.SingleAsync());

        Assert.Equal("Historical Name", customer.Name);
    }

    [SqlServerFact]
    public async Task JobTrackingMigration_PreservesHistoricalLogsErrorsAndCustomers()
    {
        await MigrateToAsync(InitialCreateMigration);
        await SeedHistoricalDataAsync();

        await MigrateToAsync(null);

        await AssertHistoricalDataIsIntactAsync();
    }

    [SqlServerFact]
    public async Task JobTrackingMigration_RoundTripsHistoricalData()
    {
        await MigrateToAsync(InitialCreateMigration);
        await SeedHistoricalDataAsync();

        await MigrateToAsync(null);
        await MigrateToAsync(InitialCreateMigration);
        await MigrateToAsync(null);

        await AssertHistoricalDataIsIntactAsync();
    }

    // The real API on an upgraded database: history is readable as finished jobs, the
    // worker never runs it again, and new imports work alongside it.
    [SqlServerFact]
    public async Task UpgradedDatabase_ServesHistoryAndRunsNewJobsWithoutReprocessingOldOnes()
    {
        await MigrateToAsync(InitialCreateMigration);
        await SeedHistoricalDataAsync();
        await MigrateToAsync(null);

        var historical = await QueryAsync(context => context.ImportLogs.SingleAsync());

        using var factory = new ApiFactory(ConnectionString, runImportWorker: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/import/customers", content: null);

        var job = await ImportJobClient.WaitForTerminalStatusAsync(client, response.Headers.Location!);

        Assert.Equal("Completed", job.Status);
        Assert.NotEqual(historical.Id, job.Id);

        // 0000012345 is in the sample file, so the historical customer is updated.
        Assert.Equal(4, job.Created);
        Assert.Equal(1, job.Updated);

        var served = await client.GetFromJsonAsync<ImportJobResponse>($"/api/import/jobs/{historical.Id}");

        Assert.NotNull(served);
        Assert.Equal("Completed", served.Status);
        Assert.Equal(7, served.Processed);
        Assert.Equal(5, served.Created);

        var unchanged = await QueryAsync(context => context.ImportLogs.SingleAsync(x => x.Id == historical.Id));

        Assert.Equal(1, unchanged.AttemptCount);
        Assert.Equal(historical.CompletedAt, unchanged.CompletedAt);
    }

    [SqlServerFact]
    public async Task JobTrackingMigration_RevertsWithJobsThatHaveNotStarted()
    {
        var createdAt = new DateTime(2026, 1, 1, 10, 0, 0);

        await AddPendingJobAsync(createdAt);

        await MigrateToAsync(InitialCreateMigration);

        await using var dbContext = CreateDbContext();

        var startedAt = await dbContext.Database
            .SqlQuery<DateTime>($"SELECT [StartedAt] AS [Value] FROM [ImportLogs]")
            .SingleAsync();

        Assert.Equal(createdAt, startedAt);
    }

    // A leftover default would let a row skip the job lifecycle without an error.
    [SqlServerFact]
    public async Task JobTrackingColumns_HaveNoDefaults()
    {
        await using var dbContext = CreateDbContext();

        var columnsWithDefaults = await dbContext.Database
            .SqlQuery<string>(
                $"""
                 SELECT COLUMN_NAME AS [Value]
                 FROM INFORMATION_SCHEMA.COLUMNS
                 WHERE TABLE_NAME = 'ImportLogs'
                   AND COLUMN_NAME IN ('Status', 'CreatedAt', 'AttemptCount')
                   AND COLUMN_DEFAULT IS NOT NULL
                 """)
            .ToListAsync();

        Assert.Empty(columnsWithDefaults);
    }

    // ---------- Input snapshot migration ----------

    private const string JobTrackingMigration = "20261103230810_AddImportJobTracking";

    [SqlServerFact]
    public async Task InputSnapshotMigration_CreatesTableForFullSizeBinaryContent()
    {
        Assert.Equal(
            new ColumnDefinition("varbinary", -1, null, null),
            await ReadColumnAsync("ImportJobInputs", "Content"));
    }

    [SqlServerFact]
    public async Task InputSnapshotMigration_FromInitialCreate_KeepsHistoryAndAddsNoInputs()
    {
        await MigrateToAsync(InitialCreateMigration);
        await SeedHistoricalDataAsync();

        await MigrateToAsync(null);

        await AssertHistoricalDataIsIntactAsync();
        Assert.Equal(0, await QueryAsync(context => context.ImportJobInputs.CountAsync()));
    }

    [SqlServerFact]
    public async Task InputSnapshotMigration_FromPreviousMigration_KeepsFinishedJobs()
    {
        await MigrateToAsync(JobTrackingMigration);

        var now = new DateTime(2026, 1, 1, 10, 0, 0);

        await ExecuteSqlAsync(
            $"""
             INSERT INTO [ImportLogs]
                 ([Status], [CreatedAt], [StartedAt], [CompletedAt], [AttemptCount],
                  [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount])
             VALUES ('Completed', {now}, {now}, {now}, 1, 7, 5, 0, 2);
             """);

        await MigrateToAsync(null);

        var job = await QueryAsync(context => context.ImportLogs.SingleAsync());

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(5, job.CreatedCount);
    }

    // A job queued before snapshots existed has no input, and the file on disk may
    // not be what was accepted. It must fail clearly rather than import that file.
    [SqlServerFact]
    public async Task UpgradedDatabase_QueuedJobWithoutInput_FailsWithoutReadingTheFile()
    {
        await MigrateToAsync(JobTrackingMigration);

        var now = new DateTime(2026, 1, 1, 10, 0, 0);

        await ExecuteSqlAsync(
            $"""
             INSERT INTO [ImportLogs]
                 ([Status], [CreatedAt], [AttemptCount],
                  [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount])
             VALUES ('Pending', {now}, 0, 0, 0, 0, 0);
             """);

        await ExecuteSqlAsync(
            $"""
             INSERT INTO [ImportLogs]
                 ([Status], [CreatedAt], [StartedAt], [AttemptCount],
                  [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount])
             VALUES ('Processing', {now}, {now}, 1, 0, 0, 0, 0);
             """);

        await MigrateToAsync(null);

        var ids = await QueryAsync(context => context.ImportLogs.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync());

        // The sample legacy file exists under this host's content root.
        using var factory = new ApiFactory(ConnectionString, runImportWorker: true);
        using var client = factory.CreateClient();

        foreach (var id in ids)
        {
            var job = await ImportJobClient.WaitForTerminalStatusAsync(
                client,
                new Uri($"/api/import/jobs/{id}", UriKind.Relative));

            Assert.Equal("Failed", job.Status);
            Assert.Equal(
                "The import job has no stored input, so it could not run. Submit a new import. No customers were changed.",
                job.FailureReason);
        }

        Assert.Equal(0, await QueryAsync(context => context.Customers.CountAsync()));
    }

    [SqlServerFact]
    public async Task InputSnapshotMigration_RevertsAndReappliesWithQueuedJobs()
    {
        await AddPendingJobAsync(input: [1, 2, 3]);

        await MigrateToAsync(JobTrackingMigration);

        Assert.Equal(1, await QueryAsync(context => context.ImportLogs.CountAsync()));

        await MigrateToAsync(null);

        // Reverting drops the snapshots, so the job is now one with no stored input.
        Assert.Equal(0, await QueryAsync(context => context.ImportJobInputs.CountAsync()));
        Assert.Equal(1, await QueryAsync(context => context.ImportLogs.CountAsync()));
    }

    // The import truncates to this constant, so it must match the real column.
    [SqlServerFact]
    public async Task MigratedRawDataColumn_MatchesTheTruncationLimit()
    {
        Assert.Equal(
            new ColumnDefinition("nvarchar", ImportError.RawDataMaxLength, null, null),
            await ReadColumnAsync("ImportErrors", "RawData"));
    }

    [SqlServerFact]
    public async Task MigratedColumns_UseTheExpectedTypesForFixedWidthAndDateValues()
    {
        Assert.Equal(
            new ColumnDefinition("char", 3, null, null),
            await ReadColumnAsync("Orders", "Currency"));

        Assert.Equal(
            new ColumnDefinition("char", 1, null, null),
            await ReadColumnAsync("Customers", "Tier"));

        Assert.Equal(
            "date",
            (await ReadColumnAsync("Customers", "SignupDate")).DataType);

        Assert.Equal(
            "datetime2",
            (await ReadColumnAsync("Orders", "OrderDate")).DataType);
    }
}
