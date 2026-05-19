using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Open-addressing hash map with linear probing for composite keys (multiple
/// <see cref="DataValue"/> elements). Supports zero-allocation probing via
/// <see cref="ReadOnlySpan{T}">ReadOnlySpan&lt;DataValue&gt;</see> and explicit
/// cache-line prefetching via <see cref="PrefetchEntry"/> to hide memory latency
/// in software-pipelined probe loops.
/// <para>
/// Linear probing gives cache-friendly sequential memory access during probe chains,
/// and the flat entry array avoids the per-bucket linked-list node overhead of
/// <see cref="Dictionary{TKey, TValue}"/>.
/// </para>
/// </summary>
/// <typeparam name="TValue">The type of the value associated with each key.</typeparam>
internal sealed class CompositeKeyHashMap<TValue>
{
    private const int DefaultCapacity = 16;
    private const int MaxLoadFactorPercent = 50;

    private Entry[] _entries;
    private int _count;
    private int _mask;
    private int _resizeThreshold;

    private struct Entry
    {
        internal int HashCode;
        internal DataValue[]? Key; // null = empty slot
        internal TValue Value;
    }

    /// <summary>
    /// Creates a new hash map with the specified initial capacity (rounded up to a power of two).
    /// </summary>
    /// <param name="capacity">The minimum initial capacity.</param>
    internal CompositeKeyHashMap(int capacity = DefaultCapacity)
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
    /// iterations before the corresponding <see cref="GetOrAddDefault(ReadOnlySpan{DataValue}, out bool, out DataValue[])"/>
    /// to overlap cache-miss latency with useful computation.
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
    /// Computes the hash code for a composite key span. Exposed so callers can
    /// hash a key once and pass the result to both <see cref="PrefetchEntry"/> and
    /// <see cref="GetOrAddDefault(ReadOnlySpan{DataValue}, int, out bool, out DataValue[])"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ComputeHash(ReadOnlySpan<DataValue> key)
    {
        HashCode hash = new();
        for (int i = 0; i < key.Length; i++)
        {
            hash.Add(key[i]);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Looks up or inserts a composite key in a single probe sequence using a span
    /// and a pre-computed hash code (from <see cref="ComputeHash"/>).
    /// On insert, the span is materialized to a permanent <see cref="DataValue"/>[].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref TValue GetOrAddDefault(
        ReadOnlySpan<DataValue> key, int hashCode, out bool exists, out DataValue[] storedKey)
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

            if (entry.Key is null)
            {
                DataValue[] materialized = key.ToArray();
                entry.HashCode = hashCode;
                entry.Key = materialized;
                _count++;
                exists = false;
                storedKey = materialized;
                return ref entry.Value;
            }

            if (entry.HashCode == hashCode && SpanEquals(entry.Key, key))
            {
                exists = true;
                storedKey = entry.Key;
                return ref entry.Value;
            }

            index = (index + 1) & _mask;
        }
    }

    /// <summary>
    /// Looks up or inserts a composite key without a pre-computed hash.
    /// Convenience overload for non-pipelined paths.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref TValue GetOrAddDefault(
        ReadOnlySpan<DataValue> key, out bool exists, out DataValue[] storedKey)
    {
        return ref GetOrAddDefault(key, ComputeHash(key), out exists, out storedKey);
    }

    /// <summary>
    /// Looks up or inserts a composite key using a pre-allocated <see cref="DataValue"/>[]
    /// that is stored directly on miss (no copy).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref TValue GetOrAddDefault(DataValue[] key, out bool exists)
    {
        if (_count >= _resizeThreshold)
        {
            Resize();
        }

        int hashCode = ComputeHash(key.AsSpan());
        Entry[] entries = _entries;
        int index = hashCode & _mask;

        while (true)
        {
            ref Entry entry = ref entries[index];

            if (entry.Key is null)
            {
                entry.HashCode = hashCode;
                entry.Key = key;
                _count++;
                exists = false;
                return ref entry.Value;
            }

            if (entry.HashCode == hashCode && SpanEquals(entry.Key, key.AsSpan()))
            {
                exists = true;
                return ref entry.Value;
            }

            index = (index + 1) & _mask;
        }
    }

    /// <summary>
    /// Looks up a key by span without inserting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetValue(ReadOnlySpan<DataValue> key, [NotNullWhen(true)] out TValue? value)
    {
        int hashCode = ComputeHash(key);
        Entry[] entries = _entries;
        int index = hashCode & _mask;

        while (true)
        {
            ref Entry entry = ref entries[index];

            if (entry.Key is null)
            {
                value = default;
                return false;
            }

            if (entry.HashCode == hashCode && SpanEquals(entry.Key, key))
            {
                value = entry.Value!;
                return true;
            }

            index = (index + 1) & _mask;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the map contains the specified key span.
    /// </summary>
    internal bool ContainsKey(ReadOnlySpan<DataValue> key) => TryGetValue(key, out _);

    /// <summary>Gets an enumerable over all values in the map.</summary>
    internal IEnumerable<TValue> Values
    {
        get
        {
            Entry[] entries = _entries;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Key is not null)
                {
                    yield return entries[i].Value;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SpanEquals(DataValue[] stored, ReadOnlySpan<DataValue> probe)
    {
        if (stored.Length != probe.Length)
        {
            return false;
        }

        for (int i = 0; i < stored.Length; i++)
        {
            if (!stored[i].Equals(probe[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void Resize()
    {
        int newCapacity = _entries.Length * 2;
        Entry[] newEntries = new Entry[newCapacity];
        int newMask = newCapacity - 1;

        for (int i = 0; i < _entries.Length; i++)
        {
            ref Entry oldEntry = ref _entries[i];
            if (oldEntry.Key is null)
            {
                continue;
            }

            int index = oldEntry.HashCode & newMask;
            while (newEntries[index].Key is not null)
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
