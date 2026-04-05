using DatumIngest.Functions;

namespace DatumIngest.Execution;

/// <summary>
/// Invokes a first-class <see cref="DatumIngest.Model.DataKind.Lambda"/>
/// value with a set of arguments and returns its result. Implemented by
/// <see cref="ExpressionEvaluator"/>; exposed as an interface on
/// <see cref="EvaluationFrame.LambdaInvoker"/> so consumer functions
/// (animation drivers, array transformations, future higher-order users)
/// can invoke lambdas without taking a dependency on the concrete
/// evaluator class.
/// </summary>
/// <remarks>
/// <para>
/// The interface is deliberately small — a single method — because
/// invocation is the only operation a function body needs to perform on a
/// lambda. Introspection / signature queries / structural rewriting all
/// happen at plan time via the AST stored on
/// <see cref="LambdaValue.Body"/>; consumers don't need to do those at
/// run time.
/// </para>
/// <para>
/// The frame's <see cref="EvaluationFrame.LambdaInvoker"/> may be
/// <see langword="null"/> for frames constructed outside the query
/// pipeline (e.g. ad-hoc unit tests, sidecar tools). Consumers that
/// require lambda invocation should check for null and surface a clear
/// error rather than nulling-out into a less-helpful NRE.
/// </para>
/// </remarks>
public interface ILambdaInvoker
{
    /// <summary>
    /// Invokes <paramref name="lambda"/> with the supplied argument values.
    /// </summary>
    /// <param name="lambda">A <see cref="ValueRef"/> of <see cref="DatumIngest.Model.DataKind.Lambda"/>.</param>
    /// <param name="arguments">Per-parameter values. Length must equal the lambda's declared parameter count; mismatch throws.</param>
    /// <param name="frame">The caller's evaluation frame — supplies arenas, sidecar registry, type registry, etc. The lambda's closure captures replace the frame's <see cref="EvaluationFrame.Row"/> internally so the body's free-variable references resolve against the row in scope when the lambda was created.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>The lambda body's evaluation result as a <see cref="ValueRef"/>.</returns>
    ValueTask<ValueRef> InvokeLambdaAsync(
        ValueRef lambda,
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken);
}
