using Microsoft.EntityFrameworkCore;
using Syspro.Api.Data;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Xunit;

namespace Syspro.Tests.Services;

// Covers the workflow rules. Database constraints do not exist under InMemory, so
// they are verified against SQL Server in Syspro.IntegrationTests.
public sealed class CustomerImportServiceTests : IDisposable
{
    private readonly string _filePath = Path.GetTempFileName();
    private readonly AppDbContext _dbContext;
    private readonly CustomerImportService _service;

    public CustomerImportServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);

        _service = new CustomerImportService(
            new LegacyCustomerParser(),
            new CustomerRepository(_dbContext),
            new ImportRepository(_dbContext));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        File.Delete(_filePath);
    }

    private static string BuildLine(
        string legacyCustomerId = "0000012345",
        string name = "John Smith",
        string signupDate = "20210315",
        string tier = "A")
    {
        return legacyCustomerId.PadRight(10) +
               name.PadRight(30) +
               "john.smith@example.com".PadRight(30) +
               signupDate.PadRight(8) +
               tier.PadRight(2);
    }

    private async Task<ImportLog> AddClaimedJobAsync()
    {
        var now = DateTime.UtcNow;

        var job = new ImportLog
        {
            Status = ImportJobStatus.Processing,
            CreatedAt = now,
            StartedAt = now,
            AttemptCount = 1
        };

        _dbContext.ImportLogs.Add(job);
        await _dbContext.SaveChangesAsync();

        return job;
    }

    private async Task<Syspro.Api.DTOs.ImportResult> ImportAsync(params string[] lines)
    {
        await File.WriteAllLinesAsync(_filePath, lines);

        return await _service.ImportAsync(await AddClaimedJobAsync(), _filePath, CancellationToken.None);
    }

    [Fact]
    public async Task ImportAsync_WithNewCustomer_CreatesItFromTheParsedFields()
    {
        var result = await ImportAsync(BuildLine(tier: "B"));

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Failed);

        var customer = await _dbContext.Customers.SingleAsync();

        Assert.Equal("0000012345", customer.LegacyCustomerId);
        Assert.Equal("John Smith", customer.Name);
        Assert.Equal("john.smith@example.com", customer.Email);
        Assert.Equal(new DateOnly(2021, 3, 15), customer.SignupDate);
        Assert.Equal("B", customer.Tier);
    }

    [Fact]
    public async Task ImportAsync_WithExistingCustomer_UpdatesItInPlace()
    {
        await ImportAsync(BuildLine(name: "Old Name", signupDate: "20200101", tier: "A"));

        var originalId = (await _dbContext.Customers.SingleAsync()).Id;

        var result = await ImportAsync(BuildLine(name: "New Name", signupDate: "20220202", tier: "C"));

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);

        var customer = await _dbContext.Customers.SingleAsync();

        Assert.Equal(originalId, customer.Id);
        Assert.Equal("New Name", customer.Name);
        Assert.Equal(new DateOnly(2022, 2, 2), customer.SignupDate);
        Assert.Equal("C", customer.Tier);
    }

    [Fact]
    public async Task ImportAsync_WithInvalidRow_RecordsItAndContinuesWithLaterRows()
    {
        var invalid = BuildLine(legacyCustomerId: "0000000002", tier: "Z");

        var result = await ImportAsync(
            BuildLine(legacyCustomerId: "0000000001"),
            invalid,
            BuildLine(legacyCustomerId: "0000000003"));

        Assert.Equal(3, result.Processed);
        Assert.Equal(2, result.Created);
        Assert.Equal(1, result.Failed);

        var error = await _dbContext.ImportErrors.SingleAsync();

        Assert.Equal(2, error.LineNumber);
        Assert.Equal(invalid, error.RawData);
        Assert.Equal("Invalid tier: Z", error.Reason);
    }

    [Fact]
    public async Task ImportAsync_DoesNotChangeExistingCustomerFromAnInvalidRow()
    {
        await ImportAsync(BuildLine(name: "Original"));

        var result = await ImportAsync(BuildLine(name: "Changed", tier: "Z"));

        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Failed);
        Assert.Equal("Original", (await _dbContext.Customers.SingleAsync()).Name);
    }

    [Fact]
    public async Task ImportAsync_WritesLogMatchingTheResult()
    {
        var before = DateTime.UtcNow;

        var result = await ImportAsync(BuildLine(), BuildLine(legacyCustomerId: "0000000002", tier: "Z"));

        var log = await _dbContext.ImportLogs.SingleAsync();

        Assert.Equal(result.Processed, log.ProcessedCount);
        Assert.Equal(result.Created, log.CreatedCount);
        Assert.Equal(result.Updated, log.UpdatedCount);
        Assert.Equal(result.Failed, log.FailedCount);
        Assert.Equal(ImportJobStatus.Completed, log.Status);
        Assert.InRange(log.StartedAt!.Value, before, DateTime.UtcNow);
        Assert.NotNull(log.CompletedAt);
        Assert.True(log.CompletedAt >= log.StartedAt);
    }

    [Fact]
    public async Task ImportAsync_WithEmptyFile_WritesACompletedLogWithZeroCounts()
    {
        var result = await ImportAsync();

        Assert.Equal(0, result.Processed);

        var log = await _dbContext.ImportLogs.SingleAsync();

        Assert.Equal(0, log.ProcessedCount);
        Assert.NotNull(log.CompletedAt);
        Assert.Equal(0, await _dbContext.Customers.CountAsync());
    }

    [Fact]
    public async Task ImportAsync_WhenFileIsMissing_ThrowsAndLeavesJobUnfinished()
    {
        var job = await AddClaimedJobAsync();

        var action = () => _service.ImportAsync(
            job,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
            CancellationToken.None);

        await Assert.ThrowsAsync<FileNotFoundException>(action);
        Assert.Equal(ImportJobStatus.Processing, job.Status);
        Assert.Null(job.CompletedAt);
        Assert.Equal(0, await _dbContext.Customers.CountAsync());
    }

    // ---------- Duplicate legacy IDs ----------

    private static void AssertCountsAddUp(Syspro.Api.DTOs.ImportResult result)
    {
        Assert.Equal(result.Processed, result.Created + result.Updated + result.Failed);
    }

    [Fact]
    public async Task ImportAsync_WithDuplicateId_KeepsFirstRowAndLogsLaterOnes()
    {
        var second = BuildLine(name: "Second");
        var third = BuildLine(name: "Third");

        var result = await ImportAsync(BuildLine(name: "First"), second, third);

        Assert.Equal(3, result.Processed);
        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(2, result.Failed);
        AssertCountsAddUp(result);

        Assert.Equal("First", (await _dbContext.Customers.SingleAsync()).Name);

        Assert.Collection(
            await _dbContext.ImportErrors.OrderBy(x => x.LineNumber).ToListAsync(),
            error =>
            {
                Assert.Equal(2, error.LineNumber);
                Assert.Equal(second, error.RawData);
                Assert.Equal("Duplicate LegacyCustomerId 0000012345. First seen on line 1.", error.Reason);
            },
            error =>
            {
                Assert.Equal(3, error.LineNumber);
                Assert.Equal(third, error.RawData);
            });
    }

    [Fact]
    public async Task ImportAsync_WithIdsDifferingOnlyByCase_TreatsThemAsDuplicates()
    {
        var result = await ImportAsync(
            BuildLine(legacyCustomerId: "ABC123", name: "First"),
            BuildLine(legacyCustomerId: "abc123", name: "Second"));

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Failed);
        Assert.Equal("First", (await _dbContext.Customers.SingleAsync()).Name);
    }

    [Fact]
    public async Task ImportAsync_WhenFirstOccurrenceIsInvalid_ProcessesTheFirstValidOne()
    {
        var result = await ImportAsync(
            BuildLine(name: "Invalid", tier: "Z"),
            BuildLine(name: "First Valid"),
            BuildLine(name: "Duplicate"));

        Assert.Equal(1, result.Created);
        Assert.Equal(2, result.Failed);
        AssertCountsAddUp(result);

        Assert.Equal("First Valid", (await _dbContext.Customers.SingleAsync()).Name);

        var errors = await _dbContext.ImportErrors.OrderBy(x => x.LineNumber).ToListAsync();

        Assert.Equal("Invalid tier: Z", errors[0].Reason);
        Assert.Equal("Duplicate LegacyCustomerId 0000012345. First seen on line 2.", errors[1].Reason);
    }

    [Fact]
    public async Task ImportAsync_WithDuplicateOfExistingCustomer_UpdatesOnceFromFirstRow()
    {
        await ImportAsync(BuildLine(name: "Original"));

        var result = await ImportAsync(BuildLine(name: "First"), BuildLine(name: "Second"));

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Failed);
        AssertCountsAddUp(result);

        Assert.Equal("First", (await _dbContext.Customers.SingleAsync()).Name);
    }

    // Duplicates apply within one file, so a later import can still update.
    [Fact]
    public async Task ImportAsync_RunAgain_DoesNotTreatPreviousFileAsDuplicates()
    {
        await ImportAsync(BuildLine(name: "Original"));

        var result = await ImportAsync(BuildLine(name: "Changed"));

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Failed);
    }

    // ---------- Over-long rows ----------

    [Fact]
    public async Task ImportAsync_WithOverLongRow_TruncatesRawDataAndKeepsOriginalLength()
    {
        var overLong = BuildLine() + "EXTRA";

        var result = await ImportAsync(overLong);

        Assert.Equal(1, result.Failed);

        var error = await _dbContext.ImportErrors.SingleAsync();

        Assert.Equal(overLong[..ImportError.RawDataMaxLength], error.RawData);
        Assert.Equal(
            "Invalid line length. Expected 80 characters but got 85. " +
            "RawData was truncated from 85 to 80 characters.",
            error.Reason);
    }

    // This row fails the empty check, so its message would not mention the length.
    [Fact]
    public async Task ImportAsync_WithOverLongBlankRow_StillRecordsOriginalLength()
    {
        await ImportAsync(new string(' ', 100));

        var error = await _dbContext.ImportErrors.SingleAsync();

        Assert.Equal(ImportError.RawDataMaxLength, error.RawData.Length);
        Assert.Equal(
            "Line cannot be empty. RawData was truncated from 100 to 80 characters.",
            error.Reason);
    }

    [Fact]
    public async Task ImportAsync_WithInvalidRowAtExactLimit_StoresItUnchanged()
    {
        var invalid = BuildLine(tier: "Z");

        await ImportAsync(invalid);

        var error = await _dbContext.ImportErrors.SingleAsync();

        Assert.Equal(invalid, error.RawData);
        Assert.Equal("Invalid tier: Z", error.Reason);
    }

    // Only parse failures are row-level. Anything else, such as a database outage,
    // must stop the import instead of being recorded against the row.
    [Fact]
    public async Task ImportAsync_WhenRepositoryFails_PropagatesInsteadOfRecordingARowFailure()
    {
        var service = new CustomerImportService(
            new LegacyCustomerParser(),
            new FailingCustomerRepository(),
            new ImportRepository(_dbContext));

        await File.WriteAllLinesAsync(_filePath, [BuildLine()]);

        var job = await AddClaimedJobAsync();

        var action = () => service.ImportAsync(job, _filePath, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(action);
        Assert.Equal(0, await _dbContext.ImportErrors.CountAsync());
    }

    private sealed class FailingCustomerRepository : ICustomerRepository
    {
        public Task<Customer?> GetByLegacyCustomerIdAsync(
            string legacyCustomerId,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Database unavailable.");
        }

        public Task AddAsync(Customer customer, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Not expected.");
        }

        // Succeeds so that a swallowed lookup failure would complete the import
        // and fail the test, rather than this method supplying the exception.
        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
