using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions;

/// <summary>
/// Extension interface for scalar functions that accept one or more
/// <see cref="LambdaExpression"/> arguments. Higher-order functions receive
/// the unevaluated lambda AST and a callback to evaluate it within the
/// current row context, rather than pre-evaluated <see cref="DataValue"/> arguments.
/// </summary>
/// <remarks>
/// <para>
/// The standard <see cref="IScalarFunction.Execute"/> path eagerly evaluates all
/// arguments to <see cref="DataValue"/> before invocation. This is incompatible
/// with lambdas — a lambda is not a value, it is a deferred expression that the
/// function invokes zero or more times with different bindings.
/// </para>
/// <para>
/// When the <see cref="DatumIngest.Execution.ExpressionEvaluator"/> encounters a
/// <see cref="FunctionCallExpression"/> whose resolved function implements this
/// interface, it routes to <see cref="ExecuteHigherOrder"/> instead of the standard
/// eager-evaluation path.
/// </para>
/// </remarks>
public interface IHigherOrderFunction : IScalarFunction
{
    /// <summary>
    /// Identifies which argument positions expect a <see cref="LambdaExpression"/>.
    /// The evaluator uses this to skip eager evaluation for those positions.
    /// </summary>
    /// <param name="argumentCount">The total number of arguments in the call.</param>
    /// <returns>
    /// A set of zero-based argument indices that must be lambda expressions.
    /// </returns>
    IReadOnlySet<int> GetLambdaParameterIndices(int argumentCount);

    /// <summary>
    /// Executes the function with a mix of evaluated <see cref="DataValue"/> arguments
    /// and unevaluated <see cref="LambdaExpression"/> arguments.
    /// </summary>
    /// <param name="arguments">
    /// Pre-evaluated argument values. Positions identified by
    /// <see cref="GetLambdaParameterIndices"/> contain <see cref="DataValue.UnknownNull()"/>
    /// as placeholders — the actual lambda is in <paramref name="lambdaArguments"/>.
    /// </param>
    /// <param name="lambdaArguments">
    /// Lambda expressions keyed by their zero-based argument index.
    /// </param>
    /// <param name="lambdaEvaluator">
    /// Callback to evaluate a lambda body with parameter bindings applied to the
    /// current row context. The function calls this per element (for array operations)
    /// or per invocation (for general higher-order application).
    /// </param>
    /// <returns>The computed result.</returns>
    DataValue ExecuteHigherOrder(
        ReadOnlySpan<DataValue> arguments,
        IReadOnlyDictionary<int, LambdaExpression> lambdaArguments,
        LambdaEvaluator lambdaEvaluator);
}

/// <summary>
/// Delegate for evaluating a <see cref="LambdaExpression"/> body with parameter
/// bindings applied to the enclosing row. Provided by the
/// <see cref="DatumIngest.Execution.ExpressionEvaluator"/> to
/// <see cref="IHigherOrderFunction"/> implementations.
/// </summary>
/// <param name="lambda">The lambda expression to evaluate.</param>
/// <param name="parameterValues">
/// Values to bind to the lambda's declared parameters, in declaration order.
/// The count must match <see cref="LambdaExpression.Parameters"/>.
/// </param>
/// <returns>The result of evaluating the lambda body.</returns>
public delegate DataValue LambdaEvaluator(LambdaExpression lambda, ReadOnlySpan<DataValue> parameterValues);
