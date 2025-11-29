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
    /// Cost weight of a single invocation in Query Units (QU). Used for
    /// billing, governance budgets, and pre-execution cost estimation.
    /// </summary>
    int QueryUnitCost => 1;

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
