using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators.Scans;

/// <summary>
/// A single chunk-level pruning strategy evaluated by
/// <see cref="ScanOperator"/> during the index-pruning path. The operator
/// builds a list of pruners once per execution (filter-based, bloom-based,
/// sorted-index-based, bitmap-based) and asks each one whether each
/// candidate chunk can be skipped.
/// </summary>
/// <remarks>
/// Pruners are evaluated in order; the first one to return
/// <see langword="true"/> wins and the remaining pruners for that chunk are
/// skipped. Each pruner is responsible for writing its own
/// <c>DatumActivity.Operators.Trace</c> entry when it prunes chunk 0 —
/// the operator's main loop just decides whether to add the chunk to the
/// active range list.
/// </remarks>
internal interface IChunkPruner
{
    /// <summary>
    /// Returns <see langword="true"/> when this pruner has determined that
    /// <paramref name="chunk"/> contains no rows that could satisfy whatever
    /// constraint the pruner enforces.
    /// </summary>
    bool ShouldPrune(int chunkIndex, IndexChunk chunk, Arena arena);
}
