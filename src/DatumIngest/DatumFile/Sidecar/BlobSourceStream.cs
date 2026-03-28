namespace DatumIngest.DatumFile.Sidecar;

/// <summary>
/// Adapts a slice of an <see cref="IBlobSource"/> as a read-only seekable
/// <see cref="Stream"/>. Used by consumers that need a managed-Stream input
/// (typically FFmpeg's <c>IOContext.ReadStream</c>) to drive decoding directly
/// from a memory-mapped <c>.datum-blob</c> region without copying the entire
/// payload to a managed buffer.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lifetime.</strong> Each instance must own its own logical reader.
/// Two FFmpeg decoders reading the same underlying sidecar can construct two
/// <see cref="BlobSourceStream"/>s over the same <see cref="IBlobSource"/>;
/// the source itself is shared (refcounted by the catalog) but the
/// <see cref="Position"/> on each stream is independent.
/// </para>
/// <para>
/// <strong>Ownership.</strong> This stream does not own the underlying
/// <see cref="IBlobSource"/>; disposing it does not dispose the source.
/// </para>
/// </remarks>
public sealed class BlobSourceStream : Stream
{
    private readonly IBlobSource _source;
    private readonly long _baseOffset;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Creates a stream view over <paramref name="length"/> bytes starting at
    /// <paramref name="baseOffset"/> in <paramref name="source"/>.
    /// </summary>
    /// <param name="source">Underlying blob source; must outlive this stream.</param>
    /// <param name="baseOffset">Absolute offset where the slice begins.</param>
    /// <param name="length">Slice length in bytes.</param>
    public BlobSourceStream(IBlobSource source, long baseOffset, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (baseOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseOffset), baseOffset, "must be non-negative.");
        }
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "must be non-negative.");
        }
        _source = source;
        _baseOffset = baseOffset;
        _length = length;
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set
        {
            ThrowIfDisposed();
            if (value < 0 || value > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"position must be in [0, {_length}].");
            }
            _position = value;
        }
    }

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), $"offset={offset}, count={count}, buffer.Length={buffer.Length}.");
        }
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        long remaining = _length - _position;
        if (remaining <= 0) return 0;
        int toRead = (int)Math.Min(remaining, buffer.Length);
        if (toRead == 0) return 0;
        ReadOnlySpan<byte> src = _source.Read(_baseOffset + _position, toRead);
        src.CopyTo(buffer);
        _position += toRead;
        return toRead;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "invalid SeekOrigin."),
        };
        if (target < 0 || target > _length)
        {
            throw new IOException(
                $"Seek to {target} out of slice bounds [0, {_length}] (origin={origin}, offset={offset}).");
        }
        _position = target;
        return _position;
    }

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("BlobSourceStream is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("BlobSourceStream is read-only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BlobSourceStream));
        }
    }
}
