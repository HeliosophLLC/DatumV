using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// A node in the query execution plan tree. Each operator produces
/// a stream of rows by pulling from its child operators or data sources.
/// </summary>
public interface IQueryOperator
{
    /// <summary>
    /// Executes this operator and streams row batches asynchronously.
    /// </summary>
    /// <param name="context">Execution context with cancellation, functions, and catalog.</param>
    /// <returns>An async stream of row batches.</returns>
    IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context);

    /// <summary>
    /// Returns plan metadata describing this operator for EXPLAIN output.
    /// Every operator implementation must provide meaningful plan metadata
    /// including its name, properties, children, and any warnings or annotations.
    /// </summary>
    /// <returns>A description of this operator for the execution plan.</returns>
    OperatorPlanDescription DescribeForExplain();

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
    IQueryOperator RewriteExpressions(Func<Expression, Expression> rewriter) => this;
}
