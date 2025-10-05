using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace DatumIngest.Model;

/// <summary>
/// A growable byte buffer that stores all reference-type payloads contiguously:
/// UTF-8 strings, float arrays, byte blobs, and encoded images. Combines the
/// roles of the former <c>StringArena</c> and <c>DataArena</c> into a single
/// unified store.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="ColumnBatch"/> owns an <see cref="Arena"/> so that all
/// string, vector, matrix, tensor, image, and byte-array columns share one
/// contiguous buffer rather than individual heap-allocated arrays.
/// </para>
/// <para>
/// Data is appended via typed methods (<see cref="AppendString(string)"/>,
/// <see cref="AppendFloats"/>, <see cref="AppendBytes(ReadOnlySpan{byte})"/>)
/// and later retrieved by offset and length. The backing storage is rented from
/// <see cref="ArrayPool{T}.Shared"/> and returned on <see cref="Dispose"/>.
/// </para>
/// <para>
/// This type is not thread-safe. Parallel decoders should use private arenas
/// and merge via <see cref="CopyFrom"/>.
/// </para>
/// </remarks>
public sealed class Arena : IStringStore, IDisposable
{
    private const int DefaultCapacity = 4096;

    private byte[] _buffer;
    private int _position;
    private bool _disposed;
    private readonly byte _storeId;

    /// <summary>Creates an arena with the specified initial byte capacity.</summary>
    /// <param name="initialCapacity">
    /// Initial size of the backing buffer in bytes. Rounded up by <see cref="ArrayPool{T}"/>.
    /// </param>
    public Arena(int initialCapacity = DefaultCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _storeId = StringStoreRegistry.Register(this);
    }

    // ───────────────────────── IStringStore ─────────────────────────

    /// <inheritdoc />
    public byte StoreId => _storeId;

    /// <inheritdoc />
    public (int P0, int P1) Store(string value)
    {
        var (offset, length) = AppendString(value);
        return (offset, length);
    }

    /// <inheritdoc />
    public string Retrieve(int p0, int p1) => GetString(p0, p1);

    // ───────────────────────── Common ─────────────────────────

    /// <summary>Total bytes written so far.</summary>
    public int BytesWritten => _position;

    // ───────────────────────── String operations ─────────────────────────

    /// <summary>
    /// Appends raw UTF-8 bytes and returns the (offset, length) pair
    /// to embed in a <see cref="DataValue"/>.
    /// </summary>
    /// <param name="utf8">The encoded bytes to append.</param>
    /// <returns>The byte offset and length within this arena.</returns>
    public (int Offset, int Length) AppendUtf8(ReadOnlySpan<byte> utf8)
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
    public (int Offset, int Length) AppendString(string value)
    {
        int maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
        EnsureCapacity(maxByteCount);
        int written = Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_position));
        int offset = _position;
        _position += written;
        return (offset, written);
    }

    /// <summary>Returns the raw UTF-8 bytes for a previously appended string.</summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendUtf8"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendUtf8"/>.</param>
    /// <returns>A span over the stored bytes. Valid only while this arena is alive.</returns>
    public ReadOnlySpan<byte> GetSpan(int offset, int length)
        => _buffer.AsSpan(offset, length);

    /// <summary>
    /// Materialises a stored UTF-8 slice into a managed <see cref="string"/>.
    /// This allocates — use only at output or deep-copy boundaries.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendUtf8"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendUtf8"/>.</param>
    /// <returns>The decoded string.</returns>
    public string GetString(int offset, int length)
        => Encoding.UTF8.GetString(_buffer, offset, length);

    // ───────────────────────── Float operations ─────────────────────────

    /// <summary>
    /// Appends a span of floats and returns the byte offset and element count.
    /// </summary>
    /// <param name="values">The float values to append.</param>
    /// <returns>The byte offset in this arena and the number of elements.</returns>
    public (int Offset, int Count) AppendFloats(ReadOnlySpan<float> values)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values);
        EnsureCapacity(bytes.Length);
        int offset = _position;
        bytes.CopyTo(_buffer.AsSpan(_position));
        _position += bytes.Length;
        return (offset, values.Length);
    }

    /// <summary>Returns a span of floats previously appended.</summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendFloats"/>.</param>
    /// <param name="count">Element count returned by <see cref="AppendFloats"/>.</param>
    /// <returns>A span over the stored floats. Valid only while this arena is alive.</returns>
    public ReadOnlySpan<float> GetFloats(int offset, int count)
        => MemoryMarshal.Cast<byte, float>(_buffer.AsSpan(offset, count * sizeof(float)));

    /// <summary>
    /// Copies a span of floats from the arena into a newly allocated <see cref="float"/> array.
    /// Used at materialisation boundaries where the data must outlive the arena.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendFloats"/>.</param>
    /// <param name="count">Element count returned by <see cref="AppendFloats"/>.</param>
    /// <returns>A new array containing the float values.</returns>
    public float[] MaterializeFloats(int offset, int count)
        => GetFloats(offset, count).ToArray();

    // ───────────────────────── Byte operations ─────────────────────────

    /// <summary>
    /// Appends raw bytes (for <see cref="DataKind.UInt8Array"/>, <see cref="DataKind.Image"/>)
    /// and returns the byte offset and length.
    /// </summary>
    /// <param name="bytes">The bytes to append.</param>
    /// <returns>The byte offset and length within this arena.</returns>
    public (int Offset, int Length) AppendBytes(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        int offset = _position;
        bytes.CopyTo(_buffer.AsSpan(_position));
        _position += bytes.Length;
        return (offset, bytes.Length);
    }

    /// <summary>Returns raw bytes previously appended.</summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendBytes(ReadOnlySpan{byte})"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendBytes(ReadOnlySpan{byte})"/>.</param>
    /// <returns>A span over the stored bytes. Valid only while this arena is alive.</returns>
    public ReadOnlySpan<byte> GetBytes(int offset, int length)
        => _buffer.AsSpan(offset, length);

    /// <summary>
    /// Copies bytes from the arena into a newly allocated <see cref="byte"/> array.
    /// Used at materialisation boundaries where the data must outlive the arena.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendBytes(ReadOnlySpan{byte})"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendBytes(ReadOnlySpan{byte})"/>.</param>
    /// <returns>A new array containing the bytes.</returns>
    public byte[] MaterializeBytes(int offset, int length)
        => GetBytes(offset, length).ToArray();

    // ───────────────────────── Merging ─────────────────────────

    /// <summary>
    /// Bulk-copies all bytes from <paramref name="source"/> into this arena,
    /// returning the base offset at which the copied region starts.
    /// Callers must adjust individual DataValue offsets by this base.
    /// </summary>
    /// <param name="source">The arena whose contents to copy.</param>
    /// <returns>The base offset in this arena where the copy begins.</returns>
    public int CopyFrom(Arena source)
    {
        ReadOnlySpan<byte> data = source._buffer.AsSpan(0, source._position);
        EnsureCapacity(data.Length);
        int baseOffset = _position;
        data.CopyTo(_buffer.AsSpan(_position));
        _position += data.Length;
        return baseOffset;
    }

    // ───────────────────────── Disposal ─────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StringStoreRegistry.Deregister(_storeId);
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
