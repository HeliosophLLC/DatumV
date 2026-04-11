using DatumIngest.Diagnostics;
using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators.Scans;

/// <summary>
/// Bitmap-index chunk pruner driven by filter literals. For equality and IN
/// predicates against bitmap-indexed columns, checks whether the chunk
/// contains the literal at all; if none do, the chunk is skipped.
/// </summary>
/// <remarks>
/// The recursive walker lives in <see cref="PredicatePruningAnalyzer"/>; this
/// pruner is just the dispatch shell that adapts it to <see cref="IChunkPruner"/>
/// + handles the tracer write on chunk 0.
/// </remarks>
internal sealed class BitmapChunkPruner : IChunkPruner
{
    private readonly Expression _filterHint;
    private readonly BitmapIndexSet _bitmapIndexes;
    private readonly string _tableName;

    public BitmapChunkPruner(Expression filterHint, BitmapIndexSet bitmapIndexes, string tableName)
    {
        _filterHint = filterHint;
        _bitmapIndexes = bitmapIndexes;
        _tableName = tableName;
    }

    public bool ShouldPrune(int chunkIndex, IndexChunk chunk, Arena arena)
    {
        if (!PredicatePruningAnalyzer.CanPruneBitmap(_filterHint, _bitmapIndexes, chunkIndex, arena))
        {
            return false;
        }

        if (chunkIndex == 0)
        {
            DatumActivity.Operators.Trace($"SCAN PRUNE chunk=0  reason=bitmap  table={_tableName}");
        }
        return true;
    }
}
