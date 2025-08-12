namespace DatumIngest.Execution;

/// <summary>
/// Chunk-level pruning techniques that can eliminate data before row-level reads.
/// </summary>
public enum PruningTechnique
{
    /// <summary>Min/max statistics bounds per chunk.</summary>
    StatisticsPruning,

    /// <summary>Bloom filter membership test per chunk.</summary>
    BloomFilterPruning,

    /// <summary>Sorted index value lookup per chunk.</summary>
    SortedIndexPruning,

    /// <summary>Exact row seek via sorted index (no full-chunk read).</summary>
    ExactSeek,
}
