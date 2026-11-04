using Microsoft.EntityFrameworkCore;
using Syspro.Api.Data;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Syspro.IntegrationTests.Infrastructure;

namespace Syspro.IntegrationTests;

public sealed class CustomerImportTests(SqlServerFixture fixture) : SqlServerTestBase(fixture)
{
    private async Task<ImportResult> ImportAsync(
        params string[] lines)
    {
        var input = TestData.ToInput(lines);

        await AddPendingJobAsync(input: input);

        await using var dbContext = CreateDbContext();

        var job = await ClaimJobAsync(dbContext);

        return await CreateService(dbContext).ImportAsync(job, new MemoryStream(input), CancellationToken.None);
    }

    private static async Task<ImportLog> ClaimJobAsync(AppDbContext dbContext)
    {
        var job = await new ImportRepository(dbContext).ClaimNextPendingJobAsync(CancellationToken.None);

        Assert.NotNull(job);

        return job;
    }

    // Both repositories share one context, matching the scoped registration in
    // Program.cs. The first SaveChanges therefore writes all rows and the log.
    private static CustomerImportService CreateService(AppDbContext dbContext)
    {
        return new CustomerImportService(
            new LegacyCustomerParser(),
            new CustomerRepository(dbContext),
            new ImportRepository(dbContext));
    }

    // ---------- Valid rows ----------

    [SqlServerFact]
    public async Task Import_PersistsEveryParsedField()
    {
        var result = await ImportAsync(TestData.LegacyLine(
            legacyCustomerId: "0000012345",
            name: "John Smith",
            email: "john.smith@example.com",
            signupDate: "20210315",
            tier: "B"));

        Assert.Equal(1, result.Created);

        var customer = await QueryAsync(context => context.Customers.SingleAsync());

        Assert.Equal("0000012345", customer.LegacyCustomerId);
        Assert.Equal("John Smith", customer.Name);
        Assert.Equal("john.smith@example.com", customer.Email);
        Assert.Equal(new DateOnly(2021, 3, 15), customer.SignupDate);
        Assert.Equal("B", customer.Tier);
    }

    [SqlServerFact]
    public async Task Import_WritesCompletedLogWithCounts()
    {
        var startedAfter = DateTime.UtcNow.AddSeconds(-1);

        await ImportAsync(
            TestData.LegacyLine(legacyCustomerId: "0000000001"),
            TestData.LegacyLine(legacyCustomerId: "0000000002"));

        var log = await QueryAsync(context => context.ImportLogs.SingleAsync());

        Assert.Equal(ImportJobStatus.Completed, log.Status);
        Assert.Equal(2, log.ProcessedCount);
        Assert.Equal(2, log.CreatedCount);
        Assert.Equal(0, log.UpdatedCount);
        Assert.Equal(0, log.FailedCount);
        Assert.True(log.StartedAt >= startedAfter);
        Assert.NotNull(log.CompletedAt);
        Assert.True(log.CompletedAt >= log.StartedAt);
    }

    [SqlServerFact]
    public async Task Import_PreservesUnicodeNamesFromTheFile()
    {
        await ImportAsync(TestData.LegacyLine(name: "Ñandú Škoda 日本語"));

        var customer = await QueryAsync(context => context.Customers.SingleAsync());

        Assert.Equal("Ñandú Škoda 日本語", customer.Name);
    }

    // ---------- Re-importing ----------

    [SqlServerFact]
    public async Task Import_OfExistingCustomer_UpdatesThatRowAndWritesASecondLog()
    {
        await ImportAsync(TestData.LegacyLine(name: "Old Name", tier: "A"));

        var original = await QueryAsync(context => context.Customers.SingleAsync());

        var result = await ImportAsync(TestData.LegacyLine(name: "New Name", tier: "C"));

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);

        var customer = await QueryAsync(context => context.Customers.SingleAsync());

        Assert.Equal(original.Id, customer.Id);
        Assert.Equal("New Name", customer.Name);
        Assert.Equal("C", customer.Tier);
        Assert.Equal(2, await QueryAsync(context => context.ImportLogs.CountAsync()));
    }

    // ---------- Invalid rows ----------

    [SqlServerFact]
    public async Task Import_LogsInvalidRowsAndStillPersistsValidOnes()
    {
        var badDate = TestData.LegacyLine(legacyCustomerId: "0000000002", signupDate: "20241340");
        var badTier = TestData.LegacyLine(legacyCustomerId: "0000000004", tier: "Z");

        var result = await ImportAsync(
            TestData.LegacyLine(legacyCustomerId: "0000000001"),
            badDate,
            TestData.LegacyLine(legacyCustomerId: "0000000003"),
            badTier);

        Assert.Equal(4, result.Processed);
        Assert.Equal(2, result.Created);
        Assert.Equal(2, result.Failed);

        var customerIds = await QueryAsync(context =>
            context.Customers.OrderBy(x => x.LegacyCustomerId).Select(x => x.LegacyCustomerId).ToListAsync());

        Assert.Equal(new[] { "0000000001", "0000000003" }, customerIds);

        var log = await QueryAsync(context => context.ImportLogs.Include(x => x.Errors).SingleAsync());

        Assert.Equal(2, log.FailedCount);
        Assert.Collection(
            log.Errors.OrderBy(x => x.LineNumber),
            error =>
            {
                Assert.Equal(2, error.LineNumber);
                Assert.Equal(badDate, error.RawData);
                Assert.Equal("Invalid signup date: 20241340", error.Reason);
            },
            error =>
            {
                Assert.Equal(4, error.LineNumber);
                Assert.Equal(badTier, error.RawData);
                Assert.Equal("Invalid tier: Z", error.Reason);
            });
    }

    [SqlServerFact]
    public async Task Import_LogsBlankLinesAsFailures()
    {
        var result = await ImportAsync(string.Empty, TestData.LegacyLine());

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Created);

        var error = await QueryAsync(context => context.ImportErrors.SingleAsync());

        Assert.Equal(1, error.LineNumber);
        Assert.Equal(string.Empty, error.RawData);
    }

    // A malformed row must not stop the import, so an over-long row has to be
    // shortened to fit the RawData column, which SQL Server would otherwise reject.
    [SqlServerFact]
    public async Task Import_LogsOverLongRowInsteadOfFailingTheWholeImport()
    {
        var overLong = TestData.LegacyLine(legacyCustomerId: "0000000002") + "EXTRA";

        var result = await ImportAsync(
            TestData.LegacyLine(legacyCustomerId: "0000000001"),
            overLong);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Failed);

        var error = await QueryAsync(context => context.ImportErrors.SingleAsync());

        Assert.Equal(2, error.LineNumber);
        Assert.Equal(overLong[..80], error.RawData);
        Assert.Equal(
            "Invalid line length. Expected 80 characters but got 85. " +
            "RawData was truncated from 85 to 80 characters.",
            error.Reason);
    }

    // ---------- Duplicate legacy IDs ----------

    [SqlServerFact]
    public async Task Import_WithDuplicateIdInOneFile_KeepsFirstRowAndLogsTheRest()
    {
        var duplicate = TestData.LegacyLine(name: "Second", tier: "C");

        var result = await ImportAsync(
            TestData.LegacyLine(name: "First", tier: "B"),
            TestData.LegacyLine(legacyCustomerId: "0000000002", name: "Other"),
            duplicate);

        Assert.Equal(3, result.Processed);
        Assert.Equal(2, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Failed);

        var customer = await QueryAsync(context =>
            context.Customers.SingleAsync(x => x.LegacyCustomerId == "0000012345"));

        Assert.Equal("First", customer.Name);
        Assert.Equal("B", customer.Tier);
        Assert.Equal(2, await QueryAsync(context => context.Customers.CountAsync()));

        var log = await QueryAsync(context => context.ImportLogs.Include(x => x.Errors).SingleAsync());

        Assert.Equal(3, log.ProcessedCount);
        Assert.Equal(2, log.CreatedCount);
        Assert.Equal(0, log.UpdatedCount);
        Assert.Equal(1, log.FailedCount);

        var error = Assert.Single(log.Errors);

        Assert.Equal(3, error.LineNumber);
        Assert.Equal(duplicate, error.RawData);
        Assert.Equal("Duplicate LegacyCustomerId 0000012345. First seen on line 1.", error.Reason);
    }

    // The unique index ignores case, so these would otherwise fail the whole save.
    [SqlServerFact]
    public async Task Import_WithIdsDifferingOnlyByCase_LogsTheSecondAsDuplicate()
    {
        var result = await ImportAsync(
            TestData.LegacyLine(legacyCustomerId: "ABC123", name: "First"),
            TestData.LegacyLine(legacyCustomerId: "abc123", name: "Second"));

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Failed);

        var customer = await QueryAsync(context => context.Customers.SingleAsync());

        Assert.Equal("ABC123", customer.LegacyCustomerId);
        Assert.Equal("First", customer.Name);
    }

    [SqlServerFact]
    public async Task Import_WithDuplicateOfExistingCustomer_UpdatesOnceFromFirstRow()
    {
        var existing = await AddCustomerAsync("0000000009", name: "Existing");

        var result = await ImportAsync(
            TestData.LegacyLine(legacyCustomerId: "0000000009", name: "First"),
            TestData.LegacyLine(legacyCustomerId: "0000000009", name: "Second"));

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Failed);

        var customer = await QueryAsync(context => context.Customers.SingleAsync());

        Assert.Equal(existing.Id, customer.Id);
        Assert.Equal("First", customer.Name);
    }

    // ---------- Rollback ----------

    // Both repositories share one context, so one SaveChanges writes the customers
    // and the log. A failure must therefore undo all of it, including updates.
    [SqlServerFact]
    public async Task Import_WhenSaveFails_RollsBackEverything()
    {
        await AddCustomerAsync("0000000009", name: "Existing");

        var input = TestData.ToInput(
            TestData.LegacyLine(legacyCustomerId: "0000000009", name: "Changed"),
            TestData.LegacyLine(legacyCustomerId: "0000000001"),
            TestData.LegacyLine(legacyCustomerId: "0000000002", tier: "Z"));

        await AddPendingJobAsync(input: input);

        await using var dbContext = CreateDbContext(
            new ConcurrentImportInterceptor(ConnectionString, "0000000001"));

        var job = await ClaimJobAsync(dbContext);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => CreateService(dbContext).ImportAsync(job, new MemoryStream(input), CancellationToken.None));

        Assert.Equal(SqlUniqueIndexViolation, TestData.SqlErrorNumber(exception));

        var names = await QueryAsync(context =>
            context.Customers.OrderBy(x => x.LegacyCustomerId).Select(x => x.Name).ToListAsync());

        // Only the row committed by the concurrent import remains.
        Assert.Equal(new[] { "Concurrent Import", "Existing" }, names);
        Assert.Equal(0, await QueryAsync(context => context.ImportErrors.CountAsync()));

        // The job row was saved earlier, and none of this attempt's results were.
        var stored = await QueryAsync(context => context.ImportLogs.SingleAsync());

        Assert.Equal(ImportJobStatus.Processing, stored.Status);
        Assert.Equal(0, stored.ProcessedCount);
    }

    [SqlServerFact]
    public async Task Import_WhenCancelledBeforeSaving_PersistsNothing()
    {
        var input = TestData.ToInput(TestData.LegacyLine());

        using var cancellationSource = new CancellationTokenSource();

        await AddPendingJobAsync(input: input);

        await using var dbContext = CreateDbContext(new CancelOnSaveInterceptor(cancellationSource));

        var job = await ClaimJobAsync(dbContext);

        var action = () => CreateService(dbContext).ImportAsync(job, new MemoryStream(input), cancellationSource.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);

        Assert.Equal(0, await QueryAsync(context => context.Customers.CountAsync()));

        var stored = await QueryAsync(context => context.ImportLogs.SingleAsync());

        Assert.Equal(ImportJobStatus.Processing, stored.Status);
    }
}
