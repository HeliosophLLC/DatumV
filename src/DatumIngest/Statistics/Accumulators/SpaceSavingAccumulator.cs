using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Statistics.Accumulators;

/// <summary>
/// Consolidated accumulator that produces <c>top_k</c>, <c>entropy</c>, and
/// <c>categorical_diagnostics</c> from a single bounded-memory Space-Saving sketch.
/// Replaces <c>TopKAccumulator</c>, <c>EntropyAccumulator</c>, and
/// <c>CategoricalDiagnosticsAccumulator</c>.
/// </summary>
/// <remarks>
/// <para>
/// Holds two sketches: a per-row-group <em>local</em> sketch that fills as
/// <see cref="Add"/> is called on each batch's rows, and a <em>global</em> sketch
/// that aggregates the locals. The ingester calls
/// <see cref="BeforeRowGroupFlush"/> before the writer resets its arena; the
/// accumulator merges the local into the global at that point, materializing
/// managed strings only for newly-promoted keys. Subsequent row groups reuse the
/// local sketch arrays without reallocation.
/// </para>
/// <para>
/// Memory cost per column: two preallocated <see cref="SpaceSavingSketch"/>
/// instances sized to <see cref="DefaultCapacity"/> = 1024 slots. No dictionary
/// resize chains (both dicts are pre-sized at construction), so allocation on
/// the hot Add loop is zero.
/// </para>
/// </remarks>
public sealed class SpaceSavingAccumulator : IStatisticAccumulator
{
    /// <summary>Default sketch capacity (slots per local + global).</summary>
    public const int DefaultCapacity = 1024;

    /// <summary>
    /// Categories observed fewer than this many times are classified as rare in
    /// <see cref="Accumulators.CategoricalDiagnosticsResult"/>. Matches the prior
    /// <c>CategoricalDiagnosticsAccumulator.RareThreshold</c> of 5.
    /// </summary>
    public const int RareThreshold = 5;

    private readonly int _k;
    private readonly DataKind _kind;
    private readonly SpaceSavingSketch _local;
    private readonly SpaceSavingSketch _global;

    private long _totalCount;
    private long _localUntrackedCount;
    private long _globalUntrackedCount;
    private bool _pendingMerge;

    /// <summary>
    /// Creates a Space-Saving accumulator for the given column.
    /// </summary>
    /// <param name="k">Top-K parameter — how many entries to expose in the top-K result.</param>
    /// <param name="kind">Column data kind; determines numeric vs string-keyed sketch layout.</param>
    /// <param name="capacity">Sketch capacity (slots). Defaults to <see cref="DefaultCapacity"/>.</param>
    public SpaceSavingAccumulator(int k, DataKind kind, int capacity = DefaultCapacity)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));
        if (capacity < k) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be >= k");

        _k = k;
        _kind = kind;
        _local = new SpaceSavingSketch(capacity, kind, isLocal: true);
        _global = new SpaceSavingSketch(capacity, kind, isLocal: false);
    }

    /// <inheritdoc />
    public void Add(DataValue value, IValueStore store)
    {
        if (value.IsNull) return;

        _totalCount++;
        if (!_local.AddLocal(value, store))
        {
            _localUntrackedCount++;
        }

        _pendingMerge = _local.Size > 0 || _localUntrackedCount > 0;
    }

    /// <summary>
    /// Merges the current row group's local sketch into the global sketch. Called
    /// by <see cref="StatisticsCollector.FlushRowGroup"/> before the writer's arena
    /// is reset — local-slot string offsets must still resolve at this moment.
    /// </summary>
    public void BeforeRowGroupFlush(IValueStore writerArenaStore)
    {
        if (!_pendingMerge) return;

        _global.MergeFromLocal(_local);
        _globalUntrackedCount += _localUntrackedCount;
        _localUntrackedCount = 0;
        _local.Reset();
        _pendingMerge = false;
    }

    /// <inheritdoc />
    public IEnumerable<StatisticResult> GetResults()
    {
        // If the Ingester's row-group flush hook hasn't been invoked (e.g. tests or
        // direct-AddRow callers), merge the local sketch now. The per-slot store refs
        // captured during Add are still valid in this scenario because no arena reset
        // has occurred. After a proper FlushRowGroup call, _pendingMerge is false and
        // this block is a no-op.
        if (_pendingMerge)
        {
            _global.MergeFromLocal(_local);
            _globalUntrackedCount += _localUntrackedCount;
            _localUntrackedCount = 0;
            _local.Reset();
            _pendingMerge = false;
        }

        bool approximate = _globalUntrackedCount > 0;

        yield return BuildTopK();
        yield return BuildEntropy(_globalUntrackedCount, approximate);
        yield return BuildCategoricalDiagnostics(_globalUntrackedCount, approximate);
    }

    private StatisticResult BuildTopK()
    {
        int size = _global.Size;
        int take = Math.Min(_k, size);
        List<KeyValuePair<string, long>> entries = new(take);

        if (take > 0)
        {
            int[] sorted = _global.GetSortedSlotIndices();
            for (int i = 0; i < take; i++)
            {
                int slot = sorted[i];
                string key = _kind == DataKind.String
                    ? _global.GlobalStringAt(slot)
                    : _global.KeyDisplayAt(slot);
                entries.Add(new KeyValuePair<string, long>(key, _global.CountAt(slot)));
            }
        }

        return new StatisticResult("top_k", new TopKResult(entries));
    }

    private StatisticResult BuildEntropy(long untrackedCount, bool approximate)
    {
        if (_totalCount == 0)
        {
            return new StatisticResult("entropy", new EntropyResult(0.0, false));
        }

        double entropy = 0.0;
        for (int i = 0; i < _global.Size; i++)
        {
            long count = _global.CountAt(i);
            if (count > 0)
            {
                double p = (double)count / _totalCount;
                entropy -= p * Math.Log2(p);
            }
        }

        // Lump all untracked mass into a single "other" bucket — a lower bound on
        // true entropy (splitting "other" into its real components would only add
        // more disorder).
        if (untrackedCount > 0)
        {
            double p = (double)untrackedCount / _totalCount;
            entropy -= p * Math.Log2(p);
        }

        return new StatisticResult("entropy", new EntropyResult(entropy, approximate));
    }

    private StatisticResult BuildCategoricalDiagnostics(long untrackedCount, bool approximate)
    {
        if (_totalCount == 0)
        {
            return new StatisticResult(
                "categorical_diagnostics",
                new CategoricalDiagnosticsResult(0.0, 0.0, 0, 0, false));
        }

        // Coverage: sum of top-K slot counts / total non-null count.
        int[] sorted = _global.GetSortedSlotIndices();
        long topKSum = 0;
        int topCount = Math.Min(_k, sorted.Length);
        for (int i = 0; i < topCount; i++)
        {
            topKSum += _global.CountAt(sorted[i]);
        }
        double coverageTopK = (double)topKSum / _totalCount;

        // Rare categories = tracked slots with count < threshold. Lower bound on
        // truly-rare if the global sketch evicted rare keys during merges.
        long rareCount = 0;
        for (int i = 0; i < _global.Size; i++)
        {
            if (_global.CountAt(i) < RareThreshold) rareCount++;
        }

        // Total category count: tracked slots. A lower bound if we ever evicted;
        // callers needing an accurate estimate should consult the HLL cardinality.
        long totalCategoryCount = _global.Size;
        double rareRatio = totalCategoryCount > 0 ? (double)rareCount / totalCategoryCount : 0.0;

        return new StatisticResult(
            "categorical_diagnostics",
            new CategoricalDiagnosticsResult(
                coverageTopK, rareRatio, rareCount, totalCategoryCount, approximate));
    }
}

/// <summary>
/// Top-K frequency result. Entries sorted by frequency descending.
/// </summary>
/// <param name="Entries">Value-frequency pairs.</param>
public sealed record TopKResult(IReadOnlyList<KeyValuePair<string, long>> Entries);

/// <summary>
/// Shannon entropy result in bits. Higher values indicate more disorder.
/// </summary>
/// <param name="Value">Shannon entropy in bits.</param>
/// <param name="Approximate">True if based on a sketch with untracked mass.</param>
public sealed record EntropyResult(double Value, bool Approximate);

/// <summary>
/// Categorical diagnostic result: top-K coverage and rare category ratio.
/// </summary>
/// <param name="CoverageTopK">Fraction of total observations covered by the K most frequent categories.</param>
/// <param name="RareRatio">Fraction of tracked categories with fewer than <see cref="SpaceSavingAccumulator.RareThreshold"/> observations.</param>
/// <param name="RareCategoryCount">Number of tracked categories classified as rare (lower bound).</param>
/// <param name="TotalCategoryCount">Number of tracked categories (lower bound on true distinct count).</param>
/// <param name="Approximate">True whenever untracked mass exists or the sketch evicted keys.</param>
public sealed record CategoricalDiagnosticsResult(
    double CoverageTopK,
    double RareRatio,
    long RareCategoryCount,
    long TotalCategoryCount,
    bool Approximate);
