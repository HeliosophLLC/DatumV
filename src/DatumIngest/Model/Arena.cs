using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using DatumIngest.Diagnostics;

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
    private const int DefaultCapacity = 1024 * 1024; // 1 MB — OS demand-pages, so unused capacity costs nothing

    private readonly Lock _writeLock = new();
    private readonly string? _backingFilePath;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private unsafe byte* _pointer;
    private int _initialCapacity;
    private int _capacity;
    private int _position;
    private bool _disposed;
    private bool _pooled;
    private int _refCount;

    // Side-list for managed objects that cannot be byte-serialized (e.g. ImageHandle).
    // Accessed under _writeLock for thread safety.
    private List<object>? _objects;

    /// <summary>Creates an anonymous-mmap arena with the specified initial byte capacity.</summary>
    /// <param name="initialCapacity">
    /// Initial size of the anonymous memory-mapped region in bytes.
    /// The mapping is not allocated until the first write.
    /// </param>
    public Arena(int initialCapacity = DefaultCapacity)
        : this(initialCapacity, backingFilePath: null) { }

    /// <summary>
    /// Internal ctor shared by anonymous and file-backed factories. <paramref name="backingFilePath"/>
    /// non-null selects the file-backed mode; the file is created lazily on first write.
    /// </summary>
    private Arena(int initialCapacity, string? backingFilePath)
    {
        _initialCapacity = Math.Max(initialCapacity, DefaultCapacity);
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
    public static Arena CreateFileBacked(string filePath, int initialCapacity = DefaultCapacity)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        return new Arena(initialCapacity, filePath);
    }

    /// <summary>Whether the backing memory-mapped region has been allocated.</summary>
    public bool IsAllocated => _mmf is not null;

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
        ThrowIfPooled();
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
        ThrowIfPooled();
        if (_objects is null || (uint)p0 >= (uint)_objects.Count)
            throw new InvalidOperationException($"No object stored at index {p0}.");
        return _objects[p0];
    }

    // ───────────────────────── Common ─────────────────────────

    /// <summary>Total bytes written so far.</summary>
    public int BytesWritten => _position;

    /// <summary>Current capacity of the backing memory-mapped region in bytes, or zero if not yet allocated.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Returns a read-only view over a sub-region ("page") of this arena.
    /// All <c>Retrieve*</c> calls on the slice add <paramref name="pageBase"/> to <c>p0</c>
    /// before dereferencing. Used to resolve page-relative offsets copied verbatim from
    /// another arena via <see cref="CopyFrom"/> without rewriting the bytes.
    /// </summary>
    /// <param name="pageBase">Byte offset at which the page begins within this arena.</param>
    /// <param name="pageLength">Length of the page in bytes.</param>
    public ArenaSlice Slice(int pageBase, int pageLength)
        => new(this, pageBase, pageLength);

    // ───────────────────────── Core write path ─────────────────────────

    /// <summary>
    /// Appends raw bytes to the arena under a lock. All typed append methods
    /// flow through this single write path.
    /// </summary>
    /// <returns>The byte offset and length of the written region.</returns>
    private (int Offset, int Length) WriteBytes(ReadOnlySpan<byte> data)
    {
        ThrowIfPooled();
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
    {
        ThrowIfPooled();
        if (_pointer == null)
            throw new InvalidOperationException("Arena has not been allocated. No data has been written.");
        return Encoding.UTF8.GetString(_pointer + offset, length);
    }

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
    public (int Offset, int Length) AppendFromStream(Stream source, int length)
    {
        ThrowIfPooled();
        lock (_writeLock)
        {
            EnsureCapacity(length);
            int offset = _position;
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
        if (source._position == 0) return 0;
        ReadOnlySpan<byte> data = source.GetSpanForRead(0, source._position);
        var (offset, _) = WriteBytes(data);
        return offset;
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

    private static unsafe void CreateMapping(
        string? backingFilePath, int capacity, FileMode fileMode,
        out MemoryMappedFile mmf, out MemoryMappedViewAccessor accessor, out byte* pointer)
    {
        mmf = backingFilePath is null
            ? MemoryMappedFile.CreateNew(null, capacity, MemoryMappedFileAccess.ReadWrite)
            : MemoryMappedFile.CreateFromFile(
                backingFilePath, fileMode, mapName: null, capacity, MemoryMappedFileAccess.ReadWrite);

        accessor = mmf.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
        pointer = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
    }

    private unsafe void ReleaseMapping()
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

    /// <summary>
    /// Allocates or grows the backing mapping. Must be called under <see cref="_writeLock"/>.
    /// On the first call, creates the initial mapping lazily. Anonymous and file-backed
    /// arenas share the rent path but diverge on grow: anonymous allocates a fresh mapping
    /// and memcpys the prior contents; file-backed unmaps, resizes the file in place, and
    /// remaps (Windows refuses <see cref="FileStream.SetLength"/> on a mapped file, so the
    /// unmap window is mandatory — it's safe because <see cref="_writeLock"/> serializes us).
    /// </summary>
    private unsafe void EnsureCapacity(int additionalBytes)
    {
        int required = _position + additionalBytes;

        // First allocation — deferred from constructor.
        if (_mmf is null)
        {
            int capacity = Math.Max(_initialCapacity, required);
            CreateMapping(_backingFilePath, capacity, FileMode.CreateNew,
                out _mmf, out _accessor, out _pointer);
            _capacity = capacity;
            DatumDiagnostics.RecordArenaInitialMapping(capacity);
            return;
        }

        if (required <= _capacity) return;

        int oldCapacity = _capacity;
        int bytesCopied = _position;
        int newCapacity = Math.Max(_capacity * 2, required);

        if (_backingFilePath is null)
        {
            // Anonymous: build new mapping, memcpy prior contents, release old.
            CreateMapping(null, newCapacity, FileMode.CreateNew,
                out var newMmf, out var newAccessor, out var newPointer);
            new ReadOnlySpan<byte>(_pointer, _position).CopyTo(new Span<byte>(newPointer, newCapacity));
            ReleaseMapping();
            _mmf = newMmf;
            _accessor = newAccessor;
            _pointer = newPointer;
        }
        else
        {
            // File-backed: bytes already persisted. Unmap → SetLength → remap. No memcpy.
            ReleaseMapping();
            using (FileStream fs = File.Open(_backingFilePath, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.SetLength(newCapacity);
            }
            CreateMapping(_backingFilePath, newCapacity, FileMode.Open,
                out _mmf, out _accessor, out _pointer);
        }

        _capacity = newCapacity;

        DatumDiagnostics.RecordArenaGrow(oldCapacity, newCapacity, bytesCopied);
    }

    private unsafe Span<byte> GetSpanForWrite(int offset, int length)
        => new(_pointer + offset, length);

    private unsafe ReadOnlySpan<byte> GetSpanForRead(int offset, int length)
    {
        ThrowIfPooled();
        if (_pointer == null)
            throw new InvalidOperationException("Arena has not been allocated. No data has been written.");
        return new(_pointer + offset, length);
    }
}
