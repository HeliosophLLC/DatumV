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
    /// <remarks>
    /// <para>
    /// Synchronously-completing implementations return
    /// <c>new ValueTask&lt;ValueRef&gt;(result)</c> — allocation-free, no state
    /// machine. Implementations that perform I/O (model dispatch, network
    /// calls) mark themselves <c>async</c> and use <c>await</c> normally.
    /// </para>
    /// <para>
    /// <see cref="ReadOnlyMemory{T}"/> rather than <see cref="ReadOnlySpan{T}"/>
    /// because spans and <c>in</c> parameters are stack-only and can't cross
    /// an <c>await</c>. Recover the span at the top of a sync body via
    /// <see cref="ReadOnlyMemory{T}.Span"/>; the evaluator allocates the
    /// argument buffer on the heap so callers see no extra allocation.
    /// </para>
    /// </remarks>
    /// <param name="arguments">Argument values, in declaration order.</param>
    /// <param name="frame">
    /// Per-row evaluation frame, available for functions that need row context
    /// (correlated columns, sidecar-bound source tables). Most scalar functions
    /// can ignore it.
    /// </param>
    /// <param name="cancellationToken">
    /// Cooperative cancellation. Implementations that perform I/O (model
    /// dispatch, network calls) should honour it; pure-arithmetic functions
    /// can ignore it.
    /// </param>
    ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken);

    /// <summary>
    /// Columnar batch entry-point: evaluates the function for
    /// <paramref name="rowCount"/> rows in one call. The default
    /// implementation loops calling <see cref="ExecuteAsync"/> row by row,
    /// so every existing function works without override. Functions whose
    /// dispatch benefits from cross-row work — model inference (pack rows
    /// into <c>[B, ...]</c> and run one <c>Session.Run</c>), SIMD scalar
    /// arithmetic (Vector&lt;T&gt; over a column), bulk image ops with
    /// reusable scratch — override to do the work efficiently then return
    /// per-row results in source order.
    /// </summary>
    /// <param name="argumentColumns">
    /// One <see cref="ReadOnlyMemory{ValueRef}"/> per declared parameter,
    /// in declaration order. Each column has length <paramref name="rowCount"/>;
    /// <c>argumentColumns[paramIdx].Span[rowIdx]</c> is the value of the
    /// <c>paramIdx</c>th parameter on the <c>rowIdx</c>th row.
    /// </param>
    /// <param name="rowCount">Number of rows in each column.</param>
    /// <param name="frame">
    /// Evaluation frame shared across all rows in the batch. Frame state
    /// that varies per row (<see cref="EvaluationFrame.Row"/>) is NOT
    /// rebound by the default loop — overrides that need per-row frame
    /// context must rebind themselves via <see cref="EvaluationFrame.WithRow"/>.
    /// Frame state that's invariant across the batch (current model,
    /// sidecar registry, type registry) is correct as-is.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>
    /// Per-row results in the same order as the input columns; length
    /// equals <paramref name="rowCount"/>.
    /// </returns>
    /// <remarks>
    /// The columnar shape is deliberate: per-parameter columns let
    /// overrides walk one column at a time (e.g. pack a Float32[] column
    /// into a single packed tensor) without per-row argument-tuple
    /// unpacking. The default loop pays that unpacking cost since it has
    /// to call the row-major <see cref="ExecuteAsync"/> anyway.
    /// </remarks>
    ValueTask<ValueRef[]> ExecuteBatchAsync(
        ReadOnlyMemory<ValueRef>[] argumentColumns,
        int rowCount,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
        => ScalarFunctionBatchHelpers.DefaultLoop(this, argumentColumns, rowCount, frame, cancellationToken);

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
