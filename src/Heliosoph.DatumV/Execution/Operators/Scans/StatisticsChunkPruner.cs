using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Operators.Scans;

/// <summary>
/// Statistics (zone-map) chunk pruner: checks filter predicates against the
/// chunk's column min/max statistics via
/// <see cref="StatisticsPredicateEvaluator.CanSkipPartition"/>.
/// </summary>
internal sealed class StatisticsChunkPruner : IChunkPruner
{
    private readonly Expression _filterHint;
    private readonly string _tableName;

    public StatisticsChunkPruner(Expression filterHint, string tableName)
    {
        _filterHint = filterHint;
        _tableName = tableName;
    }

    public bool ShouldPrune(int chunkIndex, IndexChunk chunk, Arena arena)
    {
        using ColumnStatisticsRangeLookup statistics = chunk.CreateStatisticsLookup();

        if (!StatisticsPredicateEvaluator.CanSkipPartition(_filterHint, statistics, arena))
        {
            return false;
        }

        if (chunkIndex == 0)
        {
            DatumActivity.Operators.Trace(
                $"SCAN PRUNE chunk=0  reason=zonemap  table={_tableName}  statsKeys=[{string.Join(",", statistics.Keys)}]");
        }
        return true;
    }
}
