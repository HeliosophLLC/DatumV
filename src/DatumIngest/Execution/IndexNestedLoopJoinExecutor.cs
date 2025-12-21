using DatumIngest.Catalog;
using DatumIngest.Diagnostics;
using DatumIngest.Execution.Operators;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Executes an equi-join by probing a column index on the build side for each
/// probe-side row. Each probe-side key triggers an O(log n) lookup in the
/// index, followed by a point-seek to the matching build-side row(s).
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
    private readonly ITableProvider _buildTableProvider;
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
    /// <param name="tableProvider">The seekable provider for the build-side source table.</param>
    /// <param name="joinType">The join type (must be Inner or LeftSemi).</param>
    /// <param name="extraction">The extracted equi-join key pairs and optional residual filter.</param>
    /// <param name="buildIndex">The column index on the build-side join column.</param>
    /// <param name="buildChunks">The chunk directory for translating chunk-relative offsets to absolute row positions.</param>
    /// <param name="buildAlias">
    /// The table alias that the query planner assigned to the build side via
    /// <see cref="Operators.AliasOperator"/>. When non-null, fetched build-side rows
    /// are re-qualified with this prefix so that downstream operators (e.g.
    /// <see cref="Operators.ProjectOperator"/> handling <c>SELECT table.*</c>) can
    /// match columns by table name.
    /// </param>
    /// <param name="evaluator">Expression evaluator for key extraction and residual filter evaluation.</param>
    internal IndexNestedLoopJoinExecutor(
        ITableProvider tableProvider,
        JoinType joinType,
        JoinKeyExtractionResult extraction,
        IColumnIndex buildIndex,
        IReadOnlyList<IndexChunk> buildChunks,
        string? buildAlias,
        ExpressionEvaluator evaluator)
    {
        _buildTableProvider = tableProvider;
        _joinType = joinType;
        _extraction = extraction;
        _buildIndex = buildIndex;
        _buildChunks = buildChunks;
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

        if (!_buildTableProvider.Seekable)
        {
            throw new InvalidOperationException(
                $"IndexNestedLoopJoinExecutor requires a seekable provider, but '{_buildTableProvider.Name}' " +
                $"does not indicate it is seekable.");
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
        LocalBufferPool bufferPool = context.LocalBufferPool;

        // Reusable scratch buffer for residual evaluation — allocated once, filled by CombineInto.
        DataValue[]? residualScratchBuffer = null;
        Row residualScratchRow = default;

        // Trial budget: buffer NLJ output for at most this many probe rows.
        // If the probe side exceeds this budget, the circuit breaker trips and
        // the caller falls back to hash join. This prevents pathological
        // per-row-seek behavior when a blocking operator above the join
        // swallows the LIMIT short-circuit that NLJ depends on.
        int trialBudget = Math.Min((context.RowLimit ?? 500) * 10, MaxTrialProbeRows);
        List<RowBatch> trialBuffer = new();
        long probeRowsProcessed = 0;
        long totalMatches = 0;

        ExecutionTracer.Write($"INLJ start  trialBudget={trialBudget}  buildTable={_buildTableProvider.Name}  buildAlias={_buildAlias}  joinType={_joinType}");

        // Open a seek session once for the lifetime of this executor — it owns the
        // reader, decode buffers, and projection metadata for every build-side fetch.
        // Bound to context.Store so emitted batches share the per-query arena.
        ISeekSession seekSession = _buildTableProvider.OpenSeekSession(requiredColumns: null, context.Store);

        ExecutionTracer.Write("INLJ probing probe side");
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
                    ExecutionTracer.Write($"INLJ circuit breaker tripped  probeRows={probeRowsProcessed}  matches={totalMatches}  buffered={trialBuffer.Count} batches");
                    context.ReturnRowBatch(probeBatch);
                    if (outputBatch is not null) context.ReturnRowBatch(outputBatch);
                    foreach (RowBatch buffered in trialBuffer) { context.ReturnRowBatch(buffered); }
                    _circuitBreakerTripped = true;
                    yield break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Evaluate the probe-side key.
                DataValue probeKey = await _evaluator.EvaluateAsync(probeKeyExpression, probeRow, cancellationToken).ConfigureAwait(false);

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
                        outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                        outputBatch.Add(probeRow);
                        if (outputBatch.IsFull) { trialBuffer.Add(outputBatch); outputBatch = null; }
                        continue;
                    }

                    // Fetch build rows to evaluate residual.
                    bool semiMatch = false;

                    foreach (ValueIndexEntry entry in matches)
                    {
                        Row? rawBuildRow = await FetchBuildRowAsync(seekSession, entry, bufferPool, context, cancellationToken)
                            .ConfigureAwait(false);

                        if (rawBuildRow is null)
                        {
                            continue;
                        }

                        Row buildRow = ApplyBuildAlias(rawBuildRow.Value, ref buildAliasSchema, bufferPool);
                        if (_buildAlias is not null) bufferPool.Return(rawBuildRow.Value.RawValues);

                        combinedSchema ??= JoinOperator.CombinedRowSchema.Build(probeRow, buildRow);
                        if (residualScratchBuffer is null)
                        {
                            (residualScratchRow, residualScratchBuffer) = combinedSchema.CreateReusableRow();
                        }

                        combinedSchema.CombineInto(probeRow, buildRow, residualScratchBuffer);
                        bufferPool.Return(buildRow.RawValues);

                        if (await EvaluateResidualAsync(residual, residualScratchRow, cancellationToken).ConfigureAwait(false))
                        {
                            semiMatch = true;
                            break;
                        }
                    }

                    if (semiMatch)
                    {
                        outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                        outputBatch.Add(probeRow);
                        if (outputBatch.IsFull) { trialBuffer.Add(outputBatch); outputBatch = null; }
                    }

                    continue;
                }

                // INNER join: fetch each matching build row and yield combined rows.
                foreach (ValueIndexEntry entry in matches)
                {
                    Row? rawBuildRow = await FetchBuildRowAsync(seekSession, entry, bufferPool, context, cancellationToken)
                        .ConfigureAwait(false);

                    if (rawBuildRow is null)
                    {
                        continue;
                    }

                    Row buildRow = ApplyBuildAlias(rawBuildRow.Value, ref buildAliasSchema, bufferPool);
                    if (_buildAlias is not null) bufferPool.Return(rawBuildRow.Value.RawValues);

                    combinedSchema ??= JoinOperator.CombinedRowSchema.Build(probeRow, buildRow);

                    if (residual is not null)
                    {
                        if (residualScratchBuffer is null)
                        {
                            (residualScratchRow, residualScratchBuffer) = combinedSchema.CreateReusableRow();
                        }

                        combinedSchema.CombineInto(probeRow, buildRow, residualScratchBuffer);

                        if (!await EvaluateResidualAsync(residual, residualScratchRow, cancellationToken).ConfigureAwait(false))
                        {
                            bufferPool.Return(buildRow.RawValues);
                            continue;
                        }
                    }

                    Row combined = combinedSchema.CombinePooled(probeRow, buildRow, bufferPool);
                    bufferPool.Return(buildRow.RawValues);
                    totalMatches++;

                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(combined);
                    if (outputBatch.IsFull) { trialBuffer.Add(outputBatch); outputBatch = null; }
                }
            }
            context.ReturnRowBatch(probeBatch);
        }

        ExecutionTracer.Write($"INLJ trial complete  probeRows={probeRowsProcessed}  matches={totalMatches}  buffered={trialBuffer.Count} batches");

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
        ISeekSession seekSession,
        ValueIndexEntry entry,
        LocalBufferPool bufferPool,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        long absoluteRow = _buildChunks[entry.ChunkIndex].RowOffset + entry.RowOffsetInChunk;

        await foreach (RowBatch batch in seekSession
            .SeekAsync(absoluteRow, 1, cancellationToken)
            .ConfigureAwait(false))
        {
            if (batch.Count > 0)
            {
                Row src = batch[0];
                DataValue[] owned = bufferPool.Rent(src.FieldCount);
                Array.Copy(src.RawValues, owned, src.FieldCount);
                Row row = new(src.RawNames, owned, src.RawNameIndex);
                context.ReturnRowBatch(batch);
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies the build-side alias prefix to a raw row returned by the seekable
    /// provider, producing a row with qualified column names (e.g. <c>table.column</c>).
    /// The schema is built once from the first row and reused for all subsequent rows.
    /// </summary>
    private Row ApplyBuildAlias(Row row, ref BuildAliasSchema? schema, LocalBufferPool bufferPool)
    {
        if (_buildAlias is null)
        {
            return row;
        }

        schema ??= BuildAliasSchema.Create(_buildAlias, row);
        return schema.Apply(row, bufferPool);
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
        /// Applies the alias schema to a raw build-side row, renting the backing
        /// <see cref="DataValue"/> array from <paramref name="bufferPool"/> rather
        /// than allocating. The caller must return the array via
        /// <see cref="LocalBufferPool.Return(DataValue[])"/> when the row is no longer needed.
        /// </summary>
        internal Row Apply(Row sourceRow, LocalBufferPool bufferPool)
        {
            DataValue[] values = bufferPool.Rent(_fieldCount);

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
    private async ValueTask<bool> EvaluateResidualAsync(Expression residual, Row row, CancellationToken cancellationToken)
    {
        DataValue result = await _evaluator.EvaluateAsync(residual, row, cancellationToken).ConfigureAwait(false);
        return !result.IsNull && result.Kind == DataKind.Boolean && result.AsBoolean();
    }
}
