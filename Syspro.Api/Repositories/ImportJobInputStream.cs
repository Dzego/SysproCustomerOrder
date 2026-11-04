namespace Syspro.Api.Repositories;

// Reads the bytes starting at the given zero-based offset into the destination, and
// returns how many were read. Returns zero if no bytes exist at that offset.
public delegate Task<int> ImportJobInputChunkReader(
    long offset,
    Memory<byte> destination,
    CancellationToken cancellationToken);

// Reads a stored job input one chunk at a time, using a single reusable buffer. Each
// chunk is a separate database query that has finished before the caller receives
// any data, so no reader is left open while the caller uses the same connection.
public sealed class ImportJobInputStream : Stream
{
    public const int DefaultChunkBytes = 1024 * 1024;

    // StreamReader only recognizes a byte order mark from its first read, and the longest
    // is four bytes, so a smaller chunk could change how the input is decoded.
    public const int MinChunkBytes = 4;

    private readonly long _length;
    private readonly int _chunkBytes;
    private readonly ImportJobInputChunkReader _readChunk;

    private byte[] _buffer = [];
    private int _bufferOffset;
    private int _bufferCount;
    private long _bytesFetched;

    public ImportJobInputStream(
        long length,
        ImportJobInputChunkReader readChunk,
        int chunkBytes = DefaultChunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, MinChunkBytes);

        _length = length;
        _readChunk = readChunk;
        _chunkBytes = chunkBytes;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_bufferOffset == _bufferCount && !await FetchNextChunkAsync(cancellationToken))
        {
            return 0;
        }

        var count = Math.Min(buffer.Length, _bufferCount - _bufferOffset);

        _buffer.AsMemory(_bufferOffset, count).CopyTo(buffer);
        _bufferOffset += count;

        return count;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    // A database read must not block a thread.
    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Use ReadAsync.");
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    private async Task<bool> FetchNextChunkAsync(CancellationToken cancellationToken)
    {
        var remaining = _length - _bytesFetched;

        if (remaining == 0)
        {
            return false;
        }

        if (_buffer.Length == 0)
        {
            _buffer = new byte[(int)Math.Min(_chunkBytes, _length)];
        }

        var requested = (int)Math.Min(_buffer.Length, remaining);

        var received = await _readChunk(
            _bytesFetched,
            _buffer.AsMemory(0, requested),
            cancellationToken);

        // Ending early must not look like a shorter, valid file.
        if (received <= 0)
        {
            throw new IOException(
                $"The stored import input ended after {_bytesFetched} of {_length} bytes.");
        }

        _bufferOffset = 0;
        _bufferCount = received;
        _bytesFetched += received;

        return true;
    }
}
