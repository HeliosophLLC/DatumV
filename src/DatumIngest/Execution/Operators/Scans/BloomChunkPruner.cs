using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Indexing.Bloom;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators.Scans;

/// <summary>
/// Bloom-filter chunk pruner: skips a chunk when none of the supplied
/// build-side join key values could possibly be present according to the
/// chunk's per-column bloom filters.
/// </summary>
/// <remarks>
/// Source: <see cref="JoinOperator"/> calls <c>AddBloomPruningKeys</c> on
/// each reachable scan during its build phase. This pruner then probes the
/// per-chunk bloom filters with those keys; if every value's <c>MayContain</c>
/// returns <see langword="false"/> for a given column, the chunk is skipped.
/// </remarks>
internal sealed class BloomChunkPruner : IChunkPruner
{
    private readonly IReadOnlyDictionary<string, IReadOnlyCollection<DataValue>> _pruningKeys;
    private readonly BloomFilterSet _bloomFilters;
    private readonly string _tableName;

    public BloomChunkPruner(
        IReadOnlyDictionary<string, IReadOnlyCollection<DataValue>> pruningKeys,
        BloomFilterSet bloomFilters,
        string tableName)
    {
        _pruningKeys = pruningKeys;
        _bloomFilters = bloomFilters;
        _tableName = tableName;
    }

    public bool ShouldPrune(int chunkIndex, IndexChunk chunk, Arena arena)
    {
        foreach (KeyValuePair<string, IReadOnlyCollection<DataValue>> entry in _pruningKeys)
        {
            if (!_bloomFilters.TryGetFilter(entry.Key, chunkIndex, out BloomFilter? filter))
            {
                continue;
            }

            bool anyMayMatch = false;
            foreach (DataValue keyValue in entry.Value)
            {
                if (filter.MayContain(keyValue, arena))
                {
                    anyMayMatch = true;
                    break;
                }
            }

            if (!anyMayMatch)
            {
                if (chunkIndex == 0)
                {
                    DatumActivity.Operators.Trace($"SCAN PRUNE chunk=0  reason=bloom  table={_tableName}");
                }
                return true;
            }
        }
        return false;
    }
}
