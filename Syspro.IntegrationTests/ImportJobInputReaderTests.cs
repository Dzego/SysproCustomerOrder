using System.Data.Common;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Syspro.Api.Data;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Syspro.IntegrationTests.Infrastructure;
using Xunit.Abstractions;

namespace Syspro.IntegrationTests;

// Reads real snapshots back from SQL Server and compares them with the original file.
public sealed class ImportJobInputReaderTests : SqlServerTestBase
{
    private const int Chunk = ImportJobInputStream.DefaultChunkBytes;

    private readonly ITestOutputHelper _output;
    private readonly string _filePath = Path.GetTempFileName();

    public ImportJobInputReaderTests(SqlServerFixture fixture, ITestOutputHelper output)
        : base(fixture)
    {
        _output = output;
    }

    public override async Task DisposeAsync()
    {
        File.Delete(_filePath);

        await base.DisposeAsync();
    }

    private static byte[] CreateRandomContent(int length)
    {
        var content = new byte[length];

        new Random(length).NextBytes(content);

        return content;
    }

    private async Task<byte[]> ReadBackAsync(int jobId)
    {
        await using var dbContext = CreateDbContext();

        await using var input = await new ImportRepository(dbContext)
            .OpenJobInputAsync(jobId, CancellationToken.None);

        Assert.NotNull(input);

        using var result = new MemoryStream();

        await input.CopyToAsync(result);

        return result.ToArray();
    }

    private static string BuildValid(int i, string name = "Customer")
    {
        return TestData.LegacyLine(legacyCustomerId: $"{i:D10}", name: $"{name} {i}");
    }

    // Rows failing the tier rule cost no customer lookup, which keeps large files quick.
    private static string BuildInvalid(int i)
    {
        return TestData.LegacyLine(legacyCustomerId: $"{i:D10}", name: $"Bad {i}", tier: "Z");
    }

    private CustomerImportJobRunner CreateRunner(AppDbContext dbContext)
    {
        var repository = new ImportRepository(dbContext);

        return new CustomerImportJobRunner(
            repository,
            new CustomerImportService(new LegacyCustomerParser(), new CustomerRepository(dbContext), repository,
            TestData.ImportOptions()),
            Microsoft.Extensions.Options.Options.Create(new Syspro.Api.BackgroundJobs.CustomerImportWorkerOptions()),
            new TestOutputLogger<CustomerImportJobRunner>(_output));
    }

    private async Task<ImportLog> RunJobAsync(byte[] input)
    {
        var pending = await AddPendingJobAsync(input: input);

        // These tests check the outcome, not the number of executions. A real connection
        // failure requeues the job, and it then resumes, so run until nothing is waiting.
        while (true)
        {
            await using var dbContext = CreateDbContext();

            if (!await CreateRunner(dbContext).RunNextJobAsync(CancellationToken.None))
            {
                break;
            }
        }

        return await QueryAsync(context => context.ImportLogs.SingleAsync(x => x.Id == pending.Id));
    }

    // Writes the bytes as a real file so that File.ReadAllLines supplies the expected
    // lines, then checks that SQL Server's copy produced an identical outcome.
    private async Task AssertImportMatchesFileAsync(byte[] content)
    {
        await File.WriteAllBytesAsync(_filePath, content);

        var lines = await File.ReadAllLinesAsync(_filePath);

        var job = await RunJobAsync(content);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(lines.Length, job.ProcessedCount);
        Assert.Equal(lines.Length, job.CreatedCount + job.UpdatedCount + job.FailedCount);

        var parser = new LegacyCustomerParser();
        var expectedErrors = new List<string>();
        var expectedCustomers = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            try
            {
                var record = parser.Parse(lines[i]);

                expectedCustomers.Add(record.LegacyCustomerId + "|" + record.Name);
            }
            catch (LegacyCustomerParseException)
            {
                expectedErrors.Add($"{i + 1}|{lines[i][..Math.Min(lines[i].Length, 80)]}");
            }
        }

        var customers = await QueryAsync(context => context.Customers
            .Select(x => x.LegacyCustomerId + "|" + x.Name)
            .ToListAsync());

        var errors = await QueryAsync(context => context.ImportErrors
            .OrderBy(x => x.LineNumber)
            .Select(x => x.LineNumber + "|" + x.RawData)
            .ToListAsync());

        Assert.Equal(expectedCustomers.Order(), customers.Order());
        Assert.Equal(expectedErrors, errors);
    }

    // ---------- Reading back the exact bytes ----------

    [SqlServerTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(Chunk * 2)]
    [InlineData(Chunk * 2 + 12345)]
    public async Task OpenJobInput_ReturnsExactlyWhatWasStored(int length)
    {
        var content = CreateRandomContent(length);

        var job = await AddPendingJobAsync(input: content);

        Assert.Equal(content, await ReadBackAsync(job.Id));
    }

    [SqlServerFact]
    public async Task OpenJobInput_WhenJobHasNoInput_ReturnsNull()
    {
        await using var dbContext = CreateDbContext();

        Assert.Null(await new ImportRepository(dbContext).OpenJobInputAsync(999, CancellationToken.None));
    }

    // The largest default snapshot. The old read took minutes at this size, so simply
    // finishing shows the change. The allocation limit keeps memory use from regressing:
    // loading the whole value would allocate at least its full size.
    [SqlServerFact]
    public async Task OpenJobInput_ReadsLargeSnapshotExactlyWithBoundedAllocation()
    {
        const int size = 50 * 1024 * 1024;

        var content = CreateRandomContent(size);
        var expectedHash = SHA256.HashData(content);

        var job = await AddPendingJobAsync(input: content);

        content = [];
        GC.Collect();

        await using var dbContext = CreateDbContext();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = System.Diagnostics.Stopwatch.StartNew();

        await using var input = await new ImportRepository(dbContext)
            .OpenJobInputAsync(job.Id, CancellationToken.None);

        Assert.NotNull(input);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        int read;

        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        started.Stop();

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        _output.WriteLine($"Read {size} bytes in {started.ElapsedMilliseconds} ms, allocating {allocated} bytes.");

        Assert.Equal(expectedHash, hash.GetHashAndReset());
        Assert.InRange(allocated, 0, size / 2);
    }

    // ---------- Whole imports compared with the original file ----------

    [SqlServerFact]
    public async Task Import_LargeFileAcrossSeveralChunks_MatchesTheFile()
    {
        // About 3.2 MiB. Almost all rows are invalid, which also checks line numbers.
        var builder = new StringBuilder();

        for (var i = 1; i <= 40_000; i++)
        {
            builder.Append(i % 1000 == 0 ? BuildValid(i) : BuildInvalid(i));
            builder.Append(i % 3 == 0 ? "\r\n" : "\n");
        }

        var content = Encoding.UTF8.GetBytes(builder.ToString());

        Assert.True(content.Length > Chunk * 3);

        await AssertImportMatchesFileAsync(content);
    }

    // Fills the first chunk exactly, so that the next item starts in the second chunk
    // or is split between them. Filler rows are invalid, so they only add row errors.
    private static byte[] BuildWithTailStartingAt(int boundaryOffset, byte[] tail, byte[]? prefix = null)
    {
        var bytes = new List<byte>(prefix ?? []);
        var number = 1;

        var target = Chunk + boundaryOffset;

        while (bytes.Count < target)
        {
            var row = Encoding.UTF8.GetBytes(BuildInvalid(number++) + "\n");
            var remaining = target - bytes.Count;

            if (remaining < row.Length)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(new string('p', remaining)));
            }
            else
            {
                bytes.AddRange(row);
            }
        }

        bytes.AddRange(tail);

        return [.. bytes];
    }

    [SqlServerTheory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(0)]
    public async Task Import_MultiByteCharacterSplitAcrossChunks_IsDecodedCorrectly(int boundaryOffset)
    {
        // The name starts with a four-byte character and is followed by three-byte ones,
        // so one of them straddles the boundary for each offset.
        var name = "😀日本語 Ñandú";
        var line = TestData.LegacyLine(legacyCustomerId: "9000000001", name: name);

        var content = BuildWithTailStartingAt(
            boundaryOffset,
            Encoding.UTF8.GetBytes("\n" + line + "\n"));

        await AssertImportMatchesFileAsync(content);

        var customer = await QueryAsync(context =>
            context.Customers.SingleAsync(x => x.LegacyCustomerId == "9000000001"));

        Assert.Equal(name, customer.Name);
    }

    [SqlServerFact]
    public async Task Import_CrLfSplitAcrossChunks_CountsAsOneLineEnding()
    {
        var content = BuildWithTailStartingAt(-1, Encoding.UTF8.GetBytes(
            "\r\n" + BuildValid(9000000) + "\r\n"));

        Assert.Equal((byte)'\r', content[Chunk - 1]);
        Assert.Equal((byte)'\n', content[Chunk]);

        await AssertImportMatchesFileAsync(content);
    }

    [SqlServerFact]
    public async Task Import_ValidRowSplitAcrossChunks_IsImported()
    {
        var line = BuildValid(9000000, "Straddles");

        var content = BuildWithTailStartingAt(-40, Encoding.UTF8.GetBytes("\n" + line + "\n"));

        await AssertImportMatchesFileAsync(content);

        Assert.Equal(1, await QueryAsync(context =>
            context.Customers.CountAsync(x => x.LegacyCustomerId == $"{9000000:D10}")));
    }

    [SqlServerFact]
    public async Task Import_ByteOrderMarkAndMalformedBytes_MatchTheFile()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];

        // Byte order mark, a row with an invalid UTF-8 byte in its name, a blank line,
        // an over-long row, and a last row with no line ending.
        var malformed = Encoding.UTF8.GetBytes(BuildValid(1)).ToArray();
        malformed[15] = 0xFF;

        var content = bom
            .Concat(malformed)
            .Concat(Encoding.UTF8.GetBytes("\r\n\n" + BuildValid(2) + "EXTRA\n" + BuildValid(3)))
            .ToArray();

        await AssertImportMatchesFileAsync(content);
    }

    [SqlServerFact]
    public async Task Import_EmptySnapshot_CompletesWithZeroCounts()
    {
        var job = await RunJobAsync([]);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(0, job.ProcessedCount);
    }

    // ---------- Connection use ----------

    private sealed class CustomerQueryRecorder : DbCommandInterceptor
    {
        public int Lookups { get; private set; }

        public int LookupsInsideATransaction { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("[Customers]"))
            {
                Lookups++;

                if (command.Transaction is not null)
                {
                    LookupsInsideATransaction++;
                }
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // Lookups run on the same connection between chunk reads. Without MARS, a reader left
    // open by a chunk read would make the next lookup throw, so completing proves none is.
    [SqlServerFact]
    public async Task ChunkReads_LeaveNoReaderOpenForBatchTransactions()
    {
        var builder = new StringBuilder();

        // Spread valid rows across more than two chunks.
        for (var i = 1; i <= 30_000; i++)
        {
            builder.Append(i % 500 == 0 ? BuildValid(i) : BuildInvalid(i)).Append('\n');
        }

        var content = Encoding.UTF8.GetBytes(builder.ToString());

        Assert.True(content.Length > Chunk * 2);

        var pending = await AddPendingJobAsync(input: content);

        var recorder = new CustomerQueryRecorder();

        await using (var dbContext = CreateDbContext(recorder))
        {
            Assert.True(await CreateRunner(dbContext).RunNextJobAsync(CancellationToken.None));
        }

        var job = await QueryAsync(context => context.ImportLogs.SingleAsync(x => x.Id == pending.Id));

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(60, job.CreatedCount);

        // One set-based lookup per batch, each inside that batch's transaction.
        Assert.Equal(30, recorder.Lookups);
        Assert.Equal(30, recorder.LookupsInsideATransaction);
    }

    // ---------- Snapshot safety ----------

    [SqlServerFact]
    public async Task ReadingAndFailedAttempts_LeaveTheStoredInputUnchanged()
    {
        var content = TestData.ToInput(BuildValid(1), BuildValid(2));

        var pending = await AddPendingJobAsync(input: content);

        await ReadBackAsync(pending.Id);

        await using (var dbContext = CreateDbContext(new FailOnCommitInterceptor()))
        {
            var repository = new ImportRepository(dbContext);

            var claimed = await repository.ClaimNextPendingJobAsync(CancellationToken.None);

            await using var input = await repository.OpenJobInputAsync(claimed!.Id, CancellationToken.None);

            var service = new CustomerImportService(
                new LegacyCustomerParser(),
                new CustomerRepository(dbContext),
                repository,
            TestData.ImportOptions());

            await Assert.ThrowsAsync<FailOnCommitInterceptor.CommitFailedException>(
                () => service.ImportAsync(claimed, input!, CancellationToken.None));
        }

        Assert.Equal(content, await ReadBackAsync(pending.Id));
    }

    // Must fail the job. Treating it as the end of the file would report success after
    // importing only the rows read so far.
    [SqlServerFact]
    public async Task InputDeletedPartWayThroughReading_ThrowsInsteadOfEndingEarly()
    {
        var content = CreateRandomContent(Chunk * 2);

        var job = await AddPendingJobAsync(input: content);

        await using var dbContext = CreateDbContext();

        await using var input = await new ImportRepository(dbContext)
            .OpenJobInputAsync(job.Id, CancellationToken.None);

        Assert.NotNull(input);

        var buffer = new byte[Chunk];

        await input.ReadExactlyAsync(buffer);

        await using (var other = CreateDbContext())
        {
            await other.ImportJobInputs.ExecuteDeleteAsync();
        }

        await Assert.ThrowsAsync<IOException>(() => input.ReadAsync(buffer).AsTask());
    }

    [SqlServerFact]
    public async Task ReadingWithACancelledToken_RaisesARecognizedCancellation()
    {
        var job = await AddPendingJobAsync(input: CreateRandomContent(1024));

        await using var dbContext = CreateDbContext();

        await using var input = await new ImportRepository(dbContext)
            .OpenJobInputAsync(job.Id, CancellationToken.None);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => input!.ReadAsync(new byte[16], new CancellationToken(canceled: true)).AsTask());

        Assert.True(Syspro.Api.ExceptionHandling.CancellationExceptionClassifier.IsCancellation(exception));
    }

    // ---------- Through the API ----------

    // The real path: file to POST, to SQL Server, to the worker's chunked reader.
    [SqlServerFact]
    public async Task ApiImport_OfLargeFile_ProducesResultsMatchingTheOriginalFile()
    {
        var contentRoot = Directory.CreateTempSubdirectory();

        try
        {
            var legacyDirectory = Directory.CreateDirectory(Path.Combine(contentRoot.FullName, "LegacyData"));
            var path = Path.Combine(legacyDirectory.FullName, "customers_legacy.dat");

            var lines = Enumerable.Range(1, 30_000)
                .Select(i => i % 1000 == 0 ? BuildValid(i, "Ñandú 日本") : BuildInvalid(i))
                .ToArray();

            await File.WriteAllLinesAsync(path, lines);

            var original = await File.ReadAllLinesAsync(path);

            using var factory = new ApiFactory(ConnectionString, contentRoot.FullName, runImportWorker: true);
            using var client = factory.CreateClient();

            var response = await client.PostAsync("/api/import/customers", content: null);

            var job = await ImportJobClient.WaitForTerminalStatusAsync(client, response.Headers.Location!);

            Assert.Equal("Completed", job.Status);
            Assert.Equal(original.Length, job.Processed);
            Assert.Equal(30, job.Created);
            Assert.Equal(original.Length - 30, job.Failed);

            var errors = await QueryAsync(context => context.ImportErrors
                .OrderBy(x => x.LineNumber)
                .Select(x => new { x.LineNumber, x.RawData })
                .ToListAsync());

            Assert.All(errors, error => Assert.Equal(original[error.LineNumber - 1], error.RawData));

            var name = await QueryAsync(context => context.Customers
                .Where(x => x.LegacyCustomerId == $"{1000:D10}")
                .Select(x => x.Name)
                .SingleAsync());

            Assert.Equal("Ñandú 日本 1000", name);
            Assert.Equal(0, await QueryAsync(context => context.ImportJobInputs.CountAsync()));
        }
        finally
        {
            contentRoot.Delete(recursive: true);
        }
    }
}
