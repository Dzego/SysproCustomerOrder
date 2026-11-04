using System.Text;
using Microsoft.EntityFrameworkCore;
using Syspro.Api.Data;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Syspro.Tests.TestSupport;
using Xunit;

namespace Syspro.Tests.Services;

// The import used to read a whole file with File.ReadAllLines. These prove that reading
// chunk by chunk produces the same lines for the same bytes, wherever the chunks split.
// Trying every chunk size places a boundary at every position after the byte order mark.
public sealed class ImportInputDecodingTests : IDisposable
{
    private readonly string _filePath = Path.GetTempFileName();

    public void Dispose()
    {
        File.Delete(_filePath);
    }

    private static string BuildLine(string id, string name = "John Smith", string tier = "A")
    {
        return id.PadRight(10) +
               name.PadRight(30) +
               "john.smith@example.com".PadRight(30) +
               "20210315" +
               tier.PadRight(2);
    }

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static byte[] Encode(string text, params byte[][] prefix)
    {
        return prefix.Aggregate(Array.Empty<byte>(), (all, next) => [.. all, .. next])
            .Concat(Encoding.UTF8.GetBytes(text))
            .ToArray();
    }

    public static TheoryData<string, byte[]> Inputs => new()
    {
        { "LF", Encode("first\nsecond\n") },
        { "CRLF", Encode("first\r\nsecond\r\n") },
        { "CR", Encode("first\rsecond\r") },
        { "mixed line endings", Encode("a\nb\r\nc\rd\n") },
        { "no final line ending", Encode("first\nsecond") },
        { "final bare CR after CRLF", Encode("first\r\n\r") },
        { "blank lines", Encode("\n\nfirst\n\n\r\n\nsecond\n\n") },
        { "only a line ending", Encode("\n") },
        { "empty", [] },
        { "UTF-8 BOM", Encode("first\nsecond\n", Utf8Bom) },
        { "only a UTF-8 BOM", Utf8Bom },
        { "BOM then CRLF", Encode("\r\nfirst\r\n", Utf8Bom) },
        { "UTF-16 LE BOM", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("first\r\nsecond\r\n")] },
        { "UTF-16 BE BOM", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("first\nsecond\n")] },
        { "UTF-32 LE BOM", [0xFF, 0xFE, 0x00, 0x00, .. Encoding.UTF32.GetBytes("first\nsecond\n")] },
        { "UTF-32 BE BOM", [0x00, 0x00, 0xFE, 0xFF, .. new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetBytes("first\n")] },
        { "two-byte BOM and one character", [0xFF, 0xFE, (byte)'a', 0x00] },
        { "two-byte characters", Encode("Ñandú Škoda\nÉmile\n") },
        { "three-byte characters", Encode("日本語のテキスト\n中国\n") },
        { "four-byte characters", Encode("😀😃😄\n🎉\n") },
        { "invalid UTF-8 byte", [.. Encoding.UTF8.GetBytes("ab"), 0xFF, .. Encoding.UTF8.GetBytes("cd\n")] },
        { "truncated multi-byte sequence at end", [.. Encoding.UTF8.GetBytes("ab\n"), 0xE6, 0x97] },
        { "overlong and stray continuation bytes", [0xC0, 0x80, 0x80, (byte)'\n', 0xF8, (byte)'x', (byte)'\n'] },
        { "NUL byte", [(byte)'a', 0x00, (byte)'b', (byte)'\n'] },
        { "Latin-1 bytes, not valid UTF-8", [(byte)'J', 0xF6, (byte)'r', (byte)'g', (byte)'\n'] }
    };

    private static Stream OpenInChunks(byte[] content, int chunkBytes)
    {
        return new ImportJobInputStream(
            content.Length,
            (offset, destination, _) =>
            {
                var count = Math.Min(destination.Length, content.Length - (int)offset);

                content.AsMemory((int)offset, count).CopyTo(destination);

                return Task.FromResult(count);
            },
            chunkBytes);
    }

    private static async Task<List<string>> ReadLinesAsync(Stream stream)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        var lines = new List<string>();

        while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task EveryChunkSize_ProducesTheSameLinesAsFileReadAllLines(string description, byte[] content)
    {
        await File.WriteAllBytesAsync(_filePath, content);

        var expected = await File.ReadAllLinesAsync(_filePath);

        for (var chunkBytes = ImportJobInputStream.MinChunkBytes; chunkBytes <= Math.Max(content.Length + 1, 8); chunkBytes++)
        {
            await using var stream = OpenInChunks(content, chunkBytes);

            var actual = await ReadLinesAsync(stream);

            Assert.True(
                expected.SequenceEqual(actual),
                $"{description}: chunk size {chunkBytes} produced different lines");
        }
    }

    // Puts a chunk boundary at every position, using the real import and the real rules.
    [Fact]
    public async Task Import_ProducesTheSameResultsWhereverChunksSplitTheInput()
    {
        var content = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(
                BuildLine("0000000001", "Ñandú Škoda 日本語") + "\r\n" +
                BuildLine("0000000002", "Zoë 😀", tier: "Z") + "\n" +
                "\r\n" +
                BuildLine("0000000001", "Duplicate") + "\r" +
                BuildLine("0000000003", "Last") + BuildLine("x", "over-long") + "\n" +
                BuildLine("0000000004", "No final ending")))
            .ToArray();

        await File.WriteAllBytesAsync(_filePath, content);

        var expectedLines = await File.ReadAllLinesAsync(_filePath);

        var whole = await ImportAsync(new MemoryStream(content));

        Assert.Equal(expectedLines.Length, whole.Log.ProcessedCount);
        Assert.Equal(2, whole.Log.CreatedCount);
        Assert.Equal(4, whole.Log.FailedCount);

        foreach (var chunkBytes in new[] { 4, 5, 6, 7, 11, 13, 79, 80, 81, 82, 83, 160, content.Length })
        {
            await using var stream = OpenInChunks(content, chunkBytes);

            var chunked = await ImportAsync(stream);

            Assert.Equal(whole.Customers, chunked.Customers);
            Assert.Equal(whole.Errors, chunked.Errors);
            Assert.Equal(whole.Log.ProcessedCount, chunked.Log.ProcessedCount);
            Assert.Equal(whole.Log.CreatedCount, chunked.Log.CreatedCount);
            Assert.Equal(whole.Log.UpdatedCount, chunked.Log.UpdatedCount);
            Assert.Equal(whole.Log.FailedCount, chunked.Log.FailedCount);
        }
    }

    public static TheoryData<string, byte[]> EncodingsWithByteOrderMark => new()
    {
        { "UTF-8", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(BuildLine("0000000001", "Zoë") + "\n")] },
        { "UTF-16 LE", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(BuildLine("0000000001", "Zoë") + "\n")] },
        { "UTF-16 BE", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(BuildLine("0000000001", "Zoë") + "\n")] },
        { "UTF-32 LE", [0xFF, 0xFE, 0x00, 0x00, .. Encoding.UTF32.GetBytes(BuildLine("0000000001", "Zoë") + "\n")] }
    };

    // File.ReadAllLines honored all of these, so the import itself must as well.
    [Theory]
    [MemberData(nameof(EncodingsWithByteOrderMark))]
    public async Task Import_HonorsTheByteOrderMark(string description, byte[] content)
    {
        var result = await ImportAsync(new MemoryStream(content));

        Assert.True(result.Log.CreatedCount == 1, $"{description}: the row was not recognized");
        Assert.Equal(new[] { "0000000001|Zoë|A" }, result.Customers);
    }

    // A failed read is not a bad row: it must stop the import, not skip the line.
    [Fact]
    public async Task Import_WhenReadingFailsPartWay_PropagatesAndSavesNothing()
    {
        var content = Encoding.UTF8.GetBytes(
            BuildLine("0000000001") + "\n" + BuildLine("0000000002") + "\n");

        await using var dbContext = CreateDbContext();

        var job = await AddClaimedJobAsync(dbContext);

        // Claims to hold more than it can return, like a row deleted during the read.
        await using var stream = new ImportJobInputStream(
            content.Length + 100,
            (offset, destination, _) =>
            {
                var count = Math.Min(destination.Length, Math.Max(0, content.Length - (int)offset));

                content.AsMemory((int)offset, count).CopyTo(destination);

                return Task.FromResult(count);
            },
            chunkBytes: 32);

        await Assert.ThrowsAsync<IOException>(
            () => CreateService(dbContext).ImportAsync(job, stream, CancellationToken.None));

        Assert.Equal(0, await dbContext.Customers.CountAsync());
        Assert.Equal(0, await dbContext.ImportErrors.CountAsync());
        Assert.Equal(ImportJobStatus.Processing, job.Status);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static CustomerImportService CreateService(AppDbContext dbContext)
    {
        return new CustomerImportService(
            new LegacyCustomerParser(),
            new CustomerRepository(dbContext),
            new InMemoryImportRepository(dbContext),
            Microsoft.Extensions.Options.Options.Create(new CustomerImportOptions()));
    }

    private static async Task<ImportLog> AddClaimedJobAsync(AppDbContext dbContext)
    {
        var now = DateTime.UtcNow;

        var job = new ImportLog
        {
            Status = ImportJobStatus.Processing,
            CreatedAt = now,
            StartedAt = now,
            AttemptCount = 1,
            Input = new ImportJobInput { Content = [] }
        };

        dbContext.ImportLogs.Add(job);
        await dbContext.SaveChangesAsync();

        // A claimed job and its input are not tracked, so match that here.
        dbContext.ChangeTracker.Clear();
        job.Input = null;

        return job;
    }

    private static async Task<(ImportLog Log, List<string> Customers, List<string> Errors)> ImportAsync(Stream input)
    {
        await using var dbContext = CreateDbContext();

        var job = await AddClaimedJobAsync(dbContext);

        await CreateService(dbContext).ImportAsync(job, input, CancellationToken.None);

        var customers = await dbContext.Customers
            .OrderBy(x => x.LegacyCustomerId)
            .Select(x => x.LegacyCustomerId + "|" + x.Name + "|" + x.Tier)
            .ToListAsync();

        var errors = await dbContext.ImportErrors
            .OrderBy(x => x.LineNumber)
            .Select(x => x.LineNumber + "|" + x.RawData + "|" + x.Reason)
            .ToListAsync();

        return (job, customers, errors);
    }
}
