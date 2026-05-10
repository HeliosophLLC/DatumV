using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for SELECT and CALL (which the catalog
/// lowers to a tableless SELECT). Hoists literals once at construction
/// into a long-lived arena, then drives execution with a fresh per-call
/// <see cref="ExecutionContext"/>.
/// </summary>
internal sealed class SelectPlan : StatementPlan
{
    private readonly TableCatalog _catalog;
    private readonly FunctionRegistry _functions;
    private readonly QueryOperator _operator;
    private readonly Arena _hoistStore;

    public SelectPlan(QueryOperator op, TableCatalog catalog, FunctionRegistry functions)
    {
        _catalog = catalog;
        _functions = functions;

        // Hoist once into a plan-scoped store so the resulting LiteralValueExpression
        // payloads outlive any individual ExecuteAsync / AnalyzeAsync call. Otherwise
        // the second run would dereference a recycled arena.
        _hoistStore = new Arena();
        // Baseline reference so the arena never hits refcount 0 and gets pooled by
        // mid-query batch returns. Under one-arena-per-query, every batch rent /
        // return cycles this arena's refcount; without the baseline, a balanced
        // cycle dips through 0 and `PoolBacking.TryReturn → Arena.Pool()` adds it
        // to the freelist, breaking subsequent rents and tripping the
        // "already pooled" assertion. Released in `Dispose` (TODO when SelectPlan
        // becomes IDisposable; for now leak is bounded by query lifetime since
        // SelectPlan and _hoistStore have the same scope).
        _hoistStore.AddReference();
        _operator = op.RewriteExpressions(expr => LiteralHoister.Hoist(expr, _hoistStore));
    }

    public override ExplainPlanNode ExplainTree => QueryExplainer.Explain(_operator);

    public override TableCatalog Catalog => _catalog;

    public override async Task<ExplainPlanNode> AnalyzeAsync(
        CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        ArgumentNullException.ThrowIfNull(batchContext);
        InstrumentedOperator instrumented = InstrumentedOperator.InstrumentTree(_operator);

        // Plumb the hoist store as context.Store so any operator that needs to
        // resolve a hoisted-literal DataValue's payload (offsets reference
        // _hoistStore) can do so via the well-known ExecutionContext.Store handle.
        // Without this, hoisted string literals >16 bytes are stranded in
        // _hoistStore and unreachable to operators downstream of the planner.
        // The factory snapshots the catalog's ModelTracer into the context.
        // Borrow the batch's accountant + scope / store / type registry so
        // residency accounting rolls up uniformly regardless of whether the
        // analyze is standalone or nested inside a procedural batch.
        using DatumIngest.Execution.ExecutionContext context = _catalog.CreateExecutionContext(
            store: _hoistStore,
            types: batchContext.Types,
            accountant: batchContext.Accountant,
            variableScope: batchContext.VariableScope,
            variableStore: batchContext.VariableStore,
            batchContext: batchContext,
            cancellationToken: cancellationToken);

        await foreach (RowBatch batch in instrumented.ExecuteAsync(context).WithCancellation(cancellationToken))
        {
            _catalog.Pool.ReturnRowBatch(batch);
        }

        ExplainPlanNode tree = QueryExplainer.Explain(instrumented);
        InstrumentedOperator.PopulateMetrics(tree, instrumented);
        return tree;
    }

    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        // Plumb the hoist store as context.Store so any operator that needs to
        // resolve a hoisted-literal DataValue's payload (offsets reference
        // _hoistStore) can do so via the well-known ExecutionContext.Store handle.
        // Without this, hoisted string literals >16 bytes are stranded in
        // _hoistStore and unreachable to operators downstream of the planner.
        // Factory snapshots the catalog's ModelTracer. Procedural variable
        // substrate (VariableScope / VariableStore) and the shared accountant
        // are borrowed from the batch context — the batch owns both
        // lifecycles and is responsible for starting profiling on the
        // accountant before any plan runs.
        using DatumIngest.Execution.ExecutionContext context = _catalog.CreateExecutionContext(
            store: _hoistStore,
            types: batchContext.Types,
            accountant: batchContext.Accountant,
            variableScope: batchContext.VariableScope,
            variableStore: batchContext.VariableStore,
            batchContext: batchContext,
            cancellationToken: cancellationToken);

        // Auto-return the previous batch when the consumer asks for the next one.
        // Consumers must finish using the current batch before iterating; in practice
        // all known consumers (TableFormatter, CSV export, .explain drain) extract or
        // print rows synchronously inside the loop body before MoveNextAsync runs again.
        RowBatch? previous = null;
        try
        {
            await foreach (RowBatch batch in _operator.ExecuteAsync(context).WithCancellation(cancellationToken))
            {
                if (previous is not null)
                {
                    _catalog.Pool.ReturnRowBatch(previous);
                }
                previous = batch;
                yield return batch;
            }
        }
        finally
        {
            if (previous is not null)
            {
                _catalog.Pool.ReturnRowBatch(previous);
            }
        }
    }
}
