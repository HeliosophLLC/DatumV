using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog;

/// <summary>
/// <see cref="IQueryPlan"/> produced by <see cref="InsertExecutor.Execute"/>
/// when the parsed <see cref="InsertStatement"/> carries a <c>RETURNING</c>
/// clause. Holds the resolved (post-DEFAULT, post-IDENTITY) inserted batch
/// from the side-effect path, projects each row through the RETURNING
/// expressions, and yields the projection as a single <see cref="RowBatch"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Post-commit semantics.</b> By construction this plan only exists after
/// <see cref="IAppendSession.CommitAsync"/> has succeeded — the row capture
/// happens inside <c>ApplyValuesAsync</c> after the session commits. An INSERT
/// that aborts mid-write throws before the plan is constructed and never
/// yields rows. This matches PostgreSQL's contract that RETURNING rows are
/// only observable for committed inserts.
/// </para>
/// <para>
/// <b>Lifecycle.</b> The plan owns the captured insert batch (and its arena),
/// plus the projected output batch it builds on first iteration. Both are
/// returned to the pool via <c>finally</c> in the iterator regardless of
/// whether the consumer iterates fully, breaks early, or throws — so a host
/// that does <c>await foreach (var b in plan.ExecuteAsync(ct))</c> is safe.
/// </para>
/// </remarks>
internal sealed class InsertReturningPlan : IQueryPlan
{
    private readonly string _tableName;
    private readonly Schema _targetSchema;
    private readonly IReadOnlyList<RowBatch> _capturedBatches;
    private readonly IReadOnlyList<SelectColumn> _returningColumns;
    private readonly TableCatalog _catalog;

    public InsertReturningPlan(
        string tableName,
        Schema targetSchema,
        IReadOnlyList<RowBatch> capturedBatches,
        IReadOnlyList<SelectColumn> returningColumns,
        TableCatalog catalog)
    {
        _tableName = tableName;
        _targetSchema = targetSchema;
        _capturedBatches = capturedBatches;
        _returningColumns = returningColumns;
        _catalog = catalog;
    }

    /// <inheritdoc />
    public ExplainPlanNode ExplainTree { get; } = new()
    {
        OperatorName = "InsertReturning",
        Details = "INSERT … RETURNING — applied at plan time; rows yielded post-commit",
        EstimatedRows = 0,
    };

    /// <inheritdoc />
    public Task<ExplainPlanNode> AnalyzeAsync(CancellationToken cancellationToken)
        => Task.FromResult(ExplainTree);

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        IModelStreamingSink? streamingSink,
        BatchContext? batchContext)
    {
        _ = streamingSink;
        _ = batchContext;
        cancellationToken.ThrowIfCancellationRequested();

        // Expand RETURNING to a flat list of (output column name, expression).
        // SelectAllColumns expands to every target column in declared order;
        // SelectTableColumns is the table-qualified `t.*` form.
        List<(string Name, Expression Expr)> projection = ExpandProjection();
        ColumnLookup outputLookup = new(projection.Select(p => p.Name).ToArray());

        // Per-row evaluator pulls non-inline payloads from each captured
        // batch's arena (Source) and writes new ones into the corresponding
        // output batch's arena (Target). RETURNING expressions can reference
        // any inserted column; outer rows / variables are not in scope.
        ExpressionEvaluator evaluator = new(_catalog.Functions);

        // Yield one output batch per captured input batch — preserves the
        // streaming shape of INSERT … SELECT (each source RowBatch becomes
        // one RETURNING RowBatch). VALUES has a single captured batch and
        // therefore yields a single output batch, identical to C1b.
        try
        {
            foreach (RowBatch capturedBatch in _capturedBatches)
            {
                Arena outArena = new();
                RowBatch outBatch = _catalog.Pool.RentRowBatch(
                    outputLookup, capacity: capturedBatch.Count, arena: outArena);

                for (int rowIdx = 0; rowIdx < capturedBatch.Count; rowIdx++)
                {
                    Row insertedRow = capturedBatch[rowIdx];
                    EvaluationFrame frame = new(
                        insertedRow,
                        source: capturedBatch.Arena,
                        target: outArena,
                        sidecarRegistry: _catalog.SidecarRegistry);

                    DataValue[] outRow = _catalog.Pool.RentDataValues(projection.Count);
                    for (int colIdx = 0; colIdx < projection.Count; colIdx++)
                    {
                        ValueRef result = await evaluator.EvaluateAsValueRefAsync(
                            projection[colIdx].Expr, frame, cancellationToken).ConfigureAwait(false);
                        outRow[colIdx] = result.ToDataValue(outArena);
                    }
                    outBatch.Add(outRow);
                }

                // Convention: consumer owns the batch after yield and is
                // responsible for returning it to the pool. The outer
                // QueryPlan wrapper / CTE materialiser does this. We
                // intentionally do NOT return outBatch here.
                yield return outBatch;
            }
        }
        finally
        {
            // Captured input batches are private to this plan — never
            // yielded out — so we own their lifecycle end-to-end. Return
            // them to the pool when iteration finishes (success or throw).
            foreach (RowBatch captured in _capturedBatches)
            {
                _catalog.Pool.ReturnRowBatch(captured);
            }
        }
    }

    /// <summary>
    /// Expands the RETURNING column list into flat (name, expression) pairs.
    /// <see cref="SelectAllColumns"/> expands to every target column in
    /// schema-declared order; <see cref="SelectTableColumns"/> filters by
    /// matching table name (single-table INSERT — only the target qualifies).
    /// Plain <see cref="SelectColumn"/> entries pass through with their
    /// alias-or-derived name.
    /// </summary>
    private List<(string Name, Expression Expr)> ExpandProjection()
    {
        List<(string, Expression)> result = new(_returningColumns.Count);
        foreach (SelectColumn col in _returningColumns)
        {
            switch (col)
            {
                case SelectAllColumns:
                    foreach (ColumnInfo c in _targetSchema.Columns)
                    {
                        result.Add((c.Name, new ColumnReference(null, c.Name)));
                    }
                    break;

                case SelectTableColumns tcol:
                    if (string.Equals(tcol.TableName, _tableName, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (ColumnInfo c in _targetSchema.Columns)
                        {
                            result.Add((c.Name, new ColumnReference(_tableName, c.Name)));
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"INSERT INTO '{_tableName}' RETURNING: table-qualified `*` references " +
                            $"unknown table '{tcol.TableName}'.");
                    }
                    break;

                default:
                    string name = col.Alias ?? DeriveOutputName(col.Expression);
                    result.Add((name, col.Expression));
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// Picks a default output column name for an unaliased expression.
    /// Column references take their column name; everything else falls
    /// back to a positional <c>?columnN</c> name.
    /// </summary>
    private static string DeriveOutputName(Expression expr) =>
        expr switch
        {
            ColumnReference c => c.ColumnName,
            _ => "?column?",
        };
}
