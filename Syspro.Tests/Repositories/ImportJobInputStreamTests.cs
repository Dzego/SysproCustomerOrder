using Syspro.Api.Repositories;
using Xunit;

namespace Syspro.Tests.Repositories;

public class ImportJobInputStreamTests
{
    private const int ChunkBytes = 16;

    private sealed class FakeStore(byte[] content, int? maxBytesPerRead = null)
    {
        public List<(long Offset, int Length)> Reads { get; } = [];

        public Task<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Reads.Add((offset, destination.Length));

            var available = Math.Max(0, content.Length - (int)offset);
            var count = Math.Min(destination.Length, Math.Min(available, maxBytesPerRead ?? int.MaxValue));

            content.AsMemory((int)offset, count).CopyTo(destination);

            return Task.FromResult(count);
        }
    }

    private static byte[] CreateContent(int length)
    {
        var content = new byte[length];

        new Random(length).NextBytes(content);

        return content;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, int callerBufferBytes = 4096)
    {
        using var result = new MemoryStream();

        var buffer = new byte[callerBufferBytes];
        int read;

        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ChunkBytes - 1)]
    [InlineData(ChunkBytes)]
    [InlineData(ChunkBytes + 1)]
    [InlineData(ChunkBytes * 3)]
    [InlineData(ChunkBytes * 3 + 5)]
    public async Task Read_ReturnsExactlyTheStoredBytes(int length)
    {
        var content = CreateContent(length);
        var store = new FakeStore(content);

        await using var stream = new ImportJobInputStream(length, store.ReadAsync, ChunkBytes);

        Assert.Equal(content, await ReadAllAsync(stream));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(ChunkBytes - 1)]
    [InlineData(ChunkBytes + 1)]
    public async Task Read_ReturnsExactlyTheStoredBytesForAnyCallerBufferSize(int callerBufferBytes)
    {
        var content = CreateContent(ChunkBytes * 3 + 5);
        var store = new FakeStore(content);

        await using var stream = new ImportJobInputStream(content.Length, store.ReadAsync, ChunkBytes);

        Assert.Equal(content, await ReadAllAsync(stream, callerBufferBytes));
    }

    // A store may legitimately return fewer bytes than requested.
    [Fact]
    public async Task Read_ContinuesFromTheRightOffsetAfterAShortRead()
    {
        var content = CreateContent(ChunkBytes * 2);
        var store = new FakeStore(content, maxBytesPerRead: 5);

        await using var stream = new ImportJobInputStream(content.Length, store.ReadAsync, ChunkBytes);

        Assert.Equal(content, await ReadAllAsync(stream));
        Assert.Equal(0, store.Reads[0].Offset);
        Assert.Equal(5, store.Reads[1].Offset);
    }

    [Fact]
    public async Task Read_FetchesOneChunkAtATimeInOrderAndNothingBeyondTheEnd()
    {
        var content = CreateContent(ChunkBytes * 2 + 3);
        var store = new FakeStore(content);

        await using var stream = new ImportJobInputStream(content.Length, store.ReadAsync, ChunkBytes);

        await ReadAllAsync(stream);

        Assert.Equal(
            new (long, int)[] { (0, ChunkBytes), (ChunkBytes, ChunkBytes), (ChunkBytes * 2, 3) },
            store.Reads);

        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
        Assert.Equal(3, store.Reads.Count);
    }

    [Fact]
    public async Task Read_DoesNotFetchUntilDataIsNeeded()
    {
        var content = CreateContent(ChunkBytes * 3);
        var store = new FakeStore(content);

        await using var stream = new ImportJobInputStream(content.Length, store.ReadAsync, ChunkBytes);

        Assert.Empty(store.Reads);

        await stream.ReadAsync(new byte[1]);
        await stream.ReadAsync(new byte[ChunkBytes - 1]);

        Assert.Single(store.Reads);
    }

    // This is what happens if the row is deleted during a read. It must not be mistaken
    // for a shorter file, because that would silently import only part of the input.
    [Fact]
    public async Task Read_WhenStoreEndsEarly_ThrowsInsteadOfReportingEndOfFile()
    {
        var store = new FakeStore(CreateContent(ChunkBytes));

        await using var stream = new ImportJobInputStream(ChunkBytes * 2, store.ReadAsync, ChunkBytes);

        var exception = await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(stream));

        Assert.Contains($"{ChunkBytes} of {ChunkBytes * 2} bytes", exception.Message);
    }

    [Fact]
    public async Task Read_WhenCancelled_Throws()
    {
        var store = new FakeStore(CreateContent(ChunkBytes));

        await using var stream = new ImportJobInputStream(ChunkBytes, store.ReadAsync, ChunkBytes);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.ReadAsync(new byte[1], new CancellationToken(canceled: true)).AsTask());

        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task Read_WithEmptyDestination_DoesNotFetch()
    {
        var store = new FakeStore(CreateContent(ChunkBytes));

        await using var stream = new ImportJobInputStream(ChunkBytes, store.ReadAsync, ChunkBytes);

        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.Empty(store.Reads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ImportJobInputStream.MinChunkBytes - 1)]
    public void Constructor_RejectsChunksTooSmallToHoldAByteOrderMark(int chunkBytes)
    {
        var store = new FakeStore([]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ImportJobInputStream(0, store.ReadAsync, chunkBytes));
    }

    [Fact]
    public void Constructor_AcceptsTheSmallestSafeChunk()
    {
        var store = new FakeStore([]);

        using var stream = new ImportJobInputStream(0, store.ReadAsync, ImportJobInputStream.MinChunkBytes);

        Assert.True(stream.CanRead);
    }

    // Blocking a thread on a database read could starve the thread pool.
    [Fact]
    public async Task SynchronousAndPositionalOperations_AreNotSupported()
    {
        var store = new FakeStore(CreateContent(ChunkBytes));

        await using var stream = new ImportJobInputStream(ChunkBytes, store.ReadAsync, ChunkBytes);

        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Length);
    }
}
