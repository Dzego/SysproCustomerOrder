using Microsoft.EntityFrameworkCore;
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
