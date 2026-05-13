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
/// Composer <see cref="StatementPlan"/> for <c>INSERT / UPDATE / DELETE … RETURNING</c>.
/// Owns a child <see cref="DmlPlan"/> (the side effect — populates a
/// <see cref="CapturedRowsSource"/> with post-mutation rows) and the
/// <see cref="CapturedRowsSource"/> itself; projects RETURNING columns
/// over the captured rows and yields one output <see cref="RowBatch"/>
/// per captured input batch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two construction modes.</b>
/// </para>
/// <list type="bullet">
///   <item>
///     <term>Composed (planner-time)</term>
///     <description>Both <c>dmlPlan</c> and <c>capturedSource</c> supplied.
///     <see cref="ExecuteImplAsync"/> first iterates the
///     <see cref="DmlPlan"/> (which drains zero rows but populates the
///     source as a side effect of applying the DML) and then iterates the
///     source through the RETURNING projection. EXPLAIN sees both children
///     under this composer node.</description>
///   </item>
///   <item>
///     <term>Post-applied (legacy executor return)</term>
///     <description>Only <c>capturedSource</c> supplied (the executor
///     already ran inline and pre-populated it). The DmlPlan iteration is
///     skipped; the projection iterates the source directly.</description>
///   </item>
/// </list>
/// <para>
/// <b>Post-commit semantics.</b> By construction the captured rows are
/// produced only after the executor's commit has succeeded — the row
/// capture happens after <see cref="IAppendSession.CommitAsync"/> /
/// <c>UpdateRowsAsync</c> / <c>DeleteRows</c>. A DML that aborts
/// mid-write throws before the source is populated. Matches PostgreSQL's
/// contract that RETURNING rows are only observable for committed
/// mutations.
/// </para>
/// <para>
/// <b>Lifecycle.</b> The composer owns the captured batches end-to-end
/// and returns them to the pool via <c>finally</c> in the iterator,
/// regardless of whether the consumer iterates fully, breaks early, or
/// throws.
/// </para>
/// </remarks>
internal sealed class DmlReturningPlan : StatementPlan
{
    private readonly DmlReturningKind _kind;
    private readonly string _tableName;
    private readonly Schema _targetSchema;
    private readonly StatementPlan? _dmlPlan;
    private readonly CapturedRowsSource _capturedSource;
    private readonly IReadOnlyList<SelectColumn> _returningColumns;

    private DmlReturningPlan(
        TableCatalog catalog,
        DmlReturningKind kind,
        string tableName,
        Schema targetSchema,
        StatementPlan? dmlPlan,
        CapturedRowsSource capturedSource,
        IReadOnlyList<SelectColumn> returningColumns)
        : base(catalog)
    {
        _kind = kind;
        _tableName = tableName;
        _targetSchema = targetSchema;
        _dmlPlan = dmlPlan;
        _capturedSource = capturedSource;
        _returningColumns = returningColumns;

        string verb = kind switch
        {
            DmlReturningKind.Insert => "INSERT",
            DmlReturningKind.Update => "UPDATE",
            DmlReturningKind.Delete => "DELETE",
            _ => "DML",
        };
        ExplainPlanNode tree = new()
        {
            OperatorName = $"{kind}Returning",
            Details = $"{verb} … RETURNING",
            EstimatedRows = 0,
        };
        if (dmlPlan is not null)
        {
            tree.Children.Add(dmlPlan.ExplainTree);
        }
        tree.Children.Add(capturedSource.ExplainTree);
        ExplainTree = tree;
    }

    /// <summary>
    /// Composer-mode construction: the <paramref name="dmlPlan"/> applies
    /// the side effect on iterate and populates <paramref name="capturedSource"/>;
    /// the projection then reads from the source. Used by
    /// <see cref="TableCatalog.PlanAsync(string)"/> for INSERT / UPDATE /
    /// DELETE … RETURNING.
    /// </summary>
    public static DmlReturningPlan Compose(
        DmlReturningKind kind,
        string tableName,
        Schema targetSchema,
        StatementPlan dmlPlan,
        CapturedRowsSource capturedSource,
        IReadOnlyList<SelectColumn> returningColumns,
        TableCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(dmlPlan);
        ArgumentNullException.ThrowIfNull(capturedSource);
        return new DmlReturningPlan(catalog, kind, tableName, targetSchema, dmlPlan, capturedSource, returningColumns);
    }

    /// <summary>
    /// Legacy post-applied constructor. The executor has already run and
    /// supplied the captured batches as a list; this overload wraps them
    /// in a <see cref="CapturedRowsSource"/> internally. Used by the
    /// eager <see cref="TableCatalog.ExecuteStatementAsync(string)"/>
    /// path, where the side effect runs before the plan is built.
    /// </summary>
    public DmlReturningPlan(
        TableCatalog catalog,
        DmlReturningKind kind,
        string tableName,
        Schema targetSchema,
        IReadOnlyList<RowBatch> capturedBatches,
        IReadOnlyList<SelectColumn> returningColumns)
        : this(catalog, kind, tableName, targetSchema, dmlPlan: null,
               capturedSource: PrepopulateSource(catalog, capturedBatches),
               returningColumns)
    {
    }

    private static CapturedRowsSource PrepopulateSource(TableCatalog catalog, IReadOnlyList<RowBatch> batches)
    {
        CapturedRowsSource source = new(catalog);
        foreach (RowBatch b in batches) source.Capture(b);
        return source;
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Composed mode: drain the child DmlPlan so its side effect runs
        // and populates _capturedSource. The plan yields zero rows; we
        // discard the loop body. Post-applied mode (legacy) skips this —
        // the source was already populated externally.
        if (_dmlPlan is not null)
        {
            await foreach (RowBatch _ in _dmlPlan
                .ExecuteAsync(cancellationToken, batchContext)
                .ConfigureAwait(false))
            {
                // DmlPlan yields nothing; this body is unreachable in practice.
            }
        }

        // Expand RETURNING to a flat list of (output column name, expression).
        // SelectAllColumns expands to every target column in declared order;
        // SelectTableColumns is the table-qualified `t.*` form.
        List<(string Name, Expression Expr)> projection = ExpandProjection();
        ColumnLookup outputLookup = new(projection.Select(p => p.Name).ToArray());

        // Per-row evaluator pulls non-inline payloads from each captured
        // batch's arena (Source) and writes new ones into the corresponding
        // output batch's arena (Target). RETURNING expressions can reference
        // any captured column; outer rows / variables are not in scope.
        using DatumIngest.Execution.ExecutionContext context = Catalog.CreateExecutionContext(
            accountant: batchContext.Accountant,
            types: batchContext.Types,
            cancellationToken: cancellationToken);
        ExpressionEvaluator evaluator = context.CreateEvaluator();

        // Yield one output batch per captured input batch — preserves the
        // streaming shape of INSERT … SELECT (each source RowBatch becomes
        // one RETURNING RowBatch). VALUES has a single captured batch and
        // therefore yields a single output batch. We pull from
        // CapturedRowsSource.Batches directly so the composer can own
        // end-to-end disposal (iterating the source's ExecuteAsync would
        // mark it executed and prevent the disposal pass below).
        IReadOnlyList<RowBatch> captured = _capturedSource.Batches;
        try
        {
            foreach (RowBatch capturedBatch in captured)
            {
                Arena outArena = new();
                RowBatch outBatch = Catalog.Pool.RentRowBatch(
                    outputLookup, capacity: capturedBatch.Count, arena: outArena);

                for (int rowIdx = 0; rowIdx < capturedBatch.Count; rowIdx++)
                {
                    Row capturedRow = capturedBatch[rowIdx];
                    EvaluationFrame frame = new(capturedRow, capturedBatch.Arena, outArena, context);

                    DataValue[] outRow = Catalog.Pool.RentDataValues(projection.Count);
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
                // SelectPlan wrapper / CTE materialiser does this. We
                // intentionally do NOT return outBatch here.
                yield return outBatch;
            }
        }
        finally
        {
            // Captured input batches are private to this plan — never
            // yielded out — so we own their lifecycle end-to-end. Return
            // them to the pool when iteration finishes (success or throw).
            foreach (RowBatch captured_ in captured)
            {
                Catalog.Pool.ReturnRowBatch(captured_);
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
