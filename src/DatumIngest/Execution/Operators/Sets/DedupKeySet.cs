using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Execution.Operators.Sets;

/// <summary>
/// Encapsulates the single-column vs composite-column duality of the distinct-key
/// sets used by UNION / INTERSECT / EXCEPT DISTINCT iterators. Replaces the
/// hand-rolled <c>HashSet&lt;DataValue&gt;? singleSet</c> + <c>HashSet&lt;CompositeKey&gt;? compositeSet</c>
/// pair + <c>columnCount == 1</c> branches that repeated at every set-op emit site.
/// </summary>
/// <remarks>
/// <para>
/// Initialise once per execution after the first non-empty input batch reveals the
/// column count; the set then dispatches single-vs-composite internally. Composite
/// inserts go through a <see cref="CompositeKeyComparer"/> alternate lookup so
/// per-probe row scans only allocate a <see cref="CompositeKey"/> on first sight.
/// </para>
/// <para>
/// <b>Key binding.</b> Composite keys are either allocated on the managed heap
/// (when constructed with <see cref="CompositeKeyComparer.Instance"/>) or rented
/// from the <see cref="Pool"/> (when constructed with
/// <see cref="CompositeKeyComparer.ForPool"/>). Pool-bound sets MUST be disposed
/// to balance the pool's rent counter; the singleton-bound sets dispose to a no-op.
/// </para>
/// <para>
/// <b>Shared scratch.</b> Intersect/Except DISTINCT carry two sets simultaneously
/// (right-side build + emit-dedup). The second constructor lets the second set
/// share the first's composite-key scratch buffer — only one owner returns the
/// buffer on dispose.
/// </para>
/// </remarks>
internal sealed class DedupKeySet : IDisposable
{
    private readonly Pool _pool;
    private readonly CompositeKeyComparer _comparer;
    private readonly bool _poolBoundKeys;
    private readonly bool _ownsScratch;

    private int _columnCount = -1;
    private HashSet<DataValue>? _singleSet;
    private HashSet<CompositeKey>? _compositeSet;
    private HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> _compositeLookup;
    private DataValue[]? _scratch;

    /// <summary>
    /// Constructs a stand-alone set. When <paramref name="poolBoundKeys"/> is
    /// <see langword="true"/>, composite keys are rented from <paramref name="pool"/>
    /// and returned on dispose; otherwise they are heap-allocated via
    /// <see cref="CompositeKeyComparer.Instance"/>.
    /// </summary>
    public DedupKeySet(Pool pool, bool poolBoundKeys)
    {
        _pool = pool;
        _poolBoundKeys = poolBoundKeys;
        _comparer = poolBoundKeys ? CompositeKeyComparer.ForPool(pool) : CompositeKeyComparer.Instance;
        _ownsScratch = true;
    }

    /// <summary>
    /// Constructs a set that shares the supplied <paramref name="comparer"/> and
    /// (optionally) <paramref name="sharedScratch"/> with another set — used when
    /// two co-living sets (e.g. right-set + emit-dedup) should consult the same
    /// pool-bound key arena and avoid renting a second composite-key scratch.
    /// The shared scratch buffer is owned by the first set; this one's dispose
    /// does not return it.
    /// </summary>
    public DedupKeySet(Pool pool, CompositeKeyComparer comparer, DataValue[]? sharedScratch)
    {
        _pool = pool;
        _comparer = comparer;
        _poolBoundKeys = !ReferenceEquals(comparer, CompositeKeyComparer.Instance);
        _scratch = sharedScratch;
        _ownsScratch = false;
    }

    /// <summary>Whether <see cref="Initialize"/> has been called.</summary>
    public bool IsInitialized => _columnCount != -1;

    /// <summary>The column count discovered on first row; -1 if not yet initialised.</summary>
    public int ColumnCount => _columnCount;

    /// <summary>The comparer threading this set (for sharing with co-living sets).</summary>
    public CompositeKeyComparer Comparer => _comparer;

    /// <summary>
    /// The shared scratch buffer for composite-key probes. Non-null only for the
    /// composite path after <see cref="Initialize"/>; the first-constructed set
    /// owns it. Exposed so a second co-living set can share it.
    /// </summary>
    public DataValue[]? Scratch => _scratch;

    /// <summary>
    /// Allocates the underlying single- or composite-keyed set for the given
    /// column count. Idempotent guard: a second call with a different count
    /// throws because schema drift mid-execution would corrupt the keys.
    /// </summary>
    public void Initialize(int columnCount)
    {
        if (_columnCount == columnCount) return;
        if (_columnCount != -1)
        {
            throw new InvalidOperationException(
                $"DedupKeySet already initialised with columnCount={_columnCount}; cannot re-initialise with {columnCount}.");
        }

        _columnCount = columnCount;
        if (columnCount == 1)
        {
            _singleSet = new HashSet<DataValue>();
        }
        else
        {
            _compositeSet = new HashSet<CompositeKey>(_comparer);
            _compositeLookup = _compositeSet.GetAlternateLookup<ReadOnlySpan<DataValue>>();
            if (_scratch is null)
            {
                if (!_ownsScratch)
                {
                    // Shared-scratch ctor was used but the caller passed a null scratch —
                    // typically because the scratch owner hadn't been Initialized yet at
                    // ctor time. Silently renting here would mark _ownsScratch=false, so
                    // Dispose would never return the buffer. Fast-fail instead.
                    throw new InvalidOperationException(
                        "DedupKeySet was constructed with a shared scratch but the scratch was null. " +
                        "Initialize the scratch-owning set first, or construct this set inline once the " +
                        "owner's Scratch is populated.");
                }
                _scratch = _pool.RentDataValues(columnCount);
            }
        }
    }

    /// <summary>
    /// Adds the row's key. Returns <see langword="true"/> if the key was new,
    /// <see langword="false"/> if a previously-added key matched.
    /// </summary>
    public bool Add(Row row)
    {
        if (_columnCount == 1) return _singleSet!.Add(row[0]);
        return _compositeLookup.Add(FillScratch(row));
    }

    /// <summary>Whether the row's key is present in this set.</summary>
    public bool Contains(Row row)
    {
        if (_columnCount == 1) return _singleSet!.Contains(row[0]);
        return _compositeLookup.Contains(FillScratch(row));
    }

    /// <summary>
    /// Whether the row's key is present in this set or — if non-null —
    /// the <paramref name="fallback"/> set. Used by partition-drain probes
    /// where pool-bound composite keys couldn't be safely copied into the
    /// partition-local set (see <see cref="SeedPartitionInto"/>), so the
    /// outer in-memory set must be consulted as a fallback.
    /// </summary>
    public bool Contains(Row row, DedupKeySet? fallback)
    {
        if (Contains(row)) return true;
        return fallback is not null && fallback.Contains(row);
    }

    /// <summary>
    /// Returns the hash code this set would use to route the row to a spill
    /// partition. Single-column uses <see cref="DataValue.GetHashCode"/>;
    /// composite uses the same algorithm as <see cref="CompositeKey.GetHashCode"/>
    /// so partition routing stays consistent between insert-time and probe-time.
    /// </summary>
    public int GetKeyHash(Row row)
    {
        if (_columnCount == 1) return row[0].GetHashCode();
        return _comparer.GetHashCode(FillScratch(row));
    }

    /// <summary>
    /// Copies in-memory keys whose hash routes to <paramref name="partition"/>
    /// into <paramref name="target"/>. Returns <see langword="true"/> if every
    /// matching key was copied; <see langword="false"/> if pool-bound composite
    /// keys had to be skipped to avoid double-return on dispose — in which case
    /// the caller must probe this set as a fallback at lookup time.
    /// </summary>
    public bool SeedPartitionInto(int partition, int partitionCount, DedupKeySet target)
    {
        if (_columnCount == 1)
        {
            foreach (DataValue key in _singleSet!)
            {
                if (AssignPartition(key.GetHashCode(), partitionCount) == partition)
                {
                    target._singleSet!.Add(key);
                }
            }
            return true;
        }

        if (_poolBoundKeys)
        {
            // Skip composite seed when keys are pool-bound: adding the same
            // CompositeKey to two sets would double-return its parts array on
            // dispose. Caller must consult this set via Contains(row, fallback).
            return false;
        }

        foreach (CompositeKey key in _compositeSet!)
        {
            if (AssignPartition(key.GetHashCode(), partitionCount) == partition)
            {
                target._compositeSet!.Add(key);
            }
        }
        return true;
    }

    private static int AssignPartition(int hashCode, int partitionCount)
        => (int)((uint)hashCode % (uint)partitionCount);

    private ReadOnlySpan<DataValue> FillScratch(Row row)
    {
        DataValue[] scratch = _scratch!;
        for (int i = 0; i < _columnCount; i++) scratch[i] = row[i];
        return scratch.AsSpan(0, _columnCount);
    }

    /// <summary>
    /// Returns pool-bound composite keys (if any) and the composite-key scratch
    /// buffer (if owned). Safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public void Dispose()
    {
        if (_poolBoundKeys && _compositeSet is not null)
        {
            _comparer.ReturnPooledKeys(_compositeSet);
            _compositeSet = null;
        }

        if (_ownsScratch && _scratch is not null)
        {
            _pool.ReturnDataValues(_scratch);
            _scratch = null;
        }
    }
}
