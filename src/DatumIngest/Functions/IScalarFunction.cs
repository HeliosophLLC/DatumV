using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Scalar SQL function operating on <see cref="ValueRef"/>: takes one or
/// more arguments and produces a single value. Functions never touch
/// arenas, never thread an <c>IValueStore</c>, never call <c>Stabilize</c>;
/// the evaluator handles store-routing at the call boundary.
/// </summary>
/// <remarks>
/// <para>
/// Implementing types pair this instance interface with <see cref="IFunction"/>
/// for static-abstract metadata. Both interfaces are required for
/// <see cref="FunctionRegistry.RegisterScalar{T}"/>.
/// </para>
/// </remarks>
public interface IScalarFunction
{
    /// <summary>
    /// Validates argument kinds and returns the result kind. Most
    /// implementations forward to <see cref="FunctionMetadata.Validate{T}"/>;
    /// runtime-typed return shapes (<c>cast</c>, <c>try_cast</c>) override
    /// the default rule with a custom resolver.
    /// </summary>
    /// <exception cref="FunctionArgumentException">
    /// The argument kinds do not match any signature variant.
    /// </exception>
    DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds);

    /// <summary>
    /// Executes the function on already-materialized arguments. The
    /// evaluator converts arena/sidecar-backed payloads to managed
    /// objects before calling, and converts the returned
    /// <see cref="ValueRef"/> back to a <see cref="DataValue"/> after.
    /// </summary>
    /// <param name="arguments">Argument values.</param>
    /// <param name="frame">
    /// Per-row evaluation frame, available for functions that need
    /// row context (correlated columns, sidecar-bound source tables).
    /// Most scalar functions can ignore it.
    /// </param>
    ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame);

    /// <summary>
    /// Async variant of <see cref="Execute"/>. The default implementation
    /// wraps the sync method in a synchronously-completed
    /// <see cref="ValueTask{TResult}"/> — allocation-free for sync-only
    /// functions, which is the majority. Functions that need genuine async
    /// (model dispatch, network I/O, file reads) override this directly and
    /// leave <see cref="Execute"/> to throw or forward to a sync wrapper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Argument shape change vs the sync method: <see cref="ReadOnlyMemory{T}"/>
    /// instead of <see cref="ReadOnlySpan{T}"/>, value <see cref="EvaluationFrame"/>
    /// instead of <c>in</c>. Spans and <c>in</c> parameters are stack-only and
    /// can't cross an <c>await</c>, which would forbid every interesting async
    /// implementation. The evaluator allocates the argument buffer on the heap
    /// (<see cref="System.Buffers.ArrayPool{T}"/>) so callers see no extra
    /// allocation — only the slice shape changes.
    /// </para>
    /// </remarks>
    /// <param name="arguments">Argument values, in declaration order.</param>
    /// <param name="frame">Per-row evaluation frame; see <see cref="Execute"/>.</param>
    /// <param name="cancellationToken">
    /// Cooperative cancellation. Implementations that perform I/O (model
    /// dispatch, network calls) should honour it; sync wrappers ignore it.
    /// </param>
    ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
        => new(Execute(arguments.Span, in frame));

    /// <summary>
    /// Cost weight of a single invocation in Query Units (QU). Used for
    /// billing, governance budgets, and pre-execution cost estimation.
    /// </summary>
    int QueryUnitCost => 1;

    /// <summary>
    /// Whether this function's result is a typed array whose element kind is
    /// returned by <see cref="ValidateArguments"/>. Mirrors
    /// <see cref="IAggregateFunction.ProducesArray"/>. Defaults to
    /// <see langword="false"/>; override to <see langword="true"/> on
    /// functions like <c>array()</c> and <c>cyclical_encode()</c> that
    /// return <c>Array&lt;T&gt;</c> values.
    /// </summary>
    bool ProducesArray => false;

    /// <summary>
    /// Whether this function is pure: same arguments always yield the same
    /// result, with no observable side effects. Defaults to <see langword="true"/>;
    /// functions whose output depends on time, randomness, or external state
    /// (<c>now()</c>, <c>random()</c>, file/network IO) must override to
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Common-subexpression elimination uses this flag as the eligibility gate:
    /// only subtrees whose every leaf function is pure can be hoisted into a
    /// shared evaluation. Marking a function pure when it isn't produces silent
    /// correctness bugs (two textual references collapse to one evaluation),
    /// so the default-true posture demands deliberate review for any function
    /// that touches external state.
    /// </remarks>
    bool IsPure => true;
}
