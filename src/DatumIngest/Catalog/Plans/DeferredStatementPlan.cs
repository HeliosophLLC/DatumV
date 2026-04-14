using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// <see cref="IQueryPlan"/> wrapper that defers a DDL or DML statement's
/// side effects until iteration. Produced by
/// <see cref="TableCatalog.PlanAsync(Statement, string?, BatchContext?)"/>
/// for any non-query statement so the side effect (catalog mutation, table
/// rewrite, row write) does NOT run at plan time. This is what makes
/// <c>EXPLAIN CREATE FUNCTION</c> / <c>EXPLAIN DELETE</c> safe — the
/// controller reads <see cref="ExplainTree"/> without ever iterating
/// <see cref="ExecuteAsync"/>, so the underlying statement never runs.
/// </summary>
/// <remarks>
/// <para>
/// On iteration, the plan calls
/// <see cref="TableCatalog.ExecuteStatementAsync(Statement, string?, BatchContext?)"/>
/// (the eager path), then forwards the inner plan's rows. The eager path
/// is unchanged — DML executors still apply side effects + return their
/// row-yielding plans — we just delay invoking it until someone asks for
/// rows. Callers that want eager semantics call
/// <c>ExecuteStatementAsync</c> directly.
/// </para>
/// <para>
/// <b>Idempotency:</b> the plan executes its statement at most once. The
/// first <see cref="ExecuteAsync"/> caller wins; subsequent calls throw
/// (DDL/DML statements aren't safe to re-run). This matches PG's
/// "statement plan" lifecycle — a plan handle represents one pending
/// execution, not a reusable recipe.
/// </para>
/// </remarks>
internal sealed class DeferredStatementPlan : IQueryPlan
{
    private readonly TableCatalog _catalog;
    private readonly Statement _statement;
    private readonly string? _sourceText;
    private readonly BatchContext? _batchContext;
    private int _executed;

    public DeferredStatementPlan(
        TableCatalog catalog,
        Statement statement,
        string? sourceText,
        BatchContext? batchContext)
    {
        _catalog = catalog;
        _statement = statement;
        _sourceText = sourceText;
        _batchContext = batchContext;
        ExplainTree = new ExplainPlanNode
        {
            OperatorName = statement.GetType().Name,
            Details = "Side effects deferred until ExecuteAsync.",
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
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"DeferredStatementPlan for {_statement.GetType().Name} has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to run it again.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Run the eager path now. ExecuteStatementAsync applies DDL/DML side
        // effects and returns the inner plan (EmptyQueryPlan for no-rows DDL,
        // DmlReturningPlan for INSERT/UPDATE/DELETE … RETURNING).
        IQueryPlan inner = await _catalog.ExecuteStatementAsync(
            _statement, _sourceText, batchContext ?? _batchContext).ConfigureAwait(false);
        await foreach (RowBatch batch in inner.ExecuteAsync(cancellationToken, batchContext).ConfigureAwait(false))
        {
            yield return batch;
        }
    }
}
