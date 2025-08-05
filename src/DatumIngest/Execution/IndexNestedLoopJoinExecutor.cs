using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Execution.Operators;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Executes an equi-join by probing a sorted value index on the build side for each
/// probe-side row. Each probe-side key triggers an O(log n) binary search in the
/// sorted index, followed by a point-seek to the matching build-side row(s) via
/// <see cref="ISeekableTableProvider.ReadRowRangeAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// This strategy is optimal under LIMIT when the probe side produces few rows and the
/// build side has a sorted index on the join column — the total cost is
/// <c>O(probeRows × log(buildRows))</c> with no materialization of the build side.
/// </para>
/// <para>
/// Only supports <see cref="JoinType.Inner"/> and <see cref="JoinType.LeftSemi"/>.
/// The query planner selects this executor when a sorted index is available on the
/// build-side join column and the query has a LIMIT clause.
/// </para>
/// </remarks>
internal sealed class IndexNestedLoopJoinExecutor
{
    private readonly JoinType _joinType;
    private readonly JoinKeyExtractionResult _extraction;
    private readonly SortedValueIndex _buildIndex;
    private readonly IReadOnlyList<IndexChunk> _buildChunks;
    private readonly TableDescriptor _buildDescriptor;
    private readonly ExpressionEvaluator _evaluator;

    /// <summary>
    /// Creates an index nested loop join executor.
    /// </summary>
    /// <param name="joinType">The join type (must be Inner or LeftSemi).</param>
    /// <param name="extraction">The extracted equi-join key pairs and optional residual filter.</param>
    /// <param name="buildIndex">The sorted value index on the build-side join column.</param>
    /// <param name="buildChunks">The chunk directory for translating chunk-relative offsets to absolute row positions.</param>
    /// <param name="buildDescriptor">The table descriptor for the build-side source (used for seeks).</param>
    /// <param name="evaluator">Expression evaluator for key extraction and residual filter evaluation.</param>
    internal IndexNestedLoopJoinExecutor(
        JoinType joinType,
        JoinKeyExtractionResult extraction,
        SortedValueIndex buildIndex,
        IReadOnlyList<IndexChunk> buildChunks,
        TableDescriptor buildDescriptor,
        ExpressionEvaluator evaluator)
    {
        _joinType = joinType;
        _extraction = extraction;
        _buildIndex = buildIndex;
        _buildChunks = buildChunks;
        _buildDescriptor = buildDescriptor;
        _evaluator = evaluator;
    }

    /// <summary>
    /// Executes the index nested loop join.
    /// </summary>
    /// <param name="probeOperator">The probe-side operator (typically the smaller/LIMIT side).</param>
    /// <param name="buildOperator">
    /// The build-side operator. Not iterated — the sorted index replaces full materialization.
    /// Provided only so its schema can be discovered if needed; the actual rows are fetched
    /// via the seekable provider.
    /// </param>
    /// <param name="context">Execution context.</param>
    /// <returns>Combined rows from the join.</returns>
    internal async IAsyncEnumerable<Row> ExecuteAsync(
        IQueryOperator probeOperator,
        IQueryOperator buildOperator,
        ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        ITableProvider provider = context.Catalog.CreateProvider(_buildDescriptor);

        if (provider is not ISeekableTableProvider seekable)
        {
            throw new InvalidOperationException(
                $"IndexNestedLoopJoinExecutor requires a seekable provider, but '{_buildDescriptor.Name}' " +
                $"uses '{provider.GetType().Name}' which does not implement ISeekableTableProvider.");
        }

        // We only support single-key equi-joins for index NLJ.
        // The right expression is used against the build-side index, the left against the probe row.
        (Expression probeKeyExpression, Expression _) = _extraction.KeyPairs[0];
        Expression? residual = _extraction.Residual;

        JoinOperator.CombinedRowSchema? combinedSchema = null;

        await foreach (Row probeRow in probeOperator.ExecuteAsync(context).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Evaluate the probe-side key.
            DataValue probeKey = _evaluator.Evaluate(probeKeyExpression, probeRow);

            if (probeKey.IsNull)
            {
                // NULL keys never match in equi-join.
                continue;
            }

            // Look up matching build-side entries via the sorted index.
            IReadOnlyList<ValueIndexEntry> matches = _buildIndex.FindExact(probeKey);

            if (matches.Count == 0)
            {
                continue;
            }

            if (_joinType == JoinType.LeftSemi)
            {
                // SEMI join: just emit the probe row without build columns,
                // but we need to verify residual if present.
                if (residual is null)
                {
                    yield return probeRow;
                    continue;
                }

                // Fetch one build row to evaluate residual.
                bool semiMatch = false;

                foreach (ValueIndexEntry entry in matches)
                {
                    Row? buildRow = await FetchBuildRowAsync(seekable, entry, cancellationToken)
                        .ConfigureAwait(false);

                    if (buildRow is null)
                    {
                        continue;
                    }

                    combinedSchema ??= JoinOperator.CombinedRowSchema.Build(probeRow, buildRow);
                    Row combined = combinedSchema.Combine(probeRow, buildRow);

                    if (EvaluateResidual(residual, combined))
                    {
                        semiMatch = true;
                        break;
                    }
                }

                if (semiMatch)
                {
                    yield return probeRow;
                }

                continue;
            }

            // INNER join: fetch each matching build row and yield combined rows.
            foreach (ValueIndexEntry entry in matches)
            {
                Row? buildRow = await FetchBuildRowAsync(seekable, entry, cancellationToken)
                    .ConfigureAwait(false);

                if (buildRow is null)
                {
                    continue;
                }

                combinedSchema ??= JoinOperator.CombinedRowSchema.Build(probeRow, buildRow);
                Row combined = combinedSchema.Combine(probeRow, buildRow);

                if (residual is not null && !EvaluateResidual(residual, combined))
                {
                    continue;
                }

                yield return combined;
            }
        }
    }

    /// <summary>
    /// Fetches a single build-side row by seeking to the absolute row position
    /// derived from the index entry's chunk and offset.
    /// </summary>
    private async Task<Row?> FetchBuildRowAsync(
        ISeekableTableProvider seekable,
        ValueIndexEntry entry,
        CancellationToken cancellationToken)
    {
        long absoluteRow = _buildChunks[entry.ChunkIndex].RowOffset + entry.RowOffsetInChunk;

        await foreach (Row row in seekable.ReadRowRangeAsync(
            _buildDescriptor, requiredColumns: null, absoluteRow, 1, cancellationToken)
            .ConfigureAwait(false))
        {
            return row;
        }

        return null;
    }

    /// <summary>
    /// Evaluates an optional residual predicate against a combined row.
    /// </summary>
    private bool EvaluateResidual(Expression residual, Row row)
    {
        DataValue result = _evaluator.Evaluate(residual, row);
        return !result.IsNull && result.Kind == DataKind.Boolean && result.AsBoolean();
    }
}
