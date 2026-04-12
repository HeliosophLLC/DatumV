using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators.Sets;

/// <summary>
/// Encapsulates the single-column vs composite-column duality of the counted
/// multisets used by INTERSECT / EXCEPT ALL iterators. Replaces the hand-rolled
/// <c>Dictionary&lt;DataValue,int&gt;? singleCounts</c> +
/// <c>Dictionary&lt;CompositeKey,int&gt;? compositeCounts</c> pair plus the
/// <c>columnCount == 1</c> branches that repeated at every increment/decrement site.
/// </summary>
/// <remarks>
/// <para>
/// Composite keys are always rented from the <see cref="Pool"/> (the ALL variants
/// are long-lived enough that the per-key heap cost outweighs the pool's overhead);
/// dispose returns them to balance the rent counter.
/// </para>
/// <para>
/// <b>Partition-drain fallback.</b> Unlike <see cref="DedupKeySet"/>, the counted
/// multiset cannot be safely seeded into a partition-local copy — decrementing the
/// copy would diverge from the original count and produce wrong emission. Instead,
/// the partition-local instance is always empty at the start of drain; decrement
/// probes consult <see cref="TryDecrement(Row, CountedKeyMultiset?)"/> which tries
/// the partition-local first and falls through to the in-memory outer set.
/// </para>
/// </remarks>
internal sealed class CountedKeyMultiset : IDisposable
{
    private readonly Pool _pool;
    private readonly CompositeKeyComparer _comparer;
    private readonly bool _ownsScratch;

    private int _columnCount = -1;
    private Dictionary<DataValue, int>? _singleCounts;
    private Dictionary<CompositeKey, int>? _compositeCounts;
    private Dictionary<CompositeKey, int>.AlternateLookup<ReadOnlySpan<DataValue>> _compositeLookup;
    private DataValue[]? _scratch;

    /// <summary>Constructs a stand-alone multiset; composite keys are pool-bound.</summary>
    public CountedKeyMultiset(Pool pool)
    {
        _pool = pool;
        _comparer = CompositeKeyComparer.ForPool(pool);
        _ownsScratch = true;
    }

    /// <summary>
    /// Constructs a partition-local multiset sharing the outer set's comparer and
    /// composite-key scratch buffer. The outer set owns the scratch; this instance's
    /// dispose does not return it.
    /// </summary>
    public CountedKeyMultiset(Pool pool, CompositeKeyComparer comparer, DataValue[]? sharedScratch)
    {
        _pool = pool;
        _comparer = comparer;
        _scratch = sharedScratch;
        _ownsScratch = false;
    }

    /// <summary>Whether <see cref="Initialize"/> has been called.</summary>
    public bool IsInitialized => _columnCount != -1;

    /// <summary>The column count discovered on first row; -1 if not yet initialised.</summary>
    public int ColumnCount => _columnCount;

    /// <summary>The comparer threading this multiset (for sharing with a partition-local set).</summary>
    public CompositeKeyComparer Comparer => _comparer;

    /// <summary>The composite-key scratch buffer (non-null only for the composite path).</summary>
    public DataValue[]? Scratch => _scratch;

    /// <summary>
    /// Allocates the underlying single- or composite-keyed dictionary for the
    /// given column count. Idempotent guard: a second call with a different count
    /// throws.
    /// </summary>
    public void Initialize(int columnCount)
    {
        if (_columnCount == columnCount) return;
        if (_columnCount != -1)
        {
            throw new InvalidOperationException(
                $"CountedKeyMultiset already initialised with columnCount={_columnCount}; cannot re-initialise with {columnCount}.");
        }

        _columnCount = columnCount;
        if (columnCount == 1)
        {
            _singleCounts = new Dictionary<DataValue, int>();
        }
        else
        {
            _compositeCounts = new Dictionary<CompositeKey, int>(_comparer);
            _compositeLookup = _compositeCounts.GetAlternateLookup<ReadOnlySpan<DataValue>>();
            if (_scratch is null)
            {
                _scratch = _pool.RentDataValues(columnCount);
            }
        }
    }

    /// <summary>Increments the count for the row's key by one (insert if absent).</summary>
    public void Increment(Row row)
    {
        if (_columnCount == 1)
        {
            DataValue key = row[0];
            _singleCounts![key] = _singleCounts.GetValueOrDefault(key) + 1;
            return;
        }

        ReadOnlySpan<DataValue> span = FillScratch(row);
        _compositeLookup.TryGetValue(span, out int count);
        _compositeLookup[span] = count + 1;
    }

    /// <summary>
    /// Decrements the row's key count by one if it is currently positive.
    /// Returns <see langword="true"/> when a count was consumed (the row matched a
    /// remaining occurrence), <see langword="false"/> when the key was absent or
    /// already exhausted.
    /// </summary>
    public bool TryDecrement(Row row)
    {
        if (_columnCount == -1) return false;

        if (_columnCount == 1)
        {
            DataValue key = row[0];
            if (_singleCounts!.TryGetValue(key, out int count) && count > 0)
            {
                _singleCounts[key] = count - 1;
                return true;
            }
            return false;
        }

        ReadOnlySpan<DataValue> span = FillScratch(row);
        if (_compositeLookup.TryGetValue(span, out int compositeCount) && compositeCount > 0)
        {
            _compositeLookup[span] = compositeCount - 1;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Tries to decrement this multiset; on miss, falls through to
    /// <paramref name="fallback"/>. Returns whether either decrement succeeded.
    /// Used by the partition-drain probe where a partition-local multiset
    /// shadows the outer in-memory one.
    /// </summary>
    public bool TryDecrement(Row row, CountedKeyMultiset? fallback)
    {
        if (TryDecrement(row)) return true;
        return fallback is not null && fallback.TryDecrement(row);
    }

    /// <summary>
    /// Returns the hash code this multiset would use to route the row to a spill
    /// partition. Matches the single/composite hash algorithms used by
    /// <see cref="DataValue.GetHashCode"/> / <see cref="CompositeKeyComparer.GetHashCode(ReadOnlySpan{DataValue})"/>.
    /// </summary>
    public int GetKeyHash(Row row)
    {
        if (_columnCount == 1) return row[0].GetHashCode();
        return _comparer.GetHashCode(FillScratch(row));
    }

    private ReadOnlySpan<DataValue> FillScratch(Row row)
    {
        DataValue[] scratch = _scratch!;
        for (int i = 0; i < _columnCount; i++) scratch[i] = row[i];
        return scratch.AsSpan(0, _columnCount);
    }

    /// <summary>
    /// Returns pool-bound composite keys and the scratch buffer (if owned).
    /// Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_compositeCounts is not null)
        {
            _comparer.ReturnPooledKeys(_compositeCounts.Keys);
            _compositeCounts = null;
        }

        if (_ownsScratch && _scratch is not null)
        {
            _pool.ReturnDataValues(_scratch);
            _scratch = null;
        }
    }
}
