using System.Runtime.CompilerServices;
using System.Text;

namespace DatumIngest.Model;

/// <summary>
/// Append-only store for reference-type payloads that <see cref="DataValue"/>
/// cannot hold inline (strings, float arrays, byte arrays, image handles, typed
/// arrays).  Each <see cref="DataValue"/> with the <c>HasReference</c> flag set
/// stores an integer index into this store instead of an <c>object?</c> field,
/// keeping the struct fully blittable and invisible to the garbage collector.
/// </summary>
/// <remarks>
/// Call <see cref="BeginQueryScope"/> before a query to install an isolated store
/// for the current async execution context; all child continuations inherit it
/// automatically.  Call <see cref="EndQueryScope"/> after result consumption to
/// reset and discard the scoped store.  Every code path that creates reference-backed
/// <see cref="DataValue"/> instances must run inside an active scope.
/// </remarks>
internal sealed class ReferenceStore : IValueStore
{
    private static readonly AsyncLocal<ReferenceStore?> _current = new();

    private volatile object?[] _items;
    private int _count;
    private readonly Lock _growLock = new();
    private Dictionary<string, int>? _stringIntern;

    /// <summary>
    /// Creates a new reference store with the given initial capacity.
    /// </summary>
    /// <param name="initialCapacity">Initial backing array size.</param>
    private ReferenceStore(int initialCapacity = 4096)
    {
        _items = new object?[initialCapacity];
    }

    /// <summary>
    /// Returns the store for the current query scope.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no scope has been established via <see cref="BeginQueryScope"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReferenceStore Current() =>
        _current.Value ?? throw new InvalidOperationException(
            "No ReferenceStore scope is active. Call ReferenceStore.BeginQueryScope() before creating reference-backed DataValues.");

    // ───────────────────────── IValueStore ─────────────────────────

    /// <inheritdoc />
    public (int P0, int P1) StoreString(string value) => (InternString(value), 0);

    /// <inheritdoc />
    public string RetrieveString(int p0, int p1) => Get<string>(p0);

    /// <inheritdoc />
    public (int P0, int P1) StoreBytes(ReadOnlySpan<byte> bytes)
    {
        byte[] copy = bytes.ToArray();
        return (Add(copy), 0);
    }

    /// <inheritdoc />
    public byte[] RetrieveBytes(int p0, int p1) => Get<byte[]>(p0);

    /// <inheritdoc />
    public (int P0, int P1) StoreFloats(ReadOnlySpan<float> floats)
    {
        float[] copy = floats.ToArray();
        return (Add(copy), 0);
    }

    /// <inheritdoc />
    public float[] RetrieveFloats(int p0, int p1) => Get<float[]>(p0);

    /// <inheritdoc />
    public (int P0, int P1) StoreObject(object value) => (Add(value), 0);

    /// <inheritdoc />
    public object RetrieveObject(int p0, int p1) => Get(p0);

    // ───────────────────────── Scope management ─────────────────────────

    /// <summary>
    /// Starts a new isolated store for the current async query context.
    /// Must be called before any query work that produces <see cref="DataValue"/> references.
    /// </summary>
    internal static void BeginQueryScope() => _current.Value = new ReferenceStore();

    /// <summary>
    /// Clears the current query's store and removes the scope.
    /// Call after <c>FinalizeAsync</c> and all result consumption.
    /// </summary>
    internal static void EndQueryScope()
    {
        ReferenceStore? store = _current.Value;
        if (store is not null)
        {
            store.Reset();
        }

        _current.Value = null;
    }

    /// <summary>
    /// Appends a reference-type object and returns its integer index.
    /// </summary>
    /// <param name="value">The object to store.  Must not be <see langword="null"/>.</param>
    /// <returns>A stable index that can be passed to <see cref="Get{T}"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Add(object value)
    {
        lock (_growLock)
        {
            return AddCoreLocked(value);
        }
    }

    /// <summary>
    /// Atomically reserves two consecutive slots and stores both objects.
    /// Returns the index of the first slot; the second is at <c>index + 1</c>.
    /// Used by <see cref="DataValue.FromTensor"/> which needs data and shape
    /// at adjacent indices.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int AddPair(object first, object second)
    {
        lock (_growLock)
        {
            int index = _count;
            _count += 2;
            object?[] items = _items;

            if ((uint)(index + 1) >= (uint)items.Length)
            {
                items = GrowLocked(index + 1);
            }

            items[index] = first;
            items[index + 1] = second;
            return index;
        }
    }

    /// <summary>
    /// Retrieves the object at the given index, cast to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Expected reference type.</typeparam>
    /// <param name="index">Index returned by a previous <see cref="Add"/> call.</param>
    /// <returns>The stored object.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T Get<T>(int index) where T : class
    {
        return Unsafe.As<T>(_items[index]!);
    }

    /// <summary>
    /// Retrieves the raw object at the given index without casting.
    /// </summary>
    /// <param name="index">Index returned by a previous <see cref="Add"/> call.</param>
    /// <returns>The stored object.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal object Get(int index)
    {
        return _items[index]!;
    }

    /// <summary>
    /// Returns the index for <paramref name="value"/>, reusing an existing slot
    /// when the same string has been interned before in this store. This avoids
    /// unbounded growth of the backing array when low-cardinality string columns
    /// (e.g. <c>eval_set</c> with 3 distinct values) are scanned across millions
    /// of rows.
    /// </summary>
    /// <param name="value">The string to intern.</param>
    /// <returns>A stable index that can be passed to <see cref="Get{T}"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int InternString(string value)
    {
        lock (_growLock)
        {
            _stringIntern ??= new(StringComparer.Ordinal);
            if (_stringIntern.TryGetValue(value, out int existing))
            {
                return existing;
            }

            int index = AddCoreLocked(value);
            _stringIntern[value] = index;
            return index;
        }
    }

    /// <summary>
    /// Interns a string from raw UTF-8 bytes without allocating a managed
    /// <see cref="string"/> when the value has been seen before. On a cache hit
    /// the span is hashed and compared directly against existing dictionary keys
    /// via <see cref="Dictionary{TKey,TValue}.GetAlternateLookup{TAlternate}"/>,
    /// which <see cref="StringComparer.Ordinal"/> supports for
    /// <see cref="ReadOnlySpan{T}"/> of <see langword="char"/> in .NET 9+.
    /// </summary>
    /// <param name="utf8Bytes">The UTF-8 encoded bytes of the string.</param>
    /// <returns>A stable index that can be passed to <see cref="Get{T}"/>.</returns>
    internal int InternStringFromUtf8(ReadOnlySpan<byte> utf8Bytes)
    {
        // Decode to chars on the stack for short strings, ArrayPool for long ones.
        int maxCharCount = Encoding.UTF8.GetMaxCharCount(utf8Bytes.Length);
        char[]? rented = null;
        Span<char> charBuffer = maxCharCount <= 256
            ? stackalloc char[maxCharCount]
            : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(maxCharCount));

        int charCount = Encoding.UTF8.GetChars(utf8Bytes, charBuffer);
        ReadOnlySpan<char> chars = charBuffer[..charCount];

        try
        {
            lock (_growLock)
            {
                _stringIntern ??= new(StringComparer.Ordinal);
                var lookup = _stringIntern.GetAlternateLookup<ReadOnlySpan<char>>();

                if (lookup.TryGetValue(chars, out int existing))
                {
                    return existing;
                }

                // Cache miss — allocate the string and store it.
                string value = new(chars);
                int index = AddCoreLocked(value);
                lookup[chars] = index;
                return index;
            }
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Appends a value to the backing array without acquiring the lock (caller
    /// must already hold <see cref="_growLock"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AddCoreLocked(object value)
    {
        int index = _count++;
        object?[] items = _items;

        if ((uint)index >= (uint)items.Length)
        {
            items = GrowLocked(index);
        }

        items[index] = value;
        return index;
    }

    /// <summary>
    /// Clears all stored references, allowing the garbage collector to reclaim them.
    /// Indices issued before this call become invalid.  Only safe to call when no
    /// concurrent readers or writers exist.
    /// </summary>
    internal void Reset()
    {
        int count = _count;
        if (count == 0) return;
        Array.Clear(_items, 0, Math.Min(count, _items.Length));
        _count = 0;
        _stringIntern?.Clear();
    }

    /// <summary>
    /// Current number of stored references.
    /// </summary>
    internal int Count => _count;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private object?[] GrowLocked(int requiredIndex)
    {
        object?[] current = _items;
        if ((uint)requiredIndex < (uint)current.Length)
        {
            return current;
        }

        int newCapacity = (int)Math.Min((long)current.Length * 2, int.MaxValue);
        if (newCapacity <= requiredIndex)
        {
            newCapacity = requiredIndex + 1;
        }

        object?[] grown = new object?[newCapacity];
        Array.Copy(current, grown, current.Length);
        _items = grown;
        return grown;
    }
}
