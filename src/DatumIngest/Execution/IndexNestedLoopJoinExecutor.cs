using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Execution.Operators;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Executes an equi-join by probing a column index on the build side for each
/// probe-side row. Each probe-side key triggers an O(log n) lookup in the
/// index, followed by a point-seek to the matching build-side row(s) via
/// <see cref="ISeekableTableProvider.ReadRowRangeAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// This strategy is optimal under LIMIT when the probe side produces few rows and the
/// build side has a column index on the join column — the total cost is
/// <c>O(probeRows × log(buildRows))</c> with no materialization of the build side.
/// </para>
/// <para>
/// Only supports <see cref="JoinType.Inner"/> and <see cref="JoinType.LeftSemi"/>.
/// The query planner selects this executor when a column index is available on the
/// build-side join column and the query has a LIMIT clause.
/// </para>
/// </remarks>
internal sealed class IndexNestedLoopJoinExecutor
{
    private readonly JoinType _joinType;
    private readonly JoinKeyExtractionResult _extraction;
    private readonly IColumnIndex _buildIndex;
    private readonly IReadOnlyList<IndexChunk> _buildChunks;
    private readonly TableDescriptor _buildDescriptor;
    private readonly string? _buildAlias;
    private readonly ExpressionEvaluator _evaluator;
    private bool _circuitBreakerTripped;

    /// <summary>
    /// Maximum number of probe rows the executor will process before tripping
    /// the circuit breaker. Derived from <see cref="ExecutionContext.RowLimit"/>
    /// (10× the expected output) and capped at this absolute maximum.
    /// </summary>
    private const int MaxTrialProbeRows = 5_000;

    /// <summary>
    /// Creates an index nested loop join executor.
    /// </summary>
    /// <param name="joinType">The join type (must be Inner or LeftSemi).</param>
    /// <param name="extraction">The extracted equi-join key pairs and optional residual filter.</param>
    /// <param name="buildIndex">The column index on the build-side join column.</param>
    /// <param name="buildChunks">The chunk directory for translating chunk-relative offsets to absolute row positions.</param>
    /// <param name="buildDescriptor">The table descriptor for the build-side source (used for seeks).</param>
    /// <param name="buildAlias">
    /// The table alias that the query planner assigned to the build side via
    /// <see cref="Operators.AliasOperator"/>. When non-null, fetched build-side rows
    /// are re-qualified with this prefix so that downstream operators (e.g.
    /// <see cref="Operators.ProjectOperator"/> handling <c>SELECT table.*</c>) can
    /// match columns by table name.
    /// </param>
    /// <param name="evaluator">Expression evaluator for key extraction and residual filter evaluation.</param>
    internal IndexNestedLoopJoinExecutor(
        JoinType joinType,
        JoinKeyExtractionResult extraction,
        IColumnIndex buildIndex,
        IReadOnlyList<IndexChunk> buildChunks,
        TableDescriptor buildDescriptor,
        string? buildAlias,
        ExpressionEvaluator evaluator)
    {
        _joinType = joinType;
        _extraction = extraction;
        _buildIndex = buildIndex;
        _buildChunks = buildChunks;
        _buildDescriptor = buildDescriptor;
        _buildAlias = buildAlias;
        _evaluator = evaluator;
    }

    /// <summary>
    /// When <c>true</c>, the executor exceeded its probe-row trial budget and
    /// stopped without yielding any output. The caller should fall through to
    /// an alternative join strategy (e.g. hash join).
    /// </summary>
    internal bool CircuitBreakerTripped => _circuitBreakerTripped;

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
    internal async IAsyncEnumerable<RowBatch> ExecuteAsync(
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

        // Pre-computed alias schema for re-qualifying build-side rows that the
        // seekable provider returns with raw (unqualified) column names.
        BuildAliasSchema? buildAliasSchema = null;

        RowBatch? outputBatch = null;

        // Trial budget: buffer NLJ output for at most this many probe rows.
        // If the probe side exceeds this budget, the circuit breaker trips and
        // the caller falls back to hash join. This prevents pathological
        // per-row-seek behavior when a blocking operator above the join
        // swallows the LIMIT short-circuit that NLJ depends on.
        int trialBudget = Math.Min((context.RowLimit ?? 500) * 10, MaxTrialProbeRows);
        List<RowBatch> trialBuffer = new();
        long probeRowsProcessed = 0;

        await foreach (RowBatch probeBatch in probeOperator.ExecuteAsync(context).ConfigureAwait(false))
        {
            for (int probeBatchIndex = 0; probeBatchIndex < probeBatch.Count; probeBatchIndex++)
            {
            Row probeRow = probeBatch[probeBatchIndex];

            if (++probeRowsProcessed > trialBudget)
            {
                // Too many probe rows — NLJ is not benefiting from
                // LIMIT short-circuit. Discard buffered output and signal
                // the caller to fall back to hash join.
                probeBatch.Return();
                outputBatch?.Return();
                foreach (RowBatch buffered in trialBuffer) { buffered.Return(); }
                _circuitBreakerTripped = true;
                yield break;
            }

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
                    outputBatch ??= RowBatch.Rent(context.BatchSize);
                    outputBatch.Add(probeRow);
                    if (outputBatch.IsFull) { trialBuffer.Add(outputBatch); outputBatch = null; }
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

                    buildRow = ApplyBuildAlias(buildRow.Value, ref buildAliasSchema);
                    combinedSchema ??= JoinOperator.CombinedRowSchema.Build(probeRow, buildRow.Value);
                    Row combined = combinedSchema.Combine(probeRow, buildRow.Value);

                    if (EvaluateResidual(residual, combined))
                    {
                        semiMatch = true;
                        break;
                    }
                }

                if (semiMatch)
                {
                    outputBatch ??= RowBatch.Rent(context.BatchSize);
                    outputBatch.Add(probeRow);
                    if (outputBatch.IsFull) { trialBuffer.Add(outputBatch); outputBatch = null; }
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

                buildRow = ApplyBuildAlias(buildRow.Value, ref buildAliasSchema);
                combinedSchema ??= JoinOperator.CombinedRowSchema.Build(probeRow, buildRow.Value);
                Row combined = combinedSchema.Combine(probeRow, buildRow.Value);

                if (residual is not null && !EvaluateResidual(residual, combined))
                {
                    continue;
                }

                outputBatch ??= RowBatch.Rent(context.BatchSize);
                outputBatch.Add(combined);
                if (outputBatch.IsFull) { trialBuffer.Add(outputBatch); outputBatch = null; }
            }
            }
            probeBatch.Return();
        }

        // Trial completed — NLJ processed all probe rows within budget.
        // Yield the buffered output batches.
        foreach (RowBatch buffered in trialBuffer)
        {
            yield return buffered;
        }

        if (outputBatch is not null)
        {
            yield return outputBatch;
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

        await foreach (RowBatch batch in seekable.ReadRowRangeAsync(
            _buildDescriptor, requiredColumns: null, absoluteRow, 1, cancellationToken)
            .ConfigureAwait(false))
        {
            if (batch.Count > 0)
            {
                return batch[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Applies the build-side alias prefix to a raw row returned by the seekable
    /// provider, producing a row with qualified column names (e.g. <c>table.column</c>).
    /// The schema is built once from the first row and reused for all subsequent rows.
    /// </summary>
    private Row ApplyBuildAlias(Row row, ref BuildAliasSchema? schema)
    {
        if (_buildAlias is null)
        {
            return row;
        }

        schema ??= BuildAliasSchema.Create(_buildAlias, row);
        return schema.Apply(row);
    }

    /// <summary>
    /// Pre-computed schema for re-qualifying build-side rows with the table alias.
    /// Built once from the first fetched row and reused for all subsequent rows,
    /// allocating only a <see cref="DataValue"/> array per row.
    /// </summary>
    private sealed class BuildAliasSchema
    {
        private readonly string[] _names;
        private readonly Dictionary<string, int> _nameIndex;
        private readonly int _fieldCount;

        private BuildAliasSchema(
            string[] names,
            Dictionary<string, int> nameIndex,
            int fieldCount)
        {
            _names = names;
            _nameIndex = nameIndex;
            _fieldCount = fieldCount;
        }

        /// <summary>
        /// Builds the alias schema from the alias prefix and the first build-side row.
        /// </summary>
        internal static BuildAliasSchema Create(string alias, Row firstRow)
        {
            int fieldCount = firstRow.FieldCount;
            string[] names = new string[fieldCount];

            for (int index = 0; index < fieldCount; index++)
            {
                names[index] = $"{alias}.{firstRow.ColumnNames[index]}";
            }

            Dictionary<string, int> nameIndex =
                new(fieldCount * 2, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < fieldCount; index++)
            {
                nameIndex[names[index]] = index;
                nameIndex[firstRow.ColumnNames[index]] = index;
            }

            return new BuildAliasSchema(names, nameIndex, fieldCount);
        }

        /// <summary>
        /// Applies the alias schema to a raw build-side row.
        /// </summary>
        internal Row Apply(Row sourceRow)
        {
            DataValue[] values = new DataValue[_fieldCount];

            for (int index = 0; index < _fieldCount; index++)
            {
                values[index] = sourceRow[index];
            }

            return new Row(_names, values, _nameIndex);
        }
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
