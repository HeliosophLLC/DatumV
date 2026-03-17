using System.Diagnostics;

using DatumIngest.Diagnostics;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// A node in the query execution plan tree. Each operator produces
/// a stream of rows by pulling from its child operators or data sources.
/// </summary>
public abstract class QueryOperator
{
    /// <summary>
    /// Initializes a new <see cref="QueryOperator"/> instance.
    /// </summary>
    public QueryOperator()
    {
    }


    /// <summary>
    /// Executes this operator and streams row batches asynchronously. Every
    /// invocation opens an <see cref="Activity"/> on
    /// <see cref="DatumActivity.Operators"/> for the duration of the
    /// iterator — <see cref="Activity.Current"/> nests through the operator
    /// chain (Scan → Filter → Project → …) automatically via the
    /// <see cref="AsyncLocal{T}"/> backing, so
    /// <see cref="DatumActivity.CurrentStack"/> reports the live stack of
    /// in-flight operators at any moment.
    /// </summary>
    /// <param name="context">Execution context with cancellation, functions, and catalog.</param>
    /// <returns>An async stream of row batches.</returns>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        // StartActivity returns null when no ActivityListener is attached, in
        // which case the `using` is a no-op and the call site is effectively
        // a single null check — zero observable overhead when nothing is
        // listening. When a listener is present (RecentActivityLog,
        // dotnet-trace, OpenTelemetry, etc.) the Activity is started here
        // and disposed when the iterator is fully iterated or torn down
        // early (the `using` declaration's scope is the entire iterator).
        using Activity? activity = DatumActivity.Operators.StartActivity(GetType().Name);

        await foreach (RowBatch rowBatch in ExecuteAsyncImpl(context).ConfigureAwait(false))
        {
            yield return rowBatch;
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    protected abstract IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context);

    /// <summary>
    /// Returns plan metadata describing this operator for EXPLAIN output.
    /// Every operator implementation must provide meaningful plan metadata
    /// including its name, properties, children, and any warnings or annotations.
    /// </summary>
    /// <returns>A description of this operator for the execution plan.</returns>
    public OperatorPlanDescription DescribeForExplain()
    {
        return DescribeForExplainImpl();
    }


    /// <summary>
    /// Returns plan metadata describing this operator for EXPLAIN output.
    /// Every operator implementation must provide meaningful plan metadata
    /// including its name, properties, children, and any warnings or annotations.
    /// </summary>
    /// <returns>A description of this operator for the execution plan.</returns>
    protected abstract OperatorPlanDescription DescribeForExplainImpl();

    /// <summary>
    /// Returns a copy of this operator with every contained <see cref="Expression"/>
    /// tree passed through <paramref name="rewriter"/> and every child operator
    /// recursively rewritten. Used by <see cref="LiteralHoister"/> to pre-materialize
    /// literal payloads into a query-scoped store.
    /// <para>
    /// Default returns <c>this</c> — correct for leaf operators that hold no
    /// expressions. Operators with an expression or a child operator should override
    /// to forward the rewrite. Operators that don't override still work; they simply
    /// opt out of the optimization (their descendants' literals stay unhoisted).
    /// </para>
    /// </summary>
    public virtual QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter) => this;
}
