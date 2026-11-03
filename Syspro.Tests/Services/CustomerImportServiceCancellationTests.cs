using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Xunit;

namespace Syspro.Tests.Services;

public sealed class CustomerImportServiceCancellationTests : IDisposable
{
    private const string FirstValidLine =
        "0000012345John Smith                    john.smith@example.com        20210315A ";

    private const string SecondValidLine =
        "0000012346Jane Doe                      jane.doe@example.com          20201228B ";

    private readonly string _filePath = Path.GetTempFileName();
    private readonly FakeCustomerRepository _customerRepository = new();
    private readonly FakeImportRepository _importRepository = new();
    private readonly CustomerImportService _service;

    public CustomerImportServiceCancellationTests()
    {
        _service = new CustomerImportService(
            new LegacyCustomerParser(),
            _customerRepository,
            _importRepository);
    }

    public void Dispose()
    {
        File.Delete(_filePath);
    }

    private sealed class FakeCustomerRepository : ICustomerRepository
    {
        public List<CancellationToken> ReceivedTokens { get; } = [];

        public Action? OnLookup { get; set; }

        public int LookupCount { get; private set; }

        public int SaveCount { get; private set; }

        public Task<Customer?> GetByLegacyCustomerIdAsync(
            string legacyCustomerId,
            CancellationToken cancellationToken)
        {
            ReceivedTokens.Add(cancellationToken);
            LookupCount++;
            OnLookup?.Invoke();

            return Task.FromResult<Customer?>(null);
        }

        public Task AddAsync(Customer customer, CancellationToken cancellationToken)
        {
            ReceivedTokens.Add(cancellationToken);

            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            ReceivedTokens.Add(cancellationToken);
            SaveCount++;

            return Task.CompletedTask;
        }
    }

    private sealed class FakeImportRepository : IImportRepository
    {
        public List<CancellationToken> ReceivedTokens { get; } = [];

        public ImportLog? ImportLog { get; private set; }

        public int SaveCount { get; private set; }

        public Task AddLogAsync(ImportLog importLog, CancellationToken cancellationToken)
        {
            ReceivedTokens.Add(cancellationToken);
            ImportLog = importLog;

            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            ReceivedTokens.Add(cancellationToken);
            SaveCount++;

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ImportAsync_PassesSameTokenToEveryRepositoryCall()
    {
        // Arrange
        await File.WriteAllLinesAsync(_filePath, [FirstValidLine]);

        using var cancellationSource = new CancellationTokenSource();

        // Act
        await _service.ImportAsync(_filePath, cancellationSource.Token);

        // Assert
        Assert.Equal(3, _customerRepository.ReceivedTokens.Count);
        Assert.Equal(2, _importRepository.ReceivedTokens.Count);

        Assert.All(
            _customerRepository.ReceivedTokens.Concat(_importRepository.ReceivedTokens),
            token => Assert.Equal(cancellationSource.Token, token));
    }

    // The file is empty and the fakes ignore the token, so only the file read
    // can observe the cancellation.
    [Fact]
    public async Task ImportAsync_WhenCancelled_CancelsFileRead()
    {
        // Arrange
        var cancelledToken = new CancellationToken(canceled: true);

        // Act
        var action = () => _service.ImportAsync(_filePath, cancelledToken);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.Equal(0, _customerRepository.SaveCount);
        Assert.Equal(0, _importRepository.SaveCount);
    }

    [Fact]
    public async Task ImportAsync_WhenCancelledBetweenRows_StopsAndSavesNothing()
    {
        // Arrange
        await File.WriteAllLinesAsync(_filePath, [FirstValidLine, SecondValidLine]);

        using var cancellationSource = new CancellationTokenSource();
        _customerRepository.OnLookup = cancellationSource.Cancel;

        // Act
        var action = () => _service.ImportAsync(_filePath, cancellationSource.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.Equal(1, _customerRepository.LookupCount);
        Assert.Equal(0, _customerRepository.SaveCount);
        Assert.Equal(0, _importRepository.SaveCount);
    }

    [Fact]
    public async Task ImportAsync_WhenLookupIsCancelled_DoesNotRecordRowFailure()
    {
        // Arrange
        await File.WriteAllLinesAsync(_filePath, [FirstValidLine]);

        using var cancellationSource = new CancellationTokenSource();

        _customerRepository.OnLookup = () =>
            throw new OperationCanceledException(cancellationSource.Token);

        // Act
        var action = () => _service.ImportAsync(_filePath, cancellationSource.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);

        Assert.NotNull(_importRepository.ImportLog);
        Assert.Equal(0, _importRepository.ImportLog.FailedCount);
        Assert.Empty(_importRepository.ImportLog.Errors);
        Assert.Equal(0, _importRepository.SaveCount);
    }
}
