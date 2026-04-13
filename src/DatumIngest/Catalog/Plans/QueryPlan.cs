using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// Default <see cref="IQueryPlan"/> implementation. Hoists literals once
/// at construction into a long-lived arena, then drives execution with a
/// fresh per-call <see cref="ExecutionContext"/>.
/// </summary>
internal sealed class QueryPlan : IQueryPlan
{
    private readonly TableCatalog _catalog;
    private readonly FunctionRegistry _functions;
    private readonly QueryOperator _operator;
    private readonly Arena _hoistStore;

    public QueryPlan(QueryOperator op, TableCatalog catalog, FunctionRegistry functions)
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
        // "already pooled" assertion. Released in `Dispose` (TODO when QueryPlan
        // becomes IDisposable; for now leak is bounded by query lifetime since
        // QueryPlan and _hoistStore have the same scope).
        _hoistStore.AddReference();
        _operator = op.RewriteExpressions(expr => LiteralHoister.Hoist(expr, _hoistStore));
    }

    public ExplainPlanNode ExplainTree => QueryExplainer.Explain(_operator);

    public async Task<ExplainPlanNode> AnalyzeAsync(CancellationToken cancellationToken)
    {
        InstrumentedOperator instrumented = InstrumentedOperator.InstrumentTree(_operator);

        // Plumb the hoist store as context.Store so any operator that needs to
        // resolve a hoisted-literal DataValue's payload (offsets reference
        // _hoistStore) can do so via the well-known ExecutionContext.Store handle.
        // Without this, hoisted string literals >16 bytes are stranded in
        // _hoistStore and unreachable to operators downstream of the planner.
        // The factory snapshots the catalog's ModelTracer into the context.
        using DatumIngest.Execution.ExecutionContext context = _catalog.CreateExecutionContext(
            store: _hoistStore,
            cancellationToken: cancellationToken);
        // Owned accountant — start 1Hz sampling so the per-query
        // MemoryProfile is populated for inspection.
        context.Accountant.StartProfiling();

        await foreach (RowBatch batch in instrumented.ExecuteAsync(context).WithCancellation(cancellationToken))
        {
            _catalog.Pool.ReturnRowBatch(batch);
        }

        ExplainPlanNode tree = QueryExplainer.Explain(instrumented);
        InstrumentedOperator.PopulateMetrics(tree, instrumented);
        return tree;
    }

    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext? batchContext)
    {
        // Plumb the hoist store as context.Store so any operator that needs to
        // resolve a hoisted-literal DataValue's payload (offsets reference
        // _hoistStore) can do so via the well-known ExecutionContext.Store handle.
        // Without this, hoisted string literals >16 bytes are stranded in
        // _hoistStore and unreachable to operators downstream of the planner.
        // Factory snapshots the catalog's ModelTracer. Procedural variable
        // substrate (VariableScope / VariableStore) is borrowed from the
        // enclosing batch context — null when running outside a procedural
        // batch (every existing top-level query path); references to @var
        // in that case throw at evaluation time.
        using DatumIngest.Execution.ExecutionContext context = _catalog.CreateExecutionContext(
            store: _hoistStore,
            types: batchContext?.Types,
            // Inside a procedural batch, share the batch-scoped accountant so
            // every query's residency rolls up under one budget. Standalone
            // queries get an owned accountant constructed by the context.
            accountant: batchContext?.Accountant,
            variableScope: batchContext?.VariableScope,
            variableStore: batchContext?.VariableStore,
            cancellationToken: cancellationToken);
        // Standalone query owns its accountant — start 1Hz sampling here.
        // Inside a batch, the batch is responsible for starting sampling on
        // its shared accountant, so we don't start it twice.
        if (batchContext is null)
        {
            context.Accountant.StartProfiling();
        }

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
