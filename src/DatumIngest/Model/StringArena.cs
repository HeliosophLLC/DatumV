using System.Buffers;
using System.Text;

namespace DatumIngest.Model;

/// <summary>
/// A growable byte buffer that stores UTF-8 encoded strings contiguously.
/// Values are appended via <see cref="Append(ReadOnlySpan{byte})"/> and later
/// retrieved by offset and length.  The backing storage is rented from
/// <see cref="ArrayPool{T}.Shared"/> and returned on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="ColumnBatch"/> owns a <see cref="StringArena"/> so that all
/// string columns in the batch share one contiguous buffer.  Column decoders write
/// raw UTF-8 slices during decode, and <see cref="DataValue"/> stores only
/// (offset, length) pairs — deferring actual <see cref="string"/> allocation until
/// the value crosses a materialisation boundary (display, spill, or deep-copy).
/// </para>
/// <para>
/// This type is not thread-safe.  Parallel column decoders should each use a
/// private arena and merge afterwards via <see cref="CopyFrom"/>.
/// </para>
/// </remarks>
public sealed class StringArena : IDisposable
{
    private const int DefaultCapacity = 4096;

    private byte[] _buffer;
    private int _position;
    private bool _disposed;

    /// <summary>Creates an arena with the specified initial byte capacity.</summary>
    /// <param name="initialCapacity">
    /// Initial size of the backing buffer in bytes.  Rounded up by <see cref="ArrayPool{T}"/>.
    /// </param>
    public StringArena(int initialCapacity = DefaultCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    /// <summary>Total bytes written so far.</summary>
    public int BytesWritten => _position;

    /// <summary>
    /// Appends raw UTF-8 bytes to the arena and returns the (offset, length) pair
    /// to embed in a <see cref="DataValue"/>.
    /// </summary>
    /// <param name="utf8">The encoded bytes to append.</param>
    /// <returns>The byte offset and length within this arena.</returns>
    public (int Offset, int Length) Append(ReadOnlySpan<byte> utf8)
    {
        EnsureCapacity(utf8.Length);
        int offset = _position;
        utf8.CopyTo(_buffer.AsSpan(_position));
        _position += utf8.Length;
        return (offset, utf8.Length);
    }

    /// <summary>
    /// Appends a managed <see cref="string"/> by encoding it as UTF-8.
    /// </summary>
    /// <param name="value">The string to append.</param>
    /// <returns>The byte offset and length within this arena.</returns>
    public (int Offset, int Length) Append(string value)
    {
        int maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
        EnsureCapacity(maxByteCount);
        int written = Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_position));
        int offset = _position;
        _position += written;
        return (offset, written);
    }

    /// <summary>Returns the raw UTF-8 bytes for a previously appended value.</summary>
    /// <param name="offset">Byte offset returned by <see cref="Append(ReadOnlySpan{byte})"/>.</param>
    /// <param name="length">Byte length returned by <see cref="Append(ReadOnlySpan{byte})"/>.</param>
    /// <returns>A span over the stored bytes.  Valid only while this arena is alive.</returns>
    public ReadOnlySpan<byte> GetSpan(int offset, int length)
        => _buffer.AsSpan(offset, length);

    /// <summary>
    /// Materialises a stored UTF-8 slice into a managed <see cref="string"/>.
    /// This allocates — use only at output or deep-copy boundaries.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="Append(ReadOnlySpan{byte})"/>.</param>
    /// <param name="length">Byte length returned by <see cref="Append(ReadOnlySpan{byte})"/>.</param>
    /// <returns>The decoded string.</returns>
    public string GetString(int offset, int length)
        => Encoding.UTF8.GetString(_buffer, offset, length);

    /// <summary>
    /// Bulk-copies all bytes from <paramref name="source"/> into this arena,
    /// returning the base offset at which the copied region starts.
    /// Callers must adjust individual DataValue offsets by this base.
    /// </summary>
    /// <param name="source">The arena whose contents to copy.</param>
    /// <returns>The base offset in this arena where the copy begins.</returns>
    public int CopyFrom(StringArena source)
    {
        ReadOnlySpan<byte> data = source._buffer.AsSpan(0, source._position);
        EnsureCapacity(data.Length);
        int baseOffset = _position;
        data.CopyTo(_buffer.AsSpan(_position));
        _position += data.Length;
        return baseOffset;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
        _position = 0;
    }

    private void EnsureCapacity(int additionalBytes)
    {
        int required = _position + additionalBytes;
        if (required <= _buffer.Length) return;

        int newCapacity = Math.Max(_buffer.Length * 2, required);
        byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);
        _buffer.AsSpan(0, _position).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }
}
