using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Syspro.Api.Data;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Syspro.Tests.TestSupport;
using Xunit;

namespace Syspro.Tests.Services;

public sealed class CustomerImportJobServiceTests : IDisposable
{
    private const int MaxInputBytes = 16;

    private readonly DirectoryInfo _contentRoot = Directory.CreateTempSubdirectory();
    private readonly AppDbContext _dbContext;
    private readonly CustomerImportJobService _service;

    public CustomerImportJobServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);

        _service = new CustomerImportJobService(
            new ImportRepository(_dbContext),
            new TestHostEnvironment { ContentRootPath = _contentRoot.FullName },
            Options.Create(new CustomerImportOptions { MaxInputBytes = MaxInputBytes }));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _contentRoot.Delete(recursive: true);
    }

    private async Task WriteFileAsync(byte[] content)
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot.FullName, "LegacyData"));

        await File.WriteAllBytesAsync(
            Path.Combine(_contentRoot.FullName, "LegacyData", "customers_legacy.dat"),
            content);
    }

    private async Task AssertNothingStoredAsync()
    {
        Assert.Equal(0, await _dbContext.ImportLogs.CountAsync());
        Assert.Equal(0, await _dbContext.ImportJobInputs.CountAsync());
    }

    [Fact]
    public async Task Schedule_StoresTheFileBytesUnchangedWithThePendingJob()
    {
        byte[] content = [0xEF, 0xBB, 0xBF, (byte)'A', 0x0D, 0x0A, 0xFF, 0x00];

        await WriteFileAsync(content);

        var job = await _service.ScheduleAsync(CancellationToken.None);

        Assert.Equal("Pending", job.Status);

        var stored = await _dbContext.ImportJobInputs.SingleAsync();

        Assert.Equal(job.Id, stored.ImportLogId);
        Assert.Equal(content, stored.Content);
    }

    [Fact]
    public async Task Schedule_WithEmptyFile_StoresEmptyInput()
    {
        await WriteFileAsync([]);

        await _service.ScheduleAsync(CancellationToken.None);

        Assert.Empty((await _dbContext.ImportJobInputs.SingleAsync()).Content);
    }

    [Fact]
    public async Task Schedule_WithFileExactlyAtLimit_IsAccepted()
    {
        await WriteFileAsync(new byte[MaxInputBytes]);

        await _service.ScheduleAsync(CancellationToken.None);

        Assert.Equal(MaxInputBytes, (await _dbContext.ImportJobInputs.SingleAsync()).Content.Length);
    }

    [Fact]
    public async Task Schedule_WithFileOverLimit_ThrowsAndStoresNothing()
    {
        await WriteFileAsync(new byte[MaxInputBytes + 1]);

        var exception = await Assert.ThrowsAsync<ImportInputTooLargeException>(
            () => _service.ScheduleAsync(CancellationToken.None));

        Assert.DoesNotContain(_contentRoot.FullName, exception.Message);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Schedule_WhenFileIsMissing_ThrowsNotFoundAndStoresNothing()
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot.FullName, "LegacyData"));

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.ScheduleAsync(CancellationToken.None));

        Assert.Equal("Legacy customer file was not found.", exception.Message);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Schedule_WhenDirectoryIsMissing_ThrowsNotFoundAndStoresNothing()
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.ScheduleAsync(CancellationToken.None));

        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Schedule_WhenCancelled_StoresNothing()
    {
        await WriteFileAsync([1, 2, 3]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.ScheduleAsync(new CancellationToken(canceled: true)));

        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Get_ForUnknownJob_ReturnsNull()
    {
        Assert.Null(await _service.GetAsync(999, CancellationToken.None));
    }
}
