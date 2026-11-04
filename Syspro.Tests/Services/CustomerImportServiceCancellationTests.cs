using System.Text;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Xunit;

namespace Syspro.Tests.Services;

public sealed class CustomerImportServiceCancellationTests
{
    private const string FirstValidLine =
        "0000012345John Smith                    john.smith@example.com        20210315A ";

    private const string SecondValidLine =
        "0000012346Jane Doe                      jane.doe@example.com          20201228B ";

    private readonly FakeCustomerRepository _customerRepository = new();
    private readonly FakeImportRepository _importRepository = new();
    private readonly ImportLog _job = new()
    {
        Status = ImportJobStatus.Processing,
        CreatedAt = DateTime.UtcNow,
        StartedAt = DateTime.UtcNow,
        AttemptCount = 1
    };
    private readonly CustomerImportService _service;

    public CustomerImportServiceCancellationTests()
    {
        _service = new CustomerImportService(
            new LegacyCustomerParser(),
            _customerRepository,
            _importRepository,
            Microsoft.Extensions.Options.Options.Create(new CustomerImportOptions()));
    }

    private static byte[] ToInput(params string[] lines)
    {
        return Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n")));
    }

    private sealed class FakeCustomerRepository : ICustomerRepository
    {
        public List<CancellationToken> ReceivedTokens { get; } = [];

        public Action? OnLookup { get; set; }

        public int LookupCount { get; private set; }

        public Task<Dictionary<string, Customer>> GetByLegacyCustomerIdsAsync(
            IReadOnlyCollection<string> legacyCustomerIds,
            CancellationToken cancellationToken)
        {
            ReceivedTokens.Add(cancellationToken);
            LookupCount++;
            OnLookup?.Invoke();

            return Task.FromResult(new Dictionary<string, Customer>(StringComparer.OrdinalIgnoreCase));
        }

        public void Add(Customer customer)
        {
        }
    }

    private sealed class FakeImportRepository : IImportRepository
    {
        public List<CancellationToken> ReceivedTokens { get; } = [];

        public int SaveCount { get; private set; }

        public Task AddLogAsync(ImportLog importLog, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public int TransactionCount { get; private set; }

        public bool IsInTransaction { get; private set; }

        public bool AcceptProgress { get; set; } = true;

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            ReceivedTokens.Add(cancellationToken);
            SaveCount++;

            return Task.CompletedTask;
        }

        public async Task ExecuteInTransactionAsync(
            Func<CancellationToken, Task> work,
            CancellationToken cancellationToken)
        {
            ReceivedTokens.Add(cancellationToken);
            TransactionCount++;
            IsInTransaction = true;

            try
            {
                await work(cancellationToken);
            }
            finally
            {
                IsInTransaction = false;
            }
        }

        public void AddErrors(IEnumerable<ImportError> errors)
        {
        }

        public Task<bool> ApplyBatchProgressAsync(
            int id,
            int expectedProcessedCount,
            ImportBatchProgress progress,
            bool completeJob,
            CancellationToken cancellationToken)
        {
            ReceivedTokens.Add(cancellationToken);

            return Task.FromResult(AcceptProgress);
        }

        public Task RequeueJobAsync(int id, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<Stream?> OpenJobInputAsync(int id, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public void RemoveJobInput(int id)
        {
        }

        public Task<ImportLog?> GetJobAsync(int id, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ImportLog?> ClaimNextPendingJobAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task ReleaseJobAsync(int id, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task MarkJobFailedAsync(int id, string reason, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<InterruptedJobRecovery> RecoverInterruptedJobsAsync(
            int maxAttempts,
            string exhaustedReason,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task ImportAsync_PassesSameTokenToEveryRepositoryCall()
    {
        // Arrange
        var input = ToInput(FirstValidLine);

        using var cancellationSource = new CancellationTokenSource();

        // Act
        await _service.ImportAsync(_job, new MemoryStream(input), cancellationSource.Token);

        // Assert
        Assert.Single(_customerRepository.ReceivedTokens);
        Assert.Equal(3, _importRepository.ReceivedTokens.Count);

        Assert.All(
            _customerRepository.ReceivedTokens.Concat(_importRepository.ReceivedTokens),
            token => Assert.Equal(cancellationSource.Token, token));
    }

    // The input is empty and the fakes ignore the token, so only the check at the
    // start of the import can observe the cancellation.
    [Fact]
    public async Task ImportAsync_WhenAlreadyCancelled_Throws()
    {
        // Arrange
        var cancelledToken = new CancellationToken(canceled: true);

        // Act
        var action = () => _service.ImportAsync(_job, new MemoryStream(), cancelledToken);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.Equal(0, _importRepository.SaveCount);
    }

    [Fact]
    public async Task ImportAsync_WhenCancelledBetweenRows_StopsAndSavesNothing()
    {
        // Arrange
        var input = ToInput(FirstValidLine, SecondValidLine);

        using var cancellationSource = new CancellationTokenSource();
        _customerRepository.OnLookup = cancellationSource.Cancel;

        // Act
        var action = () => _service.ImportAsync(_job, new MemoryStream(input), cancellationSource.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.Equal(1, _customerRepository.LookupCount);
        Assert.Equal(0, _importRepository.SaveCount);
    }

    // A reader held open during a batch would block the connection the batch is using.
    [Fact]
    public async Task ImportAsync_ReadsTheInputOnlyBetweenTransactions()
    {
        var content = ToInput(FirstValidLine, SecondValidLine);

        var service = new CustomerImportService(
            new LegacyCustomerParser(),
            _customerRepository,
            _importRepository,
            Microsoft.Extensions.Options.Options.Create(new CustomerImportOptions { BatchSize = 1 }));

        var readDuringTransaction = false;

        await using var stream = new Syspro.Api.Repositories.ImportJobInputStream(
            content.Length,
            (offset, destination, _) =>
            {
                readDuringTransaction |= _importRepository.IsInTransaction;

                var count = Math.Min(destination.Length, content.Length - (int)offset);

                content.AsMemory((int)offset, count).CopyTo(destination);

                return Task.FromResult(count);
            },
            chunkBytes: 16);

        await service.ImportAsync(_job, stream, CancellationToken.None);

        Assert.Equal(2, _importRepository.TransactionCount);
        Assert.False(readDuringTransaction);
    }

    [Fact]
    public async Task ImportAsync_WhenTheCheckpointWasMovedElsewhere_StopsWithoutSaving()
    {
        _importRepository.AcceptProgress = false;

        var action = () => _service.ImportAsync(_job, new MemoryStream(ToInput(FirstValidLine)), CancellationToken.None);

        await Assert.ThrowsAsync<ImportJobStateChangedException>(action);
        Assert.Equal(0, _importRepository.SaveCount);
        Assert.Equal(0, _job.ProcessedCount);
    }

    [Fact]
    public async Task ImportAsync_WhenLookupIsCancelled_DoesNotRecordRowFailure()
    {
        // Arrange
        var input = ToInput(FirstValidLine);

        using var cancellationSource = new CancellationTokenSource();

        _customerRepository.OnLookup = () =>
            throw new OperationCanceledException(cancellationSource.Token);

        // Act
        var action = () => _service.ImportAsync(_job, new MemoryStream(input), cancellationSource.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);

        Assert.Equal(ImportJobStatus.Processing, _job.Status);
        Assert.Equal(0, _job.FailedCount);
        Assert.Equal(0, _importRepository.SaveCount);
    }
}
