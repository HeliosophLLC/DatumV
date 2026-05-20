using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Open-addressing hash map with linear probing for single-<see cref="DataValue"/> keys.
/// Supports explicit cache-line prefetching via <see cref="PrefetchEntry"/> to hide
/// memory latency in software-pipelined probe loops.
/// <para>
/// Linear probing gives cache-friendly sequential memory access during probe chains,
/// and the flat entry array avoids the per-bucket linked-list node overhead of
/// <see cref="Dictionary{TKey, TValue}"/>.
/// </para>
/// </summary>
/// <typeparam name="TValue">The type of the value associated with each key.</typeparam>
internal sealed class DataValueHashMap<TValue>
{
    private const int DefaultCapacity = 16;
    private const int MaxLoadFactorPercent = 50;

    private Entry[] _entries;
    private int _count;
    private int _mask;
    private int _resizeThreshold;

    private struct Entry
    {
        internal bool Occupied;
        internal int HashCode;
        internal DataValue Key;
        internal TValue Value;
    }

    /// <summary>
    /// Creates a new hash map with the specified initial capacity (rounded up to a power of two).
    /// </summary>
    /// <param name="capacity">The minimum initial capacity.</param>
    internal DataValueHashMap(int capacity = DefaultCapacity)
    {
        int rounded = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, DefaultCapacity));
        _entries = new Entry[rounded];
        _mask = rounded - 1;
        _resizeThreshold = rounded * MaxLoadFactorPercent / 100;
    }

    /// <summary>Gets the number of key-value pairs in the map.</summary>
    internal int Count => _count;

    /// <summary>
    /// Issues a CPU prefetch hint for the cache line containing the entry at the
    /// bucket index derived from <paramref name="hashCode"/>. Call this several
    /// iterations before the corresponding <see cref="GetOrAdd(DataValue, int, out bool)"/> to overlap
    /// cache-miss latency with useful computation.
    /// </summary>
    /// <remarks>
    /// If the GC relocates the entries array between prefetch and access, the
    /// prefetch becomes a harmless no-op — the actual probe uses managed indexing.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe void PrefetchEntry(int hashCode)
    {
        if (Sse.IsSupported)
        {
            int index = hashCode & _mask;
            Sse.Prefetch0(Unsafe.AsPointer(ref _entries[index]));
        }
    }

    /// <summary>
    /// Looks up or inserts a key in a single probe sequence. Returns a reference
    /// to the value slot in the entry, allowing the caller to initialize it on miss.
    /// </summary>
    /// <param name="key">The key to look up or insert.</param>
    /// <param name="exists">
    /// Set to <c>true</c> if the key was already present; <c>false</c> if a new
    /// entry was created.
    /// </param>
    /// <returns>A reference to the value slot for the entry.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref TValue GetOrAdd(DataValue key, out bool exists)
    {
        return ref GetOrAdd(key, key.GetHashCode(), out exists);
    }

    /// <summary>
    /// Looks up or inserts a key using a pre-computed hash code. Call this
    /// overload after <see cref="PrefetchEntry"/> to avoid rehashing the key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref TValue GetOrAdd(DataValue key, int hashCode, out bool exists)
    {
        if (_count >= _resizeThreshold)
        {
            Resize();
        }

        Entry[] entries = _entries;
        int index = hashCode & _mask;

        while (true)
        {
            ref Entry entry = ref entries[index];

            if (!entry.Occupied)
            {
                entry.HashCode = hashCode;
                entry.Key = key;
                entry.Occupied = true;
                _count++;
                exists = false;
                return ref entry.Value;
            }

            if (entry.HashCode == hashCode && entry.Key.Equals(key))
            {
                exists = true;
                return ref entry.Value;
            }

            index = (index + 1) & _mask;
        }
    }

    /// <summary>
    /// Looks up a key without inserting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetValue(DataValue key, [NotNullWhen(true)] out TValue? value)
    {
        int hashCode = key.GetHashCode();
        Entry[] entries = _entries;
        int index = hashCode & _mask;

        while (true)
        {
            ref Entry entry = ref entries[index];

            if (!entry.Occupied)
            {
                value = default;
                return false;
            }

            if (entry.HashCode == hashCode && entry.Key.Equals(key))
            {
                value = entry.Value!;
                return true;
            }

            index = (index + 1) & _mask;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the map contains the specified key.
    /// </summary>
    internal bool ContainsKey(DataValue key) => TryGetValue(key, out _);

    /// <summary>Gets an enumerable over all values in the map.</summary>
    internal IEnumerable<TValue> Values
    {
        get
        {
            Entry[] entries = _entries;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Occupied)
                {
                    yield return entries[i].Value;
                }
            }
        }
    }

    private void Resize()
    {
        int newCapacity = _entries.Length * 2;
        Entry[] newEntries = new Entry[newCapacity];
        int newMask = newCapacity - 1;

        for (int i = 0; i < _entries.Length; i++)
        {
            ref Entry oldEntry = ref _entries[i];
            if (!oldEntry.Occupied)
            {
                continue;
            }

            int index = oldEntry.HashCode & newMask;
            while (newEntries[index].Occupied)
            {
                index = (index + 1) & newMask;
            }

            newEntries[index] = oldEntry;
        }

        _entries = newEntries;
        _mask = newMask;
        _resizeThreshold = newCapacity * MaxLoadFactorPercent / 100;
    }
}
