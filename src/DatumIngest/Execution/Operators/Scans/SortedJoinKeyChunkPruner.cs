using DatumIngest.Catalog;
using DatumIngest.Diagnostics;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators.Scans;

/// <summary>
/// Sorted-index chunk pruner driven by join keys. For each column with a
/// sorted <see cref="IColumnIndex"/>, checks whether any build-side key
/// value's matching-chunk set includes this chunk; if none do, the chunk
/// is skipped.
/// </summary>
/// <remarks>
/// Symmetric to <see cref="BloomChunkPruner"/> — same key-set source
/// (<c>JoinOperator.AddSortedIndexPruningKeys</c>), different index type.
/// </remarks>
internal sealed class SortedJoinKeyChunkPruner : IChunkPruner
{
    private readonly IReadOnlyDictionary<string, IReadOnlyCollection<DataValue>> _pruningKeys;
    private readonly ITableProvider _provider;
    private readonly string _tableName;

    public SortedJoinKeyChunkPruner(
        IReadOnlyDictionary<string, IReadOnlyCollection<DataValue>> pruningKeys,
        ITableProvider provider,
        string tableName)
    {
        _pruningKeys = pruningKeys;
        _provider = provider;
        _tableName = tableName;
    }

    public bool ShouldPrune(int chunkIndex, IndexChunk chunk, Arena arena)
    {
        foreach (KeyValuePair<string, IReadOnlyCollection<DataValue>> entry in _pruningKeys)
        {
            if (!_provider.TryGetColumnIndex(entry.Key, out IColumnIndex? index))
            {
                continue;
            }

            bool anyPresent = false;
            foreach (DataValue keyValue in entry.Value)
            {
                IReadOnlySet<int> matchingChunks = index.FindChunksContaining(keyValue);
                if (matchingChunks.Contains(chunkIndex))
                {
                    anyPresent = true;
                    break;
                }
            }

            if (!anyPresent)
            {
                if (chunkIndex == 0)
                {
                    DatumActivity.Operators.Trace($"SCAN PRUNE chunk=0  reason=sorted_join_key  table={_tableName}");
                }
                return true;
            }
        }
        return false;
    }
}
