using System.Runtime.CompilerServices;
using DatumIngest.Catalog.Executors;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for INSERT / UPDATE / DELETE. One class
/// for every DML shape — they're structurally identical (target table,
/// optional source query, side effect applied), so a discriminator
/// label is all that distinguishes them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source plan composition.</b> For INSERT … SELECT the source query
/// is planned at <see cref="ForInsert"/>-construction time and held as a
/// real child, so the EXPLAIN tree shows the full SELECT subtree under
/// the INSERT node without applying any side effect. INSERT … VALUES,
/// INSERT … DEFAULT VALUES, and UPDATE / DELETE drivers don't produce a
/// separate child plan — their scan logic is internal to the executor.
/// </para>
/// <para>
/// <b>RETURNING composition.</b> A DML plan yields zero rows on its own.
/// When RETURNING is present, callers wrap a <see cref="DmlPlan"/> in a
/// <see cref="DmlReturningPlan"/> composer that owns this plan plus a
/// <see cref="CapturedRowsSource"/>; the executor populates the source
/// during side-effect application, and the composer projects RETURNING
/// columns over the captured rows. The composer handles the row stream;
/// this plan handles only the side effect.
/// </para>
/// <para>
/// <b>Side-effect delegation.</b> The actual writes still flow through
/// <see cref="InsertExecutor"/> / <see cref="UpdateExecutor"/> /
/// <see cref="DeleteExecutor"/>; this plan wraps the call so the
/// planner-time vs. executor-time split is enforced — the executor only
/// fires during <see cref="ExecuteImplAsync"/>.
/// </para>
/// <para>
/// <b>Idempotency:</b> single-shot. Re-iterating throws.
/// </para>
/// </remarks>
internal sealed class DmlPlan : StatementPlan
{
    private readonly StatementPlan? _sourcePlan;
    private readonly Func<BatchContext, Task> _apply;
    private int _executed;

    private DmlPlan(
        TableCatalog catalog,
        string operatorName,
        string details,
        StatementPlan? sourcePlan,
        Func<BatchContext, Task> apply)
        : base(catalog)
    {
        _sourcePlan = sourcePlan;
        _apply = apply;

        ExplainPlanNode tree = new()
        {
            OperatorName = operatorName,
            Details = details,
            EstimatedRows = 0,
        };
        if (sourcePlan is not null)
        {
            tree.Children.Add(sourcePlan.ExplainTree);
        }
        ExplainTree = tree;
    }

    /// <summary>
    /// Builds a DML plan for <c>INSERT</c>. For <c>INSERT … SELECT</c>
    /// the source query is planned at this call (<paramref name="sourcePlan"/>)
    /// and threaded through to the executor — composing it as a child
    /// rather than re-planning at execute time means EXPLAIN sees the
    /// full tree. Pass <see langword="null"/> for <c>VALUES</c> /
    /// <c>DEFAULT VALUES</c> insertion. When
    /// <paramref name="captureSink"/> is non-null the executor populates
    /// it with post-mutation rows so a wrapping
    /// <see cref="DmlReturningPlan"/> can project RETURNING columns from
    /// the captured rows.
    /// </summary>
    public static DmlPlan ForInsert(
        TableCatalog catalog,
        InsertStatement insert,
        StatementPlan? sourcePlan,
        CapturedRowsSource? captureSink = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(insert);
        string details = $"target={DescribeTarget(insert.SchemaName, insert.TableName)}";
        if (insert.Returning is not null) details += " RETURNING";
        return new DmlPlan(
            catalog,
            operatorName: "Insert",
            details: details,
            sourcePlan: sourcePlan,
            apply: bctx => InsertExecutor.ExecuteAsync(catalog, insert, sourcePlan, captureSink, bctx));
    }

    /// <summary>
    /// Builds a DML plan for <c>UPDATE</c>. The driver scan lives inside
    /// the executor; no separate child source plan.
    /// </summary>
    public static DmlPlan ForUpdate(
        TableCatalog catalog,
        UpdateStatement update,
        CapturedRowsSource? captureSink = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(update);
        string details = $"target={DescribeTarget(update.SchemaName, update.TableName)}";
        if (update.Returning is not null) details += " RETURNING";
        return new DmlPlan(
            catalog,
            operatorName: "Update",
            details: details,
            sourcePlan: null,
            apply: bctx => UpdateExecutor.ExecuteAsync(catalog, update, captureSink, bctx));
    }

    /// <summary>
    /// Builds a DML plan for <c>DELETE</c>. The driver scan lives inside
    /// the executor; no separate child source plan.
    /// </summary>
    public static DmlPlan ForDelete(
        TableCatalog catalog,
        DeleteStatement delete,
        CapturedRowsSource? captureSink = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(delete);
        string details = $"target={DescribeTarget(delete.SchemaName, delete.TableName)}";
        if (delete.Returning is not null) details += " RETURNING";
        return new DmlPlan(
            catalog,
            operatorName: "Delete",
            details: details,
            sourcePlan: null,
            apply: bctx => DeleteExecutor.ExecuteAsync(catalog, delete, captureSink, bctx));
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"DmlPlan '{ExplainTree.OperatorName}' has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to run it again.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Pure side effect. When a capture sink was supplied at construction,
        // the executor populates it as part of the apply; a wrapping
        // DmlReturningPlan composer projects the captured rows. This plan
        // yields nothing on its own — RETURNING projection is no longer
        // chained through an inner-plan forwarding loop here.
        await _apply(batchContext).ConfigureAwait(false);
        yield break;
    }

    private static string DescribeTarget(string? schemaName, string tableName) =>
        schemaName is null ? tableName : $"{schemaName}.{tableName}";
}
