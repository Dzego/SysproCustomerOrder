using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
