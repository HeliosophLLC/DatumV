using System.Runtime.CompilerServices;
using DatumIngest.Catalog.Executors;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// Which DML statement produced this plan. Drives the explain-tree label and
/// the diagnostic prefix on schema-mismatch errors during projection expansion.
/// </summary>
internal enum DmlReturningKind
{
    /// <summary><c>INSERT … RETURNING</c> — captured rows are the post-commit insert image.</summary>
    Insert,
    /// <summary><c>UPDATE … RETURNING</c> — captured rows are the post-update image of every WHERE-matched row.</summary>
    Update,
    /// <summary><c>DELETE … RETURNING</c> — captured rows are the pre-delete image of every tombstoned row.</summary>
    Delete,
}

/// <summary>
/// <see cref="IQueryPlan"/> produced by the INSERT / UPDATE / DELETE executors
/// when the parsed statement carries a <c>RETURNING</c> clause. Holds the
/// captured row batch from the side-effect path (post-DEFAULT / post-IDENTITY
/// inserted rows for INSERT, post-update images for UPDATE, pre-delete images
/// for DELETE), projects each row through the RETURNING expressions, and yields
/// the projection as one <see cref="RowBatch"/> per captured input batch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Post-commit semantics.</b> By construction this plan only exists after
/// the executor's commit has succeeded — the row capture happens after
/// <see cref="IAppendSession.CommitAsync"/> / <c>UpdateRowsAsync</c> /
/// <c>DeleteRows</c>. A DML that aborts mid-write throws before the plan is
/// constructed and never yields rows. Matches PostgreSQL's contract that
/// RETURNING rows are only observable for committed mutations.
/// </para>
/// <para>
/// <b>Lifecycle.</b> The plan owns the captured batches (and any arena they
/// reference) and returns them to the pool via <c>finally</c> in the iterator
/// regardless of whether the consumer iterates fully, breaks early, or throws.
/// </para>
/// </remarks>
internal sealed class DmlReturningPlan : IQueryPlan
{
    private readonly DmlReturningKind _kind;
    private readonly string _tableName;
    private readonly Schema _targetSchema;
    private readonly IReadOnlyList<RowBatch> _capturedBatches;
    private readonly IReadOnlyList<SelectColumn> _returningColumns;
    private readonly TableCatalog _catalog;

    public DmlReturningPlan(
        DmlReturningKind kind,
        string tableName,
        Schema targetSchema,
        IReadOnlyList<RowBatch> capturedBatches,
        IReadOnlyList<SelectColumn> returningColumns,
        TableCatalog catalog)
    {
        _kind = kind;
        _tableName = tableName;
        _targetSchema = targetSchema;
        _capturedBatches = capturedBatches;
        _returningColumns = returningColumns;
        _catalog = catalog;

        string verb = kind switch
        {
            DmlReturningKind.Insert => "INSERT",
            DmlReturningKind.Update => "UPDATE",
            DmlReturningKind.Delete => "DELETE",
            _ => "DML",
        };
        ExplainTree = new ExplainPlanNode
        {
            OperatorName = $"{kind}Returning",
            Details = $"{verb} … RETURNING — applied at plan time; rows yielded post-commit",
            EstimatedRows = 0,
        };
    }

    /// <inheritdoc />
    public ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    public Task<ExplainPlanNode> AnalyzeAsync(CancellationToken cancellationToken)
        => Task.FromResult(ExplainTree);

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext? batchContext)
    {
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
        // any captured column; outer rows / variables are not in scope.
        ExpressionEvaluator evaluator = new(_catalog.Functions);

        // Yield one output batch per captured input batch — preserves the
        // streaming shape of INSERT … SELECT (each source RowBatch becomes
        // one RETURNING RowBatch). VALUES has a single captured batch and
        // therefore yields a single output batch.
        try
        {
            foreach (RowBatch capturedBatch in _capturedBatches)
            {
                Arena outArena = new();
                RowBatch outBatch = _catalog.Pool.RentRowBatch(
                    outputLookup, capacity: capturedBatch.Count, arena: outArena);

                for (int rowIdx = 0; rowIdx < capturedBatch.Count; rowIdx++)
                {
                    Row capturedRow = capturedBatch[rowIdx];
                    EvaluationFrame frame = new(
                        capturedRow,
                        source: capturedBatch.Arena,
                        target: outArena,
                        accountant: evaluator.Accountant,
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
    /// matching table name (single-table DML — only the target qualifies).
    /// Plain <see cref="SelectColumn"/> entries pass through with their
    /// alias-or-derived name.
    /// </summary>
    private List<(string Name, Expression Expr)> ExpandProjection()
    {
        string verb = _kind switch
        {
            DmlReturningKind.Insert => "INSERT INTO",
            DmlReturningKind.Update => "UPDATE",
            DmlReturningKind.Delete => "DELETE FROM",
            _ => "DML on",
        };

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
                            $"{verb} '{_tableName}' RETURNING: table-qualified `*` references " +
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
    /// back to a positional <c>?column?</c> name.
    /// </summary>
    private static string DeriveOutputName(Expression expr) =>
        expr switch
        {
            ColumnReference c => c.ColumnName,
            _ => "?column?",
        };
}
