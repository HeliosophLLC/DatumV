using System.Runtime.CompilerServices;

namespace DatumIngest.Model;

/// <summary>
/// Global append-only store for reference-type payloads that <see cref="DataValue"/>
/// cannot hold inline (strings, float arrays, byte arrays, image handles, typed
/// arrays).  Each <see cref="DataValue"/> with the <c>HasReference</c> flag set
/// stores an integer index into this store instead of an <c>object?</c> field,
/// keeping the struct fully blittable and invisible to the garbage collector.
/// </summary>
/// <remarks>
/// <para>
/// The store uses a single global instance with lock-free append
/// (<see cref="Interlocked.Increment(ref int)"/> for index allocation) and
/// a lock only for rare geometric resizing.  This makes it safe for use
/// across threads and async continuations without thread-affinity constraints.
/// </para>
/// <para>
/// Call <see cref="Reset"/> at query boundaries when no concurrent readers
/// or writers exist to reclaim memory.
/// </para>
/// </remarks>
internal sealed class ReferenceStore
{
    private static readonly ReferenceStore Instance = new();

    private volatile object?[] _items;
    private int _count;
    private readonly Lock _growLock = new();

    /// <summary>
    /// Creates a new reference store with the given initial capacity.
    /// </summary>
    /// <param name="initialCapacity">Initial backing array size.</param>
    private ReferenceStore(int initialCapacity = 4096)
    {
        _items = new object?[initialCapacity];
    }

    /// <summary>
    /// Returns the global reference store singleton.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReferenceStore CurrentOrCreate() => Instance;

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
            int index = _count++;
            object?[] items = _items;

            if ((uint)index >= (uint)items.Length)
            {
                items = GrowLocked(index);
            }

            items[index] = value;
            return index;
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
