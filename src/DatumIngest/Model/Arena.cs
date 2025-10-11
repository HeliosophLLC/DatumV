using System.Buffers;
using System.IO.MemoryMappedFiles;
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
/// and later retrieved by offset and length. The backing storage is an anonymous
/// memory-mapped region managed by the OS. Growth allocates a new mapping and
/// copies existing data.
/// </para>
/// <para>
/// All writes are serialized via an internal lock, making concurrent appends
/// safe from parallel operators. Reads are lock-free since they only access
/// data at offsets returned by prior writes.
/// </para>
/// </remarks>
public sealed class Arena : IValueStore, IDisposable
{
    private const int DefaultCapacity = 4096;

    private readonly Lock _writeLock = new();
    private MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _accessor;
    private unsafe byte* _pointer;
    private int _capacity;
    private int _position;
    private bool _disposed;

    // Side-list for managed objects that cannot be byte-serialized (e.g. ImageHandle).
    // Accessed under _writeLock for thread safety.
    private List<object>? _objects;

    /// <summary>Creates an arena with the specified initial byte capacity.</summary>
    /// <param name="initialCapacity">
    /// Initial size of the anonymous memory-mapped region in bytes.
    /// </param>
    public unsafe Arena(int initialCapacity = DefaultCapacity)
    {
        _capacity = Math.Max(initialCapacity, DefaultCapacity);
        CreateMapping(_capacity, out _mmf, out _accessor, out _pointer);
    }

    // ───────────────────────── IValueStore ─────────────────────────

    /// <inheritdoc />
    public (int P0, int P1) StoreString(string value)
    {
        var (offset, length) = AppendString(value);
        return (offset, length);
    }

    /// <inheritdoc />
    public string RetrieveString(int p0, int p1) => GetString(p0, p1);

    /// <inheritdoc />
    public ReadOnlySpan<byte> RetrieveUtf8Span(int p0, int p1) => GetSpan(p0, p1);

    /// <inheritdoc />
    public (int P0, int P1) StoreUtf8(ReadOnlySpan<byte> utf8) => AppendUtf8(utf8);

    /// <inheritdoc />
    public (int P0, int P1) StoreChars(ReadOnlySpan<char> chars) => AppendChars(chars);

    /// <inheritdoc />
    public (int P0, int P1) StoreBytes(ReadOnlySpan<byte> bytes)
    {
        var (offset, length) = AppendBytes(bytes);
        return (offset, length);
    }

    /// <inheritdoc />
    public byte[] RetrieveBytes(int p0, int p1) => MaterializeBytes(p0, p1);

    /// <inheritdoc />
    public (int P0, int P1) StoreFloats(ReadOnlySpan<float> floats)
    {
        var (offset, count) = AppendFloats(floats);
        return (offset, count);
    }

    /// <inheritdoc />
    public float[] RetrieveFloats(int p0, int p1) => MaterializeFloats(p0, p1);

    /// <inheritdoc />
    public (int P0, int P1) StoreTensor(ReadOnlySpan<float> data, ReadOnlySpan<int> shape)
    {
        var (offset, length) = AppendTensor(data, shape);
        return (offset, length);
    }

    /// <inheritdoc />
    public float[] RetrieveTensor(int p0, int p1, out int[] shape) => MaterializeTensor(p0, p1, out shape);

    /// <inheritdoc />
    public (int P0, int P1) StoreDataValues(ReadOnlySpan<DataValue> values)
    {
        var (offset, count) = AppendDataValues(values);
        return (offset, count);
    }

    /// <inheritdoc />
    public DataValue[] RetrieveDataValues(int p0, int p1) => MaterializeDataValues(p0, p1);

    /// <inheritdoc />
    public (int P0, int P1) StoreObject(object value)
    {
        lock (_writeLock)
        {
            _objects ??= [];
            _objects.Add(value);
            return (_objects.Count - 1, 0);
        }
    }

    /// <inheritdoc />
    public object RetrieveObject(int p0, int p1)
    {
        if (_objects is null || (uint)p0 >= (uint)_objects.Count)
            throw new InvalidOperationException($"No object stored at index {p0}.");
        return _objects[p0];
    }

    // ───────────────────────── Common ─────────────────────────

    /// <summary>Total bytes written so far.</summary>
    public int BytesWritten => _position;

    // ───────────────────────── Core write path ─────────────────────────

    /// <summary>
    /// Appends raw bytes to the arena under a lock. All typed append methods
    /// flow through this single write path.
    /// </summary>
    /// <returns>The byte offset and length of the written region.</returns>
    private (int Offset, int Length) WriteBytes(ReadOnlySpan<byte> data)
    {
        lock (_writeLock)
        {
            EnsureCapacity(data.Length);
            int offset = _position;
            data.CopyTo(GetSpanForWrite(offset, data.Length));
            _position += data.Length;
            return (offset, data.Length);
        }
    }

    // ───────────────────────── String operations ─────────────────────────

    /// <summary>
    /// Appends raw UTF-8 bytes and returns the (offset, length) pair
    /// to embed in a <see cref="DataValue"/>.
    /// </summary>
    /// <param name="utf8">The encoded bytes to append.</param>
    /// <returns>The byte offset and length within this arena.</returns>
    public (int Offset, int Length) AppendUtf8(ReadOnlySpan<byte> utf8)
        => WriteBytes(utf8);

    /// <summary>
    /// Appends a managed <see cref="string"/> by encoding it as UTF-8.
    /// </summary>
    /// <param name="value">The string to append.</param>
    /// <returns>The byte offset and length within this arena.</returns>
    public (int Offset, int Length) AppendString(string value)
    {
        int maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);

        // Encode to a temporary buffer, then write the exact bytes.
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

    /// <summary>
    /// Appends a span of chars by encoding as UTF-8, without allocating a managed string.
    /// </summary>
    /// <param name="chars">The char span to encode and append.</param>
    /// <returns>The byte offset and length within this arena.</returns>
    public (int Offset, int Length) AppendChars(ReadOnlySpan<char> chars)
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

    /// <summary>Returns the raw UTF-8 bytes for a previously appended string.</summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendUtf8"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendUtf8"/>.</param>
    /// <returns>A span over the stored bytes. Valid only while this arena is alive.</returns>
    public ReadOnlySpan<byte> GetSpan(int offset, int length)
        => GetSpanForRead(offset, length);

    /// <summary>
    /// Materialises a stored UTF-8 slice into a managed <see cref="string"/>.
    /// This allocates — use only at output or deep-copy boundaries.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendUtf8"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendUtf8"/>.</param>
    /// <returns>The decoded string.</returns>
    public unsafe string GetString(int offset, int length)
        => Encoding.UTF8.GetString(_pointer + offset, length);

    // ───────────────────────── Float operations ─────────────────────────

    /// <summary>
    /// Appends a span of floats and returns the byte offset and element count.
    /// </summary>
    /// <param name="values">The float values to append.</param>
    /// <returns>The byte offset in this arena and the number of elements.</returns>
    public (int Offset, int Count) AppendFloats(ReadOnlySpan<float> values)
    {
        var (offset, _) = WriteBytes(MemoryMarshal.AsBytes(values));
        return (offset, values.Length);
    }

    /// <summary>Returns a span of floats previously appended.</summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendFloats"/>.</param>
    /// <param name="count">Element count returned by <see cref="AppendFloats"/>.</param>
    /// <returns>A span over the stored floats. Valid only while this arena is alive.</returns>
    public ReadOnlySpan<float> GetFloats(int offset, int count)
        => MemoryMarshal.Cast<byte, float>(GetSpanForRead(offset, count * sizeof(float)));

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
        => WriteBytes(bytes);

    /// <summary>Returns raw bytes previously appended.</summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendBytes(ReadOnlySpan{byte})"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendBytes(ReadOnlySpan{byte})"/>.</param>
    /// <returns>A span over the stored bytes. Valid only while this arena is alive.</returns>
    public ReadOnlySpan<byte> GetBytes(int offset, int length)
        => GetSpanForRead(offset, length);

    /// <summary>
    /// Copies bytes from the arena into a newly allocated <see cref="byte"/> array.
    /// Used at materialisation boundaries where the data must outlive the arena.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendBytes(ReadOnlySpan{byte})"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendBytes(ReadOnlySpan{byte})"/>.</param>
    /// <returns>A new array containing the bytes.</returns>
    public byte[] MaterializeBytes(int offset, int length)
        => GetBytes(offset, length).ToArray();

    // ───────────────────────── DataValue array operations ─────────────────────────

    /// <summary>
    /// Appends a <see cref="DataValue"/> array as raw bytes (20 bytes per element).
    /// </summary>
    /// <param name="values">The DataValue array to append.</param>
    /// <returns>The byte offset and element count within this arena.</returns>
    public (int Offset, int Count) AppendDataValues(ReadOnlySpan<DataValue> values)
    {
        var (offset, _) = WriteBytes(MemoryMarshal.AsBytes(values));
        return (offset, values.Length);
    }

    /// <summary>
    /// Retrieves a <see cref="DataValue"/> array previously stored via <see cref="AppendDataValues"/>.
    /// Allocates a new array — use at materialisation boundaries only.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendDataValues"/>.</param>
    /// <param name="count">Element count returned by <see cref="AppendDataValues"/>.</param>
    /// <returns>A new array containing the DataValues.</returns>
    public DataValue[] MaterializeDataValues(int offset, int count)
    {
        ReadOnlySpan<byte> bytes = GetSpanForRead(offset, count * 20);
        DataValue[] result = new DataValue[count];
        MemoryMarshal.Cast<byte, DataValue>(bytes).CopyTo(result);
        return result;
    }

    // ───────────────────────── Tensor operations ─────────────────────────

    /// <summary>
    /// Appends a tensor as a single contiguous region: shape prefix followed by float data.
    /// Layout: <c>[rank:int32][dim0:int32]...[dimN:int32][float0:float32]...[floatM:float32]</c>.
    /// </summary>
    /// <param name="data">The flat float data.</param>
    /// <param name="shape">The dimension sizes.</param>
    /// <returns>The byte offset and total byte length of the combined region.</returns>
    public (int Offset, int Length) AppendTensor(ReadOnlySpan<float> data, ReadOnlySpan<int> shape)
    {
        int shapeBytes = (1 + shape.Length) * sizeof(int); // rank + dimensions
        int dataBytes = data.Length * sizeof(float);
        int totalBytes = shapeBytes + dataBytes;

        // Build the tensor layout in a temporary buffer, then write atomically.
        byte[]? rented = null;
        Span<byte> temp = totalBytes <= 1024
            ? stackalloc byte[totalBytes]
            : (rented = ArrayPool<byte>.Shared.Rent(totalBytes));

        Span<byte> dest = temp[..totalBytes];

        // Write rank
        MemoryMarshal.Write(dest, shape.Length);
        dest = dest[sizeof(int)..];

        // Write shape dimensions
        MemoryMarshal.AsBytes(shape).CopyTo(dest);
        dest = dest[(shape.Length * sizeof(int))..];

        // Write float data
        MemoryMarshal.AsBytes(data).CopyTo(dest);

        var result = WriteBytes(temp[..totalBytes]);

        if (rented is not null)
            ArrayPool<byte>.Shared.Return(rented);

        return result;
    }

    /// <summary>
    /// Retrieves a tensor previously stored via <see cref="AppendTensor"/>,
    /// parsing the shape prefix and returning the float data and shape.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendTensor"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendTensor"/>.</param>
    /// <param name="shape">The reconstructed dimension sizes.</param>
    /// <returns>A new float array containing the tensor data.</returns>
    public float[] MaterializeTensor(int offset, int length, out int[] shape)
    {
        ReadOnlySpan<byte> region = GetSpanForRead(offset, length);

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
        ReadOnlySpan<byte> data = source.GetSpanForRead(0, source._position);
        var (offset, _) = WriteBytes(data);
        return offset;
    }

    // ───────────────────────── Disposal ─────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseMapping();
        _position = 0;
    }

    // ───────────────────────── Memory-mapped backing ─────────────────────────

    private static unsafe void CreateMapping(int capacity,
        out MemoryMappedFile mmf, out MemoryMappedViewAccessor accessor, out byte* pointer)
    {
        mmf = MemoryMappedFile.CreateNew(null, capacity, MemoryMappedFileAccess.ReadWrite);
        accessor = mmf.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
        pointer = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
    }

    private unsafe void ReleaseMapping()
    {
        if (_pointer != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        _accessor.Dispose();
        _mmf.Dispose();
    }

    /// <summary>
    /// Grows the backing mapping. Must be called under <see cref="_writeLock"/>.
    /// </summary>
    private unsafe void EnsureCapacity(int additionalBytes)
    {
        int required = _position + additionalBytes;
        if (required <= _capacity) return;

        int newCapacity = Math.Max(_capacity * 2, required);
        CreateMapping(newCapacity, out var newMmf, out var newAccessor, out var newPointer);

        // Copy existing data to the new mapping.
        new ReadOnlySpan<byte>(_pointer, _position).CopyTo(new Span<byte>(newPointer, newCapacity));

        ReleaseMapping();

        _mmf = newMmf;
        _accessor = newAccessor;
        _pointer = newPointer;
        _capacity = newCapacity;
    }

    private unsafe Span<byte> GetSpanForWrite(int offset, int length)
        => new(_pointer + offset, length);

    private unsafe ReadOnlySpan<byte> GetSpanForRead(int offset, int length)
        => new(_pointer + offset, length);
}
