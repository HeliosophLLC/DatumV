using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using Heliosoph.DatumV.Diagnostics;

namespace Heliosoph.DatumV.Model;

/// <summary>
/// A growable byte buffer that stores all reference-type payloads contiguously:
/// UTF-8 strings, float arrays, byte blobs, and encoded images. Combines the
/// roles of the former <c>StringArena</c> and <c>DataArena</c> into a single
/// unified store.
/// </summary>
/// <remarks>
/// <para>
/// Data is appended via typed methods (<see cref="AppendString(string)"/>,
/// <see cref="AppendFloats"/>, <see cref="AppendBytes(ReadOnlySpan{byte})"/>)
/// and later retrieved by offset and length.
/// </para>
/// <para>
/// <strong>Anonymous backing.</strong> A large virtual-address range (see
/// <see cref="MaxAnonymousReservation"/>) is reserved up front via
/// <see cref="VirtualMemory.Reserve"/>; pages within that range are committed
/// on demand as the arena grows. The base pointer is stable for the arena's
/// lifetime — no remap, no memcpy, no dangling spans. This is what makes
/// parallel scalar dispatch safe to read through a growing arena.
/// </para>
/// <para>
/// <strong>File-backed</strong> arenas (<see cref="CreateFileBacked"/>) use
/// memory-mapped files instead so that bytes persist on disk and the OS can
/// page cold pages out of working set under memory pressure. File-backed
/// growth still remaps; concurrent reads of file-backed arenas are NOT safe
/// against concurrent writes for that reason.
/// </para>
/// <para>
/// All writes are serialized via an internal lock, making concurrent appends
/// safe from parallel operators. Reads are lock-free since they only access
/// data at offsets returned by prior writes.
/// </para>
/// </remarks>
public sealed class Arena : IValueStore, IDisposable
{
    /// <summary>
    /// Gets the default initial capacity for arenas.
    /// </summary>
    public const int DefaultCapacity = 1024 * 1024; // 1 MB — initial commit; grows on demand within the reservation.

    // Default virtual-address reservation per anonymous arena. The reservation is cheap
    // (VA only — no RAM, no commit charge) and lets the base pointer stay stable across
    // every grow. 8 GB is a hard per-arena cap; on 64-bit hosts with 128 TB user VA we
    // can comfortably reserve thousands of these concurrently. Bump if a single arena
    // ever needs more than 8 GB.
    private const long MaxAnonymousReservation = 8L * 1024 * 1024 * 1024;

    private readonly Lock _writeLock = new();
    private readonly string? _backingFilePath;

    // File-backed only.
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;

    // Anonymous only — null when file-backed; for file-backed _pointer is reused for the MMF view base.
    private long _reservedBytes;

    private unsafe byte* _pointer;
    private long _initialCapacity;
    private long _capacity;
    private long _position;
    private bool _disposed;
    private bool _pooled;
    private int _refCount;

    private static int s_nextId;

    /// <summary>
    /// Process-wide unique identifier assigned at construction. Diagnostic only —
    /// surfaces in <see cref="GetSpanForRead"/> error messages so a stale-DataValue
    /// reference can be correlated with the specific arena that produced/owned it.
    /// </summary>
    public int Id { get; } = System.Threading.Interlocked.Increment(ref s_nextId);

    // Side-list for managed objects that cannot be byte-serialized (e.g. ImageHandle).
    // Accessed under _writeLock for thread safety.
    private List<object>? _objects;

    /// <summary>Creates an anonymous-mmap arena with the specified initial byte capacity.</summary>
    /// <param name="initialCapacity">
    /// Initial size of the anonymous memory-mapped region in bytes.
    /// The mapping is not allocated until the first write.
    /// </param>
    public Arena(long initialCapacity = DefaultCapacity)
        : this(initialCapacity, backingFilePath: null) { }

    /// <summary>
    /// Internal ctor shared by anonymous and file-backed factories. <paramref name="backingFilePath"/>
    /// non-null selects the file-backed mode; the file is created lazily on first write.
    /// </summary>
    private Arena(long initialCapacity, string? backingFilePath)
    {
        _initialCapacity = Math.Max(initialCapacity, (long)DefaultCapacity);
        _backingFilePath = backingFilePath;
    }

    /// <summary>
    /// Creates a file-backed arena. Bytes live in <paramref name="filePath"/>; the OS can page
    /// cold pages out under memory pressure and reload them from the file. Suitable for spill
    /// scenarios where payload bytes need to survive a memory budget without committing process
    /// memory for them. NOT poolable — <see cref="IDisposable.Dispose"/> deletes the file.
    /// </summary>
    /// <param name="filePath">
    /// Absolute path to the backing file. Must not exist (we open with <see cref="FileMode.CreateNew"/>
    /// so a stale file from a crashed process surfaces as an exception rather than silently
    /// resuming with corrupted state).
    /// </param>
    /// <param name="initialCapacity">
    /// Pre-size the file to this many bytes. Generous values reduce growth churn — file-backed
    /// growth requires unmapping → resizing → remapping, more expensive than anonymous growth's
    /// in-process memcpy. Floored at <see cref="DefaultCapacity"/>.
    /// </param>
    public static Arena CreateFileBacked(string filePath, long initialCapacity = DefaultCapacity)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        return new Arena(initialCapacity, filePath);
    }

    /// <summary>Whether the backing memory region has been allocated.</summary>
    public unsafe bool IsAllocated => _backingFilePath is null ? _pointer != null : _mmf is not null;

    /// <summary>
    /// Whether this arena's bytes live in a backing file (<see cref="CreateFileBacked"/>) rather
    /// than an anonymous mmap region. File-backed arenas are not pool-managed; their lifecycle is
    /// owner-disposed.
    /// </summary>
    public bool IsFileBacked => _backingFilePath is not null;

    /// <summary>Whether this arena is currently held in a pool and must not be accessed.</summary>
    public bool Pooled { get => _pooled; private set => _pooled = value; }

    /// <summary>
    /// Gets the number of active references to this arena. Used by the owning batch to determine when to return the arena to the pool.
    /// </summary>
    public int ReferenceCount => _refCount;

    internal void AddReference() => Interlocked.Increment(ref _refCount);

    internal int ReleaseReference()
    {
        return Interlocked.Decrement(ref _refCount);  
    } 

    // ───────────────────────── IValueStore ─────────────────────────

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreString(string value)
    {
        var (offset, length) = AppendString(value);
        return (new ArenaOffset(offset), new ArenaLength(length));
    }

    /// <inheritdoc />
    public string RetrieveString(ArenaOffset p0, ArenaLength p1) => GetString(p0.Value, checked((int)p1.Value));

    /// <inheritdoc />
    public ReadOnlySpan<byte> RetrieveUtf8Span(ArenaOffset p0, ArenaLength p1) => GetSpan(p0.Value, checked((int)p1.Value));

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreUtf8(ReadOnlySpan<byte> utf8)
    {
        var (offset, length) = AppendUtf8(utf8);
        return (new ArenaOffset(offset), new ArenaLength(length));
    }

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreChars(ReadOnlySpan<char> chars)
    {
        var (offset, length) = AppendChars(chars);
        return (new ArenaOffset(offset), new ArenaLength(length));
    }

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreBytes(ReadOnlySpan<byte> bytes)
    {
        var (offset, length) = AppendBytes(bytes);
        return (new ArenaOffset(offset), new ArenaLength(length));
    }

    /// <inheritdoc />
    public byte[] RetrieveBytes(ArenaOffset p0, ArenaLength p1) => MaterializeBytes(p0.Value, checked((int)p1.Value));

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreFloats(ReadOnlySpan<float> floats)
    {
        var (offset, count) = AppendFloats(floats);
        return (new ArenaOffset(offset), new ArenaLength(count));
    }

    /// <inheritdoc />
    public float[] RetrieveFloats(ArenaOffset p0, ArenaLength p1) => MaterializeFloats(p0.Value, checked((int)p1.Value));

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreTensor(ReadOnlySpan<float> data, ReadOnlySpan<int> shape)
    {
        var (offset, length) = AppendTensor(data, shape);
        return (new ArenaOffset(offset), new ArenaLength(length));
    }

    /// <inheritdoc />
    public float[] RetrieveTensor(ArenaOffset p0, ArenaLength p1, out int[] shape) => MaterializeTensor(p0.Value, checked((int)p1.Value), out shape);

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreDataValues(ReadOnlySpan<DataValue> values)
    {
        var (offset, count) = AppendDataValues(values);
        return (new ArenaOffset(offset), new ArenaLength(count));
    }

    /// <inheritdoc />
    public DataValue[] RetrieveDataValues(ArenaOffset p0, ArenaLength p1) => MaterializeDataValues(p0.Value, checked((int)p1.Value));

    /// <inheritdoc />
    public (ArenaOffset P0, ArenaLength P1) StoreObject(object value)
    {
        ThrowIfPooled();
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
        ThrowIfPooled();
        int index = checked((int)p0.Value);
        if (_objects is null || (uint)index >= (uint)_objects.Count)
            throw new InvalidOperationException($"No object stored at index {index}.");
        return _objects[index];
    }

    // ───────────────────────── Common ─────────────────────────

    /// <summary>Total bytes written so far.</summary>
    public long BytesWritten => _position;

    /// <summary>Current capacity of the backing memory-mapped region in bytes, or zero if not yet allocated.</summary>
    public long Capacity => _capacity;

    /// <summary>
    /// Returns a read-only view over a sub-region ("page") of this arena.
    /// All <c>Retrieve*</c> calls on the slice add <paramref name="pageBase"/> to <c>p0</c>
    /// before dereferencing. Used to resolve page-relative offsets copied verbatim from
    /// another arena via <see cref="CopyFrom"/> without rewriting the bytes.
    /// </summary>
    /// <param name="pageBase">Byte offset at which the page begins within this arena.</param>
    /// <param name="pageLength">Length of the page in bytes.</param>
    public ArenaSlice Slice(long pageBase, long pageLength)
        => new(this, pageBase, pageLength);

    // ───────────────────────── Core write path ─────────────────────────

    /// <summary>
    /// Appends raw bytes to the arena under a lock. All typed append methods
    /// flow through this single write path.
    /// </summary>
    /// <returns>The byte offset and length of the written region.</returns>
    private (long Offset, int Length) WriteBytes(ReadOnlySpan<byte> data)
    {
        ThrowIfPooled();
        lock (_writeLock)
        {
            EnsureCapacity(data.Length);
            long offset = _position;
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
    public (long Offset, int Length) AppendUtf8(ReadOnlySpan<byte> utf8)
        => WriteBytes(utf8);

    /// <summary>
    /// Appends a managed <see cref="string"/> by encoding it as UTF-8.
    /// </summary>
    /// <param name="value">The string to append.</param>
    /// <returns>The byte offset and length within this arena.</returns>
    public (long Offset, int Length) AppendString(string value)
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
    public (long Offset, int Length) AppendChars(ReadOnlySpan<char> chars)
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
    public ReadOnlySpan<byte> GetSpan(long offset, int length)
        => GetSpanForRead(offset, length);

    /// <summary>
    /// Materialises a stored UTF-8 slice into a managed <see cref="string"/>.
    /// This allocates — use only at output or deep-copy boundaries.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendUtf8"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendUtf8"/>.</param>
    /// <returns>The decoded string.</returns>
    public unsafe string GetString(long offset, int length)
    {
        ThrowIfPooled();
        if (_pointer == null)
            throw new InvalidOperationException(
                $"Arena[#{Id}] has not been allocated. " +
                $"Disposed={_disposed} Pooled={_pooled} Capacity={_capacity} Position={_position} RefCount={_refCount} " +
                $"GetString at offset={offset} length={length}");
        return Encoding.UTF8.GetString(_pointer + offset, length);
    }

    // ───────────────────────── Float operations ─────────────────────────

    /// <summary>
    /// Appends a span of floats and returns the byte offset and element count.
    /// </summary>
    /// <param name="values">The float values to append.</param>
    /// <returns>The byte offset in this arena and the number of elements.</returns>
    public (long Offset, int Count) AppendFloats(ReadOnlySpan<float> values)
    {
        var (offset, _) = WriteBytes(MemoryMarshal.AsBytes(values));
        return (offset, values.Length);
    }

    /// <summary>Returns a span of floats previously appended.</summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendFloats"/>.</param>
    /// <param name="count">Element count returned by <see cref="AppendFloats"/>.</param>
    /// <returns>A span over the stored floats. Valid only while this arena is alive.</returns>
    public ReadOnlySpan<float> GetFloats(long offset, int count)
        => MemoryMarshal.Cast<byte, float>(GetSpanForRead(offset, count * sizeof(float)));

    /// <summary>
    /// Copies a span of floats from the arena into a newly allocated <see cref="float"/> array.
    /// Used at materialisation boundaries where the data must outlive the arena.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendFloats"/>.</param>
    /// <param name="count">Element count returned by <see cref="AppendFloats"/>.</param>
    /// <returns>A new array containing the float values.</returns>
    public float[] MaterializeFloats(long offset, int count)
        => GetFloats(offset, count).ToArray();

    // ───────────────────────── Byte operations ─────────────────────────

    /// <summary>
    /// Appends raw bytes (for byte-array values <see cref="DataKind.UInt8"/> with
    /// the <c>IsArray</c> flag set, or <see cref="DataKind.Image"/>)
    /// and returns the byte offset and length.
    /// </summary>
    /// <param name="bytes">The bytes to append.</param>
    /// <returns>The byte offset and length within this arena.</returns>
    public (long Offset, int Length) AppendBytes(ReadOnlySpan<byte> bytes)
        => WriteBytes(bytes);

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes from <paramref name="source"/> directly
    /// into the arena's backing memory, avoiding an intermediate managed <c>byte[]</c>.
    /// Returns <c>(Offset, bytesRead)</c> — if the stream ends early, <c>bytesRead</c>
    /// reflects the actual number of bytes written and the arena position is advanced
    /// accordingly.
    /// </summary>
    /// <remarks>
    /// Motivation: decompressed ZIP entries and other large binary sources average well
    /// above the 85 KB Large Object Heap threshold. Allocating a fresh <c>byte[]</c> per
    /// source entry forces Gen2 collections. Writing directly into the arena's memory-mapped
    /// storage avoids the managed heap entirely. The write is performed under the same lock
    /// as <see cref="AppendBytes"/>, so single-writer callers (e.g. a deserializer) can
    /// stream many entries back-to-back without GC pressure.
    /// </remarks>
    /// <param name="source">Source stream. Read synchronously; the caller's lock is held for the duration of the read.</param>
    /// <param name="length">Expected byte count. Capacity is pre-grown by this amount.</param>
    public (long Offset, int Length) AppendFromStream(Stream source, int length)
    {
        ThrowIfPooled();
        lock (_writeLock)
        {
            EnsureCapacity(length);
            long offset = _position;
            Span<byte> destination = GetSpanForWrite(offset, length);
            int total = 0;
            while (total < length)
            {
                int read = source.Read(destination.Slice(total, length - total));
                if (read == 0) break;
                total += read;
            }
            _position += total;
            return (offset, total);
        }
    }

    /// <summary>Returns raw bytes previously appended.</summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendBytes(ReadOnlySpan{byte})"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendBytes(ReadOnlySpan{byte})"/>.</param>
    /// <returns>A span over the stored bytes. Valid only while this arena is alive.</returns>
    public ReadOnlySpan<byte> GetBytes(long offset, int length)
        => GetSpanForRead(offset, length);

    /// <summary>
    /// Copies bytes from the arena into a newly allocated <see cref="byte"/> array.
    /// Used at materialisation boundaries where the data must outlive the arena.
    /// </summary>
    /// <param name="offset">Byte offset returned by <see cref="AppendBytes(ReadOnlySpan{byte})"/>.</param>
    /// <param name="length">Byte length returned by <see cref="AppendBytes(ReadOnlySpan{byte})"/>.</param>
    /// <returns>A new array containing the bytes.</returns>
    public byte[] MaterializeBytes(long offset, int length)
        => GetBytes(offset, length).ToArray();

    // ───────────────────────── DataValue array operations ─────────────────────────

    /// <summary>
    /// Appends a <see cref="DataValue"/> array as raw bytes (<see cref="DataValue.SizeBytes"/> per element).
    /// </summary>
    /// <param name="values">The DataValue array to append.</param>
    /// <returns>The byte offset and element count within this arena.</returns>
    public (long Offset, int Count) AppendDataValues(ReadOnlySpan<DataValue> values)
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
    public DataValue[] MaterializeDataValues(long offset, int count)
    {
        ReadOnlySpan<byte> bytes = GetSpanForRead(offset, count * DataValue.SizeBytes);
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
    public (long Offset, int Length) AppendTensor(ReadOnlySpan<float> data, ReadOnlySpan<int> shape)
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
    public float[] MaterializeTensor(long offset, int length, out int[] shape)
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
    public long CopyFrom(Arena source)
    {
        if (source._position == 0) return 0;

        // Reserve room and capture the base offset, then byte-copy in
        // int.MaxValue-sized chunks (Span<byte> length is int-capped, so a
        // single CopyTo can't span the whole region for arenas past 2GB).
        long totalBytes = source._position;
        long baseOffset;
        lock (_writeLock)
        {
            EnsureCapacity(totalBytes);
            baseOffset = _position;
            long copied = 0;
            while (copied < totalBytes)
            {
                int chunk = (int)Math.Min(totalBytes - copied, int.MaxValue);
                ReadOnlySpan<byte> src = source.GetSpanForRead(copied, chunk);
                src.CopyTo(GetSpanForWrite(baseOffset + copied, chunk));
                copied += chunk;
            }
            _position += totalBytes;
        }
        return baseOffset;
    }

    // ───────────────────────── Reset ─────────────────────────

    /// <summary>
    /// Clears the arena for reuse without releasing the backing mapping.
    /// Position returns to zero and the object side-list is emptied; the mmap
    /// region is kept alive so the next write avoids the allocation cost.
    /// </summary>
    /// <remarks>
    /// Intended for owners (e.g. <c>DatumFileWriter</c>) that reuse a single arena
    /// across distinct lifecycle phases such as row-group flushes. Pool-managed
    /// arenas use <see cref="Pool"/>/<see cref="Unpool"/> instead, which handle
    /// the reset as part of the pool transition.
    /// </remarks>
    public void Reset()
    {
        if (_backingFilePath is not null)
            throw new InvalidOperationException(
                "File-backed arenas are not reset for reuse — they're disposed at end of life. " +
                "Reset is part of the anonymous-pool reuse cycle, which file-backed arenas do not participate in.");

        ThrowIfPooled();
        lock (_writeLock)
        {
            _position = 0;
            _objects?.Clear();
        }
        DatumDiagnostics.RecordArenaReset();
    }

    // ───────────────────────── Pooling ─────────────────────────

    /// <summary>
    /// Marks this arena as pooled, clears the owner, and resets position to zero.
    /// Any subsequent read or write will throw until <see cref="Unpool"/> is called.
    /// Called by the pool when the arena is returned.
    /// </summary>
    internal void Pool()
    {
        if (_backingFilePath is not null)
            throw new InvalidOperationException(
                "File-backed arenas cannot be pooled — they have file identity, not interchangeability. " +
                "Dispose them directly when their owner is done.");

        if (_pooled)
            throw new InvalidOperationException("Arena is already pooled — double-return detected.");

        lock (_writeLock)
        {
            _position = 0;
            _objects?.Clear();
        }

        _pooled = true;
    }

    /// <summary>
    /// Marks this arena as active, resets it for reuse, and assigns the new owner.
    /// Called by the pool when the arena is rented out.
    /// </summary>
    internal void Unpool()
    {
        if (!_pooled)
            throw new InvalidOperationException("Arena is not pooled — double-rent detected.");
        _pooled = false;
    }

    private void ThrowIfPooled()
    {
        if (_pooled)
            throw new InvalidOperationException(
                "Arena is pooled and must not be accessed. " +
                "This indicates a use-after-return bug — a DataValue or operator is reading from an arena that was returned to the pool.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DatumDiagnostics.RecordArenaDispose(_capacity);
        ReleaseMapping();
        _position = 0;

        // File-backed: best-effort delete of the backing file. The mapping has been released
        // above so the file is unlocked; on Windows the delete may briefly fail if another
        // handle still references the file (shouldn't happen — file path is GUID-unique to
        // this arena). Swallow IOException because the OS reclaims temp dirs at shutdown
        // anyway, and we don't want disposal to throw.
        if (_backingFilePath is not null && File.Exists(_backingFilePath))
        {
            try
            {
                File.Delete(_backingFilePath);
            }
            catch (IOException) { }
        }
    }

    // ───────────────────────── Memory-mapped backing ─────────────────────────

    private static unsafe void CreateFileMapping(
        string backingFilePath, long capacity, FileMode fileMode,
        out MemoryMappedFile mmf, out MemoryMappedViewAccessor accessor, out byte* pointer)
    {
        mmf = MemoryMappedFile.CreateFromFile(
            backingFilePath, fileMode, mapName: null, capacity, MemoryMappedFileAccess.ReadWrite);

        accessor = mmf.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
        pointer = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
    }

    private unsafe void ReleaseMapping()
    {
        if (_backingFilePath is null)
        {
            // Anonymous: release the entire VA reservation. Pointer becomes invalid.
            if (_pointer != null)
            {
                VirtualMemory.Release(_pointer, _reservedBytes);
                _pointer = null;
            }
            _reservedBytes = 0;
            _capacity = 0;
        }
        else
        {
            if (_pointer != null)
            {
                _accessor!.SafeMemoryMappedViewHandle.ReleasePointer();
                _pointer = null;
            }
            _accessor?.Dispose();
            _mmf?.Dispose();
            _accessor = null;
            _mmf = null;
        }
    }

    /// <summary>
    /// Allocates or grows backing storage to fit an additional write. Must be called under
    /// <see cref="_writeLock"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Anonymous arenas</strong> reserve a large VA range on first write and commit
    /// pages on demand. The base pointer is stable for the arena's lifetime — grows never
    /// move <see cref="_pointer"/>. This is what lets parallel scalar dispatch hold spans
    /// across calls without dangling on a concurrent grow.
    /// </para>
    /// <para>
    /// <strong>File-backed arenas</strong> still unmap / SetLength / remap on grow, because
    /// the bytes have to live in the file. Concurrent reads of a file-backed arena that's
    /// being written are NOT safe — but file-backed mode is single-writer-spill-style and
    /// doesn't share that contract.
    /// </para>
    /// </remarks>
    private unsafe void EnsureCapacity(long additionalBytes)
    {
        long required = _position + additionalBytes;

        if (_backingFilePath is null)
        {
            EnsureAnonymousCapacity(required);
        }
        else
        {
            EnsureFileBackedCapacity(required);
        }
    }

    private unsafe void EnsureAnonymousCapacity(long required)
    {
        // First write: reserve the full range and commit the initial slice.
        if (_pointer == null)
        {
            long initial = Math.Max(_initialCapacity, required);
            if (initial > MaxAnonymousReservation)
            {
                throw new InvalidOperationException(
                    $"Arena[#{Id}] initial capacity {initial:N0} exceeds per-arena reservation cap " +
                    $"{MaxAnonymousReservation:N0}. Bump MaxAnonymousReservation if a single arena truly needs more.");
            }

            _pointer = VirtualMemory.Reserve(MaxAnonymousReservation);
            _reservedBytes = MaxAnonymousReservation;

            long initialCommit = VirtualMemory.RoundUpToPage(initial);
            VirtualMemory.Commit(_pointer, 0, initialCommit);
            _capacity = initialCommit;

            DatumDiagnostics.RecordArenaInitialMapping(_capacity);
            return;
        }

        if (required <= _capacity) return;

        // Grow: commit more pages within the existing reservation. Pointer does NOT move.
        long oldCapacity = _capacity;
        long target = Math.Max(_capacity * 2, required);
        if (target > _reservedBytes)
        {
            throw new InvalidOperationException(
                $"Arena[#{Id}] needs {target:N0} bytes but reservation is {_reservedBytes:N0}. " +
                $"Bump MaxAnonymousReservation if a single arena truly needs more than 8 GB.");
        }

        long newCapacity = VirtualMemory.RoundUpToPage(target);
        VirtualMemory.Commit(_pointer, _capacity, newCapacity - _capacity);
        _capacity = newCapacity;

        // bytesCopied is 0 — VA grow never copies. The counter still fires so diagnostics
        // see the "growth event" but it accurately reflects "no copy happened."
        DatumDiagnostics.RecordArenaGrow(oldCapacity, newCapacity, bytesCopied: 0);
    }

    private unsafe void EnsureFileBackedCapacity(long required)
    {
        // First write: create the file and map it.
        if (_mmf is null)
        {
            long capacity = Math.Max(_initialCapacity, required);
            CreateFileMapping(_backingFilePath!, capacity, FileMode.CreateNew,
                out _mmf, out _accessor, out _pointer);
            _capacity = capacity;
            DatumDiagnostics.RecordArenaInitialMapping(capacity);
            return;
        }

        if (required <= _capacity) return;

        // File-backed: bytes already persisted on disk. Unmap → SetLength → remap. No memcpy.
        long oldCapacity = _capacity;
        long newCapacity = Math.Max(_capacity * 2, required);

        ReleaseMapping();
        using (FileStream fs = File.Open(_backingFilePath!, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.SetLength(newCapacity);
        }
        CreateFileMapping(_backingFilePath!, newCapacity, FileMode.Open,
            out _mmf, out _accessor, out _pointer);

        _capacity = newCapacity;
        DatumDiagnostics.RecordArenaGrow(oldCapacity, newCapacity, bytesCopied: 0);
    }

    private unsafe Span<byte> GetSpanForWrite(long offset, int length)
        => new(_pointer + offset, length);

    private unsafe ReadOnlySpan<byte> GetSpanForRead(long offset, int length)
    {
        ThrowIfPooled();
        if (_pointer == null)
            throw new InvalidOperationException(
                $"Arena[#{Id}] has not been allocated. " +
                $"Disposed={_disposed} Pooled={_pooled} Capacity={_capacity} Position={_position} RefCount={_refCount} " +
                $"Read at offset={offset} length={length}");
        return new(_pointer + offset, length);
    }
}
