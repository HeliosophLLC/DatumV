using DatumIngest.Catalog;
using DatumIngest.Diagnostics;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators.Scans;

/// <summary>
/// Sorted-index chunk pruner driven by filter literals. Walks the filter
/// expression tree extracting equality / comparison / BETWEEN / IN predicates
/// against sorted-indexed columns; if any predicate proves the chunk can't
/// contain a matching key, the chunk is skipped.
/// </summary>
/// <remarks>
/// The recursive walker lives in <see cref="PredicatePruningAnalyzer"/>; this
/// pruner is just the dispatch shell that adapts it to <see cref="IChunkPruner"/>
/// + handles the tracer write on chunk 0.
/// </remarks>
internal sealed class SortedFilterChunkPruner : IChunkPruner
{
    private readonly Expression _filterHint;
    private readonly ITableProvider _provider;
    private readonly string _tableName;

    public SortedFilterChunkPruner(Expression filterHint, ITableProvider provider, string tableName)
    {
        _filterHint = filterHint;
        _provider = provider;
        _tableName = tableName;
    }

    public bool ShouldPrune(int chunkIndex, IndexChunk chunk, Arena arena)
    {
        if (!PredicatePruningAnalyzer.CanPruneSorted(_filterHint, _provider, chunkIndex, arena))
        {
            return false;
        }

        if (chunkIndex == 0)
        {
            DatumActivity.Operators.Trace($"SCAN PRUNE chunk=0  reason=column_index  table={_tableName}");
        }
        return true;
    }
}
