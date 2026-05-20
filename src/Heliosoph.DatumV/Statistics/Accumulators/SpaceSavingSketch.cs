using System.IO.Hashing;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Statistics.Accumulators;

/// <summary>
/// Bounded-memory frequency sketch used by <see cref="SpaceSavingAccumulator"/>.
/// Implements the Space-Saving algorithm (Metwally, Agrawal, El-Abbadi 2005) with a
/// cap-and-skip fallback for pathological high-cardinality workloads.
/// </summary>
/// <remarks>
/// <para>
/// Two roles share this class. A <em>local</em> sketch is populated across all
/// batches of a single row group and discarded after merging. A <em>global</em>
/// sketch aggregates locals via <see cref="MergeFromLocal"/>; its materialized
/// strings outlive the writer's arena resets.
/// </para>
/// <para>
/// Allocation behavior: the Add hot path allocates nothing for numeric columns
/// and nothing for string columns either — the local sketch retains the
/// <see cref="DataValue"/> and the resolving <see cref="IValueStore"/> reference,
/// deferring string materialization to merge time. At merge, one managed string
/// is allocated only when a key is newly promoted into the global sketch (shared
/// keys skip materialization entirely). For a realistic Zipfian column this
/// converges to a fixed-size working set of ~<see cref="Capacity"/> strings total.
/// </para>
/// <para>
/// Guarantee (classical Space-Saving): any value with true frequency exceeding
/// <c>N / M</c> is tracked, where <c>N</c> is non-null observations and <c>M</c>
/// is <see cref="Capacity"/>. The guarantee <em>does not hold</em> when the local
/// sketch cap-and-skips — a pragmatic trade-off for columns (e.g. primary keys)
/// whose frequency distribution is uniform and whose top-K is meaningless anyway.
/// </para>
/// </remarks>
internal sealed class SpaceSavingSketch
{
    private readonly int _capacity;
    private readonly DataKind _kind;
    private readonly bool _isStringLike;
    private readonly bool _isLocal;

    private readonly Dictionary<long, int> _hashToSlot;
    private readonly long[] _hashes;
    private readonly long[] _counts;
    private readonly long[] _errors;

    // Local string sketch: retains the DataValue + the store that resolves its
    // offsets. The store reference keeps the underlying arena alive until merge.
    private readonly DataValue[]? _localValues;
    private readonly IValueStore?[]? _localStores;

    // Global string sketch: retains managed strings that survive arena resets.
    private readonly string?[]? _globalStrings;

    private int _size;

    public SpaceSavingSketch(int capacity, DataKind kind, bool isLocal)
    {
        _capacity = capacity;
        _kind = kind;
        _isStringLike = kind == DataKind.String;
        _isLocal = isLocal;

        _hashToSlot = new Dictionary<long, int>(capacity);
        _hashes = new long[capacity];
        _counts = new long[capacity];
        _errors = new long[capacity];

        if (_isStringLike)
        {
            if (isLocal)
            {
                _localValues = new DataValue[capacity];
                _localStores = new IValueStore?[capacity];
            }
            else
            {
                _globalStrings = new string?[capacity];
            }
        }
    }

    public int Capacity => _capacity;
    public int Size => _size;

    public long CountAt(int index) => _counts[index];
    public long ErrorAt(int index) => _errors[index];
    public string GlobalStringAt(int index) => _globalStrings![index]!;

    /// <summary>Sum of all tracked slot counts (does not include untracked mass).</summary>
    public long TrackedCount
    {
        get
        {
            long sum = 0;
            for (int i = 0; i < _size; i++) sum += _counts[i];
            return sum;
        }
    }

    /// <summary>
    /// Adds a non-null value to a local sketch. Returns <c>false</c> when the sketch
    /// is full and the value is new — caller should increment its untracked counter.
    /// </summary>
    public bool AddLocal(DataValue value, IValueStore store)
    {
        long hash = ComputeHash(value, store);

        if (_hashToSlot.TryGetValue(hash, out int slot))
        {
            _counts[slot]++;
            return true;
        }

        if (_size < _capacity)
        {
            int newSlot = _size;
            _hashes[newSlot] = hash;
            _counts[newSlot] = 1;
            _errors[newSlot] = 0;
            if (_isStringLike)
            {
                _localValues![newSlot] = value;
                _localStores![newSlot] = store;
            }
            _hashToSlot[hash] = newSlot;
            _size++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Merges every slot of <paramref name="local"/> into this (global) sketch,
    /// materializing managed strings only for keys newly promoted into the global.
    /// Safe to call only before the local's backing arena is reset.
    /// </summary>
    public void MergeFromLocal(SpaceSavingSketch local)
    {
        for (int i = 0; i < local._size; i++)
        {
            long hash = local._hashes[i];
            long addCount = local._counts[i];
            long addErr = local._errors[i];

            if (_hashToSlot.TryGetValue(hash, out int slot))
            {
                _counts[slot] += addCount;
                _errors[slot] += addErr;
                continue;
            }

            if (_size < _capacity)
            {
                int newSlot = _size;
                _hashes[newSlot] = hash;
                _counts[newSlot] = addCount;
                _errors[newSlot] = addErr;
                if (_isStringLike)
                {
                    _globalStrings![newSlot] =
                        local._localValues![i].AsString(local._localStores![i]!);
                }
                _hashToSlot[hash] = newSlot;
                _size++;
                continue;
            }

            // New key, sketch full — evict min and inflate newcomer's count by the
            // evicted count (standard Space-Saving merge rule).
            int minSlot = FindMinSlot();
            long minCount = _counts[minSlot];

            _hashToSlot.Remove(_hashes[minSlot]);

            _hashes[minSlot] = hash;
            _counts[minSlot] = addCount + minCount;
            _errors[minSlot] = addErr + minCount;
            if (_isStringLike)
            {
                _globalStrings![minSlot] =
                    local._localValues![i].AsString(local._localStores![i]!);
            }
            _hashToSlot[hash] = minSlot;
        }
    }

    /// <summary>
    /// Returns slot indices sorted by count descending. Used by
    /// <see cref="SpaceSavingAccumulator"/> when producing top-K results.
    /// </summary>
    public int[] GetSortedSlotIndices()
    {
        int[] indices = new int[_size];
        for (int i = 0; i < _size; i++) indices[i] = i;
        Array.Sort(indices, (a, b) => _counts[b].CompareTo(_counts[a]));
        return indices;
    }

    /// <summary>
    /// Resets the sketch for reuse in the next row group. Clears size and the
    /// dictionary; drops retained store references so underlying arenas are
    /// eligible for GC. Preallocated slot arrays are preserved.
    /// </summary>
    public void Reset()
    {
        _hashToSlot.Clear();
        _size = 0;
        if (_localValues is not null) Array.Clear(_localValues);
        if (_localStores is not null) Array.Clear(_localStores);
    }

    private int FindMinSlot()
    {
        int minIndex = 0;
        long minCount = _counts[0];
        for (int i = 1; i < _size; i++)
        {
            if (_counts[i] < minCount)
            {
                minIndex = i;
                minCount = _counts[i];
            }
        }
        return minIndex;
    }

    private long ComputeHash(DataValue value, IValueStore store)
    {
        if (_isStringLike)
        {
            ulong cached = value.RawContentHash;
            if (cached != 0) return unchecked((long)cached);
            // Arena-slice values without a cached hash — hash UTF-8 bytes directly.
            return unchecked((long)XxHash64.HashToUInt64(value.AsUtf8Span(store)));
        }

        return _kind switch
        {
            DataKind.Int64 => value.AsInt64(),
            DataKind.UInt64 => unchecked((long)value.AsUInt64()),
            DataKind.Float64 => BitConverter.DoubleToInt64Bits(value.AsFloat64()),
            DataKind.Float32 => BitConverter.SingleToInt32Bits(value.AsFloat32()),
            DataKind.UInt8 => value.AsUInt8(),
            DataKind.Int8 => value.AsInt8(),
            DataKind.Int16 => value.AsInt16(),
            DataKind.UInt16 => value.AsUInt16(),
            DataKind.Int32 => value.AsInt32(),
            DataKind.UInt32 => value.AsUInt32(),
            DataKind.Boolean => value.AsBoolean() ? 1 : 0,
            DataKind.Date => value.AsDate().ToDateTime(TimeOnly.MinValue).Ticks,
            DataKind.TimestampTz => value.AsTimestampTz().UtcTicks,
            DataKind.Timestamp => value.AsTimestamp().Ticks,
            DataKind.Time => value.AsTime().Ticks,
            DataKind.Duration => value.AsDuration().Ticks,
            DataKind.Uuid => value.AsUuid().GetHashCode(),
            _ => 0
        };
    }

    /// <summary>Display string for a numeric slot key. Global string slots use
    /// <see cref="GlobalStringAt"/> instead.</summary>
    public string KeyDisplayAt(int index)
    {
        long key = _hashes[index];
        return _kind switch
        {
            DataKind.Int64 => key.ToString(),
            DataKind.UInt64 => unchecked((ulong)key).ToString(),
            DataKind.Float64 => BitConverter.Int64BitsToDouble(key).ToString("G"),
            DataKind.Float32 => BitConverter.Int32BitsToSingle((int)key).ToString("G"),
            DataKind.UInt8 => ((byte)key).ToString(),
            DataKind.Int8 => ((sbyte)key).ToString(),
            DataKind.Int16 => ((short)key).ToString(),
            DataKind.UInt16 => ((ushort)key).ToString(),
            DataKind.Int32 => ((int)key).ToString(),
            DataKind.UInt32 => ((uint)key).ToString(),
            DataKind.Boolean => (key != 0).ToString(),
            DataKind.Date => DateOnly.FromDateTime(new DateTime(key)).ToString("O"),
            DataKind.TimestampTz => new DateTime(key, DateTimeKind.Utc).ToString("O"),
            DataKind.Timestamp => new DateTime(key, DateTimeKind.Unspecified).ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF"),
            DataKind.Time => TimeOnly.FromTimeSpan(new TimeSpan(key)).ToString("O"),
            DataKind.Duration => new TimeSpan(key).ToString(),
            _ => key.ToString()
        };
    }
}
