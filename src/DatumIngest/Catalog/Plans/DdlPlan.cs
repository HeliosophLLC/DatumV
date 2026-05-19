using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Catalog.Plans;

/// <summary>
/// Single delegate-backed <see cref="StatementPlan"/> for every DDL
/// shape (CREATE / DROP / ALTER / ANALYZE / REINDEX / SET search_path).
/// One class with <see cref="ExplainPlanNode.OperatorName"/> + Details
/// carrying the kind — DDL plans are structurally identical (zero
/// children, zero rows, one side-effect apply), so a discriminator
/// label is all that distinguishes them.
/// </summary>
/// <remarks>
/// <para>
/// Two construction modes:
/// </para>
/// <list type="bullet">
///   <item>
///     <term>Deferred apply</term>
///     <description>Pass a real <see cref="Func{T1, TResult}"/> that
///     mutates the catalog when the plan iterates. Used by
///     <see cref="TableCatalog.PlanAsync(string)"/> so
///     <c>EXPLAIN CREATE FUNCTION …</c> reads the tree without ever
///     triggering the apply.</description>
///   </item>
///   <item>
///     <term>Already-applied (<see cref="NoOp"/>)</term>
///     <description>The side effect already ran (e.g. the caller went
///     through <c>ExecuteStatementAsync</c>'s eager path); the returned
///     plan only exists to satisfy the <see cref="StatementPlan"/> contract
///     and yield zero rows. Iterating it is a no-op.</description>
///   </item>
/// </list>
/// <para>
/// <b>Idempotency:</b> the apply delegate runs at most once. The first
/// caller wins; subsequent <c>ExecuteAsync</c> calls throw — DDL isn't
/// safe to re-run. Re-plan the statement to apply it a second time.
/// </para>
/// </remarks>
internal sealed class DdlPlan : StatementPlan
{
    private readonly Func<CancellationToken, Task> _apply;
    private int _executed;

    public DdlPlan(
        TableCatalog catalog,
        string operatorName,
        string details,
        Func<CancellationToken, Task> apply)
        : base(catalog)
    {
        ArgumentNullException.ThrowIfNull(apply);

        _apply = apply;
        ExplainTree = new ExplainPlanNode
        {
            OperatorName = operatorName,
            Details = details,
            EstimatedRows = 0,
        };
    }

    /// <summary>
    /// Constructs an already-applied <see cref="DdlPlan"/>. The side
    /// effect ran in the caller's eager path; the returned plan only
    /// exists to yield zero rows and supply an <see cref="ExplainPlanNode"/>.
    /// </summary>
    public static DdlPlan NoOp(
        TableCatalog catalog,
        string operatorName,
        string details = "applied at plan time; no rows produced")
        => new(catalog, operatorName, details, _ => Task.CompletedTask);

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Execution.ExecutionContext context)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"DdlPlan '{ExplainTree.OperatorName}' has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to apply it again.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        await _apply(cancellationToken).ConfigureAwait(false);
        
        yield break;
    }
}
