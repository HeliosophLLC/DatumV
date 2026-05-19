namespace Heliosoph.DatumV.Manifest;

using Heliosoph.DatumV.Indexing;

/// <summary>
/// Per-column statistics derived live from a <see cref="SourceIndex"/>'s per-chunk
/// state, without consulting the cached <c>.datum-manifest</c>. Fed into
/// <see cref="FeatureManifest"/> overlays so cheap fields (count, null ratio,
/// distinct estimate) stay fresh through every mutation while expensive fields
/// (top-K, quantiles, histogram, entropy, kind-specific summaries) remain in the
/// cache and refresh on <c>ANALYZE</c>.
/// </summary>
/// <param name="Count">Sum of non-null row counts across all chunks (gross — does not subtract tombstones).</param>
/// <param name="NullCount">Sum of null counts across all chunks.</param>
/// <param name="EstimatedDistinctCount">
/// Upper bound on distinct values: sum of per-chunk HyperLogLog cardinality estimates,
/// capped at <see cref="Count"/> + <see cref="NullCount"/>. PR13d's chunk statistics
/// don't currently propagate the HLL state, so a true cross-chunk merge isn't possible
/// without a format change; the sum-with-cap path overestimates but stays useful for
/// planner selectivity decisions where conservative bounds are better than stale ones.
/// </param>
public sealed record LiveColumnStats(
    long Count,
    long NullCount,
    long EstimatedDistinctCount)
{
    /// <summary>
    /// Computes a <see cref="LiveColumnStats"/> for the named column from a
    /// <see cref="SourceIndex"/>'s chunks. Returns <see langword="null"/> when the
    /// column has no chunk-level statistics in the index (e.g. a column that
    /// dropped out of indexing for a reason like sidecar-backed payloads).
    /// </summary>
    public static LiveColumnStats? ComputeFromIndex(SourceIndex index, string columnName)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrEmpty(columnName);

        long nonNull = 0;
        long nullCount = 0;
        long cardinalitySum = 0;
        bool sawChunk = false;

        foreach (IndexChunk chunk in index.Chunks)
        {
            if (!chunk.ColumnStatistics.TryGetValue(columnName, out ChunkColumnStatistics? stats))
            {
                continue;
            }

            sawChunk = true;
            nullCount += stats.NullCount;
            nonNull += Math.Max(0, stats.RowCount - stats.NullCount);
            cardinalitySum += stats.EstimatedCardinality;
        }

        if (!sawChunk)
        {
            return null;
        }

        long total = nonNull + nullCount;
        long cappedCardinality = cardinalitySum > total ? total : cardinalitySum;

        return new LiveColumnStats(
            Count: nonNull,
            NullCount: nullCount,
            EstimatedDistinctCount: cappedCardinality);
    }
}
