using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog;

/// <summary>
/// Default <see cref="IQueryPlan"/> implementation. Hoists literals once
/// at construction into a long-lived arena, then drives execution with a
/// fresh per-call <see cref="ExecutionContext"/>.
/// </summary>
internal sealed class QueryPlan : IQueryPlan
{
    private readonly TableCatalog _catalog;
    private readonly FunctionRegistry _functions;
    private readonly PoolBacking _backing;
    private readonly IQueryOperator _operator;
    private readonly Arena _hoistStore;

    public QueryPlan(IQueryOperator op, TableCatalog catalog, FunctionRegistry functions, PoolBacking backing)
    {
        _catalog = catalog;
        _functions = functions;
        _backing = backing;

        // Hoist once into a plan-scoped store so the resulting LiteralValueExpression
        // payloads outlive any individual ExecuteAsync / AnalyzeAsync call. Otherwise
        // the second run would dereference a recycled arena.
        //
        // image() lowering already ran inside QueryPlanner.Finalize; we don't repeat it
        // here. Hoisting after lowering is fine — LiteralHoister recognises every node
        // type that can hold child expressions, including FunctionCallExpression args
        // and (transitively) the auxiliary-arg expressions referenced from
        // FusedImagePipelineExpression's child nodes.
        _hoistStore = new Arena();
        _operator = op.RewriteExpressions(expr => LiteralHoister.Hoist(expr, _hoistStore));
    }

    public ExplainPlanNode ExplainTree => QueryExplainer.Explain(_operator);

    public async Task<ExplainPlanNode> AnalyzeAsync(CancellationToken cancellationToken)
    {
        InstrumentedOperator instrumented = InstrumentedOperator.InstrumentTree(_operator);

        using LocalBufferPool localBufferPool = new(_backing);
        DatumIngest.Execution.ExecutionContext context = new(
            cancellationToken, _functions, _catalog, localBufferPool, _catalog.Pool);

        await foreach (RowBatch batch in instrumented.ExecuteAsync(context).WithCancellation(cancellationToken))
        {
            _catalog.Pool.ReturnRowBatch(batch);
        }

        ExplainPlanNode tree = QueryExplainer.Explain(instrumented);
        InstrumentedOperator.PopulateMetrics(tree, instrumented);
        return tree;
    }

    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using LocalBufferPool localBufferPool = new(_backing);
        DatumIngest.Execution.ExecutionContext context = new(
            cancellationToken, _functions, _catalog, localBufferPool, _catalog.Pool);

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
