using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace DatumIngest.Model;

/// <summary>
/// In-memory <see cref="IValueStore"/> backed by a single managed <see cref="byte"/> array.
/// A drop-in alternative to <see cref="Arena"/> for testing or lightweight scenarios that
/// don't need anonymous virtual-memory reservations, file backing, or pool management.
/// </summary>
/// <remarks>
/// <para>
/// The backing array starts at the requested initial capacity and grows by
/// reallocation (doubling, or larger if a single write demands it) when a write would
/// exceed the current capacity. Because growth reallocates, <see cref="ReadOnlySpan{T}"/>
/// values returned by <see cref="RetrieveUtf8Span"/> and similar accessors are only valid
/// until the next write — unlike <see cref="Arena"/>, which keeps its base pointer stable.
/// </para>
/// <para>
/// Writes are serialized via an internal lock; reads are not synchronized.
/// </para>
/// </remarks>
public sealed class ByteArrayValueStore : IValueStore
{
    /// <summary>Default initial capacity in bytes.</summary>
    public const int DefaultCapacity = 4096;

    private readonly Lock _writeLock = new();
    private byte[] _buffer;
    private int _position;
    private List<object>? _objects;

    /// <summary>Creates a store with the given initial byte capacity.</summary>
    /// <param name="initialCapacity">Initial size of the backing array. Floored at 0.</param>
    public ByteArrayValueStore(int initialCapacity = DefaultCapacity)
    {
        _buffer = new byte[Math.Max(0, initialCapacity)];
    }

    /// <summary>Total bytes written so far.</summary>
    public int BytesWritten => _position;

    /// <summary>Current capacity of the backing array in bytes.</summary>
    public int Capacity => _buffer.Length;

    // ───────────────────────── IValueStore ─────────────────────────

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreString(string value)
    {
        int maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[]? rented = null;
        Span<byte> temp = maxByteCount <= 256
            ? stackalloc byte[maxByteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(maxByteCount));

        int written = Encoding.UTF8.GetBytes(value, temp);
        var result = WriteBytes(temp[..written]);

        if (rented is not null)
            ArrayPool<byte>.Shared.Return(rented);

        return result;
    }

    /// <inheritdoc />
    public string RetrieveString(ArenaOffset p0, ArenaLength p1)
    {
        int offset = checked((int)p0.Value);
        int length = checked((int)p1.Value);
        return Encoding.UTF8.GetString(_buffer, offset, length);
    }

    /// <inheritdoc />
    public ReadOnlySpan<byte> RetrieveUtf8Span(ArenaOffset p0, ArenaLength p1)
        => GetReadSpan(p0.Value, checked((int)p1.Value));

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreUtf8(ReadOnlySpan<byte> utf8)
        => WriteBytes(utf8);

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreChars(ReadOnlySpan<char> chars)
    {
        int maxByteCount = Encoding.UTF8.GetMaxByteCount(chars.Length);
        byte[]? rented = null;
        Span<byte> temp = maxByteCount <= 256
            ? stackalloc byte[maxByteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(maxByteCount));

        int written = Encoding.UTF8.GetBytes(chars, temp);
        var result = WriteBytes(temp[..written]);

        if (rented is not null)
            ArrayPool<byte>.Shared.Return(rented);

        return result;
    }

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreBytes(ReadOnlySpan<byte> bytes)
        => WriteBytes(bytes);

    /// <inheritdoc />
    public byte[] RetrieveBytes(ArenaOffset p0, ArenaLength p1)
        => GetReadSpan(p0.Value, checked((int)p1.Value)).ToArray();

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreFloats(ReadOnlySpan<float> floats)
    {
        var (offset, _) = WriteBytes(MemoryMarshal.AsBytes(floats));
        return (offset, new ArenaLength(floats.Length));
    }

    /// <inheritdoc />
    public float[] RetrieveFloats(ArenaOffset p0, ArenaLength p1)
    {
        int count = checked((int)p1.Value);
        ReadOnlySpan<byte> bytes = GetReadSpan(p0.Value, count * sizeof(float));
        float[] result = new float[count];
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(result);
        return result;
    }

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreTensor(ReadOnlySpan<float> data, ReadOnlySpan<int> shape)
    {
        int shapeBytes = (1 + shape.Length) * sizeof(int);
        int dataBytes = data.Length * sizeof(float);
        int totalBytes = shapeBytes + dataBytes;

        byte[]? rented = null;
        Span<byte> temp = totalBytes <= 1024
            ? stackalloc byte[totalBytes]
            : (rented = ArrayPool<byte>.Shared.Rent(totalBytes));

        Span<byte> dest = temp[..totalBytes];
        MemoryMarshal.Write(dest, shape.Length);
        dest = dest[sizeof(int)..];
        MemoryMarshal.AsBytes(shape).CopyTo(dest);
        dest = dest[(shape.Length * sizeof(int))..];
        MemoryMarshal.AsBytes(data).CopyTo(dest);

        var result = WriteBytes(temp[..totalBytes]);

        if (rented is not null)
            ArrayPool<byte>.Shared.Return(rented);

        return result;
    }

    /// <inheritdoc />
    public float[] RetrieveTensor(ArenaOffset p0, ArenaLength p1, out int[] shape)
    {
        int length = checked((int)p1.Value);
        ReadOnlySpan<byte> region = GetReadSpan(p0.Value, length);

        int rank = MemoryMarshal.Read<int>(region);
        region = region[sizeof(int)..];

        shape = new int[rank];
        MemoryMarshal.Cast<byte, int>(region[..(rank * sizeof(int))]).CopyTo(shape);
        region = region[(rank * sizeof(int))..];

        int floatCount = region.Length / sizeof(float);
        float[] data = new float[floatCount];
        MemoryMarshal.Cast<byte, float>(region).CopyTo(data);
        return data;
    }

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreDataValues(ReadOnlySpan<DataValue> values)
    {
        var (offset, _) = WriteBytes(MemoryMarshal.AsBytes(values));
        return (offset, new ArenaLength(values.Length));
    }

    /// <inheritdoc />
    public DataValue[] RetrieveDataValues(ArenaOffset p0, ArenaLength p1)
    {
        int count = checked((int)p1.Value);
        ReadOnlySpan<byte> bytes = GetReadSpan(p0.Value, count * DataValue.SizeBytes);
        DataValue[] result = new DataValue[count];
        MemoryMarshal.Cast<byte, DataValue>(bytes).CopyTo(result);
        return result;
    }

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreObject(object value)
    {
        lock (_writeLock)
        {
            _objects ??= [];
            _objects.Add(value);
            return (new ArenaOffset(_objects.Count - 1), ArenaLength.Zero);
        }
    }

    /// <inheritdoc />
    public object RetrieveObject(ArenaOffset p0, ArenaLength p1)
    {
        int index = checked((int)p0.Value);
        if (_objects is null || (uint)index >= (uint)_objects.Count)
            throw new InvalidOperationException($"No object stored at index {index}.");
        return _objects[index];
    }

    // ───────────────────────── Internals ─────────────────────────

    private (ArenaOffset Offset, ArenaLength Length) WriteBytes(ReadOnlySpan<byte> data)
    {
        lock (_writeLock)
        {
            EnsureCapacity(data.Length);
            int offset = _position;
            data.CopyTo(_buffer.AsSpan(offset, data.Length));
            _position += data.Length;
            return (new ArenaOffset(offset), new ArenaLength(data.Length));
        }
    }

    private void EnsureCapacity(int additionalBytes)
    {
        long required = (long)_position + additionalBytes;
        if (required <= _buffer.Length) return;

        long target = Math.Max((long)_buffer.Length * 2, required);
        if (target > int.MaxValue)
            throw new InvalidOperationException(
                $"ByteArrayValueStore needs {target:N0} bytes but a single byte[] is capped at {int.MaxValue:N0}.");

        byte[] grown = new byte[(int)target];
        Buffer.BlockCopy(_buffer, 0, grown, 0, _position);
        _buffer = grown;
    }

    private ReadOnlySpan<byte> GetReadSpan(long offset, int length)
        => _buffer.AsSpan(checked((int)offset), length);
}
