using System.Buffers;
using System.Runtime.InteropServices;

namespace DatumIngest.Model;

/// <summary>
/// A growable byte buffer that stores binary blobs (float arrays, byte arrays,
/// nested <see cref="DataValue"/> arrays) contiguously.  Works identically to
/// <see cref="StringArena"/> but for non-string reference-type payloads.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="ColumnBatch"/> owns a <see cref="DataArena"/> so that vector,
/// matrix, tensor, image, and UInt8Array columns can store their backing data
/// in one contiguous buffer rather than individual heap-allocated arrays.
/// </para>
/// <para>
/// Data is written as raw bytes via <see cref="AppendFloats"/> or
/// <see cref="AppendBytes"/> and later retrieved by offset and element count.
/// </para>
/// <para>
/// This type is not thread-safe.  Parallel decoders should use private arenas
/// and merge via <see cref="CopyFrom"/>.
/// </para>
/// </remarks>
public sealed class DataArena : IDisposable
{
    private const int DefaultCapacity = 4096;

    private byte[] _buffer;
    private int _position;
    private bool _disposed;

    /// <summary>Creates an arena with the specified initial byte capacity.</summary>
    /// <param name="initialCapacity">
    /// Initial size of the backing buffer in bytes.  Rounded up by <see cref="ArrayPool{T}"/>.
    /// </param>
    public DataArena(int initialCapacity = DefaultCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    /// <summary>Total bytes written so far.</summary>
    public int BytesWritten => _position;

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

    /// <summary>Returns a span of floats previously appended.</summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendFloats"/>.</param>
    /// <param name="count">Element count returned by <see cref="AppendFloats"/>.</param>
    /// <returns>A span over the stored floats.  Valid only while this arena is alive.</returns>
    public ReadOnlySpan<float> GetFloats(int offset, int count)
        => MemoryMarshal.Cast<byte, float>(_buffer.AsSpan(offset, count * sizeof(float)));

    /// <summary>Returns raw bytes previously appended.</summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendBytes"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendBytes"/>.</param>
    /// <returns>A span over the stored bytes.  Valid only while this arena is alive.</returns>
    public ReadOnlySpan<byte> GetBytes(int offset, int length)
        => _buffer.AsSpan(offset, length);

    /// <summary>
    /// Copies a span of floats from the arena into a newly allocated <see cref="float"/> array.
    /// Used at materialisation boundaries where the data must outlive the arena.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendFloats"/>.</param>
    /// <param name="count">Element count returned by <see cref="AppendFloats"/>.</param>
    /// <returns>A new array containing the float values.</returns>
    public float[] MaterializeFloats(int offset, int count)
        => GetFloats(offset, count).ToArray();

    /// <summary>
    /// Copies bytes from the arena into a newly allocated <see cref="byte"/> array.
    /// Used at materialisation boundaries where the data must outlive the arena.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendBytes"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendBytes"/>.</param>
    /// <returns>A new array containing the bytes.</returns>
    public byte[] MaterializeBytes(int offset, int length)
        => GetBytes(offset, length).ToArray();

    /// <summary>
    /// Bulk-copies all bytes from <paramref name="source"/> into this arena,
    /// returning the base offset at which the copied region starts.
    /// </summary>
    /// <param name="source">The arena whose contents to copy.</param>
    /// <returns>The base offset in this arena where the copy begins.</returns>
    public int CopyFrom(DataArena source)
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
