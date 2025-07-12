using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Reads rows from a table provider, applying projection pushdown
/// to skip unreferenced columns at the source. When the provider implements
/// <see cref="IFilterableTableProvider"/> and a filter hint is present,
/// the provider may skip entire partitions based on column statistics.
/// When a <see cref="SourceIndex"/> is available, chunk-level statistics
/// enable partition pruning for any provider type. Bloom filter pruning
/// allows join operators to pre-filter chunks by providing build-side key
/// values: chunks where no build-side key could possibly exist are skipped.
/// </summary>
public sealed class ScanOperator : IQueryOperator
{
    private readonly TableDescriptor _descriptor;
    private readonly IReadOnlySet<string>? _requiredColumns;
    private Expression? _filterHint;
    private SourceIndex? _sourceIndex;
    private Dictionary<string, IReadOnlyCollection<DataValue>>? _bloomPruningKeys;

    /// <summary>
    /// Creates a scan operator for the given table.
    /// </summary>
    /// <param name="descriptor">Table descriptor identifying the data source.</param>
    /// <param name="requiredColumns">Columns needed downstream; null means all columns.</param>
    public ScanOperator(TableDescriptor descriptor, IReadOnlySet<string>? requiredColumns)
    {
        _descriptor = descriptor;
        _requiredColumns = requiredColumns;
    }

    /// <summary>The table descriptor this operator scans.</summary>
    public TableDescriptor Descriptor => _descriptor;

    /// <summary>The set of columns requested for projection pushdown.</summary>
    public IReadOnlySet<string>? RequiredColumns => _requiredColumns;

    /// <summary>The advisory filter hint passed to filterable providers, or <c>null</c>.</summary>
    public Expression? FilterHint => _filterHint;

    /// <summary>The source index for chunk-based pruning, or <c>null</c> if none is available.</summary>
    public SourceIndex? SourceIndex => _sourceIndex;

    /// <summary>Total number of index chunks considered during the last execution.</summary>
    public int TotalIndexChunks { get; private set; }

    /// <summary>Number of index chunks pruned (skipped) during the last execution.</summary>
    public int PrunedIndexChunks { get; private set; }

    /// <summary>
    /// Adds an advisory filter predicate for statistics-based partition pruning.
    /// Multiple calls combine predicates with AND.
    /// </summary>
    /// <param name="predicate">The predicate to add as a filter hint.</param>
    public void AddFilterHint(Expression predicate)
    {
        _filterHint = _filterHint is null
            ? predicate
            : new BinaryExpression(_filterHint, BinaryOperator.And, predicate);
    }

    /// <summary>
    /// Attaches a source index for chunk-based partition pruning.
    /// </summary>
    /// <param name="index">The source index to use during execution.</param>
    public void SetSourceIndex(SourceIndex index)
    {
        _sourceIndex = index;
    }

    /// <summary>
    /// Registers join key values for bloom-filter-based chunk pruning.
    /// During execution, chunks whose bloom filters report that none of the
    /// provided key values could be present are skipped entirely.
    /// </summary>
    /// <param name="columnName">The column name to check bloom filters for.</param>
    /// <param name="keyValues">The build-side key values from the join partner.</param>
    public void AddBloomPruningKeys(string columnName, IReadOnlyCollection<DataValue> keyValues)
    {
        _bloomPruningKeys ??= new(StringComparer.OrdinalIgnoreCase);
        _bloomPruningKeys[columnName] = keyValues;
    }

    /// <summary>
    /// The most recent <see cref="IFilterableTableProvider"/> used during execution,
    /// or <c>null</c> if the provider is not filterable. Used by the explain/instrumentation
    /// layer to retrieve pruning statistics after execution.
    /// </summary>
    public IFilterableTableProvider? LastFilterableProvider { get; private set; }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        ITableProvider provider = context.Catalog.CreateProvider(_descriptor);

        IAsyncEnumerable<Row> rows;

        if (_filterHint is not null && provider is IFilterableTableProvider filterable)
        {
            LastFilterableProvider = filterable;
            rows = filterable.OpenAsync(_descriptor, _requiredColumns, _filterHint, cancellationToken);
        }
        else
        {
            rows = provider.OpenAsync(_descriptor, _requiredColumns, cancellationToken);
        }

        // When a source index is available and either a filter hint or bloom pruning
        // keys are present, apply chunk-level pruning.
        bool hasIndexPruning = _sourceIndex is not null
            && (_filterHint is not null || _bloomPruningKeys is not null);

        if (hasIndexPruning)
        {
            await foreach (Row row in ExecuteWithIndexPruningAsync(rows).ConfigureAwait(false))
            {
                yield return row;
            }
        }
        else
        {
            await foreach (Row row in rows.ConfigureAwait(false))
            {
                yield return row;
            }
        }
    }

    private async IAsyncEnumerable<Row> ExecuteWithIndexPruningAsync(IAsyncEnumerable<Row> rows)
    {
        IReadOnlyList<IndexChunk> chunks = _sourceIndex!.Chunks;
        BloomFilterSet? bloomFilters = _sourceIndex.BloomFilters;
        TotalIndexChunks = chunks.Count;
        PrunedIndexChunks = 0;

        // Build a set of non-pruned chunk row ranges.
        List<(long Start, long End)> activeRanges = new();

        for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            IndexChunk chunk = chunks[chunkIndex];
            bool pruned = false;

            // Statistics-based pruning: check filter predicates against min/max stats.
            if (!pruned && _filterHint is not null)
            {
                Dictionary<string, ColumnStatisticsRange> statistics = new(StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, ChunkColumnStatistics> entry in chunk.ColumnStatistics)
                {
                    statistics[entry.Key] = entry.Value.ToColumnStatisticsRange();
                }

                if (StatisticsPredicateEvaluator.CanSkipPartition(_filterHint, statistics))
                {
                    pruned = true;
                }
            }

            // Bloom-filter-based pruning: check if any build-side join key could
            // be present in this chunk. If no key could possibly match, skip it.
            if (!pruned && _bloomPruningKeys is not null && bloomFilters is not null)
            {
                foreach (KeyValuePair<string, IReadOnlyCollection<DataValue>> entry in _bloomPruningKeys)
                {
                    if (bloomFilters.TryGetFilter(entry.Key, chunkIndex, out BloomFilter? filter))
                    {
                        bool anyMayMatch = false;
                        foreach (DataValue keyValue in entry.Value)
                        {
                            if (filter.MayContain(keyValue))
                            {
                                anyMayMatch = true;
                                break;
                            }
                        }

                        if (!anyMayMatch)
                        {
                            pruned = true;
                            break;
                        }
                    }
                }
            }

            if (pruned)
            {
                PrunedIndexChunks++;
            }
            else
            {
                activeRanges.Add((chunk.RowOffset, chunk.RowOffset + chunk.RowCount));
            }
        }

        // If no chunks were pruned, stream all rows without overhead.
        if (PrunedIndexChunks == 0)
        {
            await foreach (Row row in rows.ConfigureAwait(false))
            {
                yield return row;
            }

            yield break;
        }

        // Stream rows, skipping those in pruned chunks by row index.
        long rowIndex = 0;
        int rangeIndex = 0;

        await foreach (Row row in rows.ConfigureAwait(false))
        {
            // Advance past ranges we've gone beyond.
            while (rangeIndex < activeRanges.Count && rowIndex >= activeRanges[rangeIndex].End)
            {
                rangeIndex++;
            }

            if (rangeIndex < activeRanges.Count
                && rowIndex >= activeRanges[rangeIndex].Start
                && rowIndex < activeRanges[rangeIndex].End)
            {
                yield return row;
            }

            rowIndex++;
        }
    }
}
