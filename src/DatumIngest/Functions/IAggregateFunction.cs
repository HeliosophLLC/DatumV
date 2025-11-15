using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Interface for aggregate SQL functions that accumulate values across
/// multiple rows and produce a single result per group.
/// </summary>
public interface IAggregateFunction
{
    /// <summary>The SQL function name (case-insensitive matching).</summary>
    string Name { get; }

    /// <summary>
    /// Validates the argument types and returns the result <see cref="DataKind"/>.
    /// </summary>
    /// <param name="argumentKinds">The kinds of the arguments being passed.</param>
    /// <returns>The kind of the result value.</returns>
    /// <exception cref="ArgumentException">The argument types are not valid for this function.</exception>
    DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds);

    /// <summary>
    /// Creates a new accumulator instance for a single group. Each group in a
    /// GROUP BY query gets its own accumulator. Per-call context (source/target
    /// stores, sidecar registry) flows in through the
    /// <see cref="InvocationFrame"/> passed to <c>Accumulate</c> and
    /// <c>Result</c>; the accumulator does not need it at construction time.
    /// </summary>
    IAggregateAccumulator CreateAccumulator();

    /// <summary>
    /// Returns the element kind of the result array when this aggregate produces a
    /// <see cref="DataKind.Array"/>, given the argument types at plan time.
    /// </summary>
    /// <param name="argumentKinds">The kinds of the arguments being passed.</param>
    /// <returns>
    /// The array element kind, or <c>null</c> when this function does not return
    /// a <see cref="DataKind.Array"/> or when the element kind cannot be
    /// determined statically.
    /// </returns>
    DataKind? GetResultArrayElementKind(ReadOnlySpan<DataKind> argumentKinds) => null;

    /// <summary>
    /// The cost weight of a single invocation of this function, measured in Query Units (QU).
    /// </summary>
    int QueryUnitCost => 1;
}

/// <summary>
/// Mutable accumulator that collects row values for a single aggregate
/// function within a single group. Created by
/// <see cref="IAggregateFunction.CreateAccumulator"/> and used by the
/// <c>GroupByOperator</c> to compute per-group results.
/// </summary>
public interface IAggregateAccumulator
{
    /// <summary>
    /// Incorporates one row's argument values into the running aggregate. The
    /// <paramref name="frame"/> resolves arena-backed argument payloads through
    /// <see cref="InvocationFrame.Source"/> and provides
    /// <see cref="InvocationFrame.Target"/> for any state the accumulator must
    /// stabilise across the source batch's lifetime (e.g. running min/max
    /// strings, accumulated string lists).
    /// </summary>
    /// <param name="arguments">The evaluated argument values for this row.</param>
    /// <param name="frame">Per-call invocation context.</param>
    void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame);

    /// <summary>
    /// Merges the state of another accumulator (of the same concrete type) into
    /// this one. Used by parallel hash aggregate to combine thread-local partial
    /// aggregations into a single result per group. The <paramref name="other"/>
    /// accumulator must not be used after merging.
    /// <para>
    /// Both accumulators were constructed and accumulated against the same Target
    /// store (per the parallel-aggregate contract — workers share
    /// <c>context.Store</c>), so any arena-backed payloads in either side resolve
    /// against <paramref name="frame"/>'s stores. Frame-aware Merge lets
    /// implementations like <c>Min</c>/<c>Max</c>/<c>ArgMax</c> use the store-aware
    /// <c>DataValueComparer.Compare</c> overload when comparing captured values
    /// across the merge.
    /// </para>
    /// </summary>
    /// <param name="other">
    /// The accumulator to merge into this one. Must be the same concrete type.
    /// </param>
    /// <param name="frame">Per-call invocation context.</param>
    void Merge(IAggregateAccumulator other, in InvocationFrame frame);

    /// <summary>
    /// Computes the current aggregate result. The <paramref name="frame"/>'s
    /// <see cref="InvocationFrame.Target"/> is the home for any non-inline
    /// payloads in the returned <see cref="DataValue"/> — string concatenations,
    /// array constructions, etc. Inline-result accumulators (Sum, Count, Avg)
    /// may ignore the frame.
    /// </summary>
    /// <param name="frame">Per-emit invocation context.</param>
    DataValue Result(in InvocationFrame frame);

    /// <summary>
    /// Resets the accumulator to its initial (empty) state so it can be reused
    /// for a different group without allocating a new instance. Implementations
    /// must clear all mutable state but should retain allocated collection
    /// capacity to avoid re-allocation on the next group.
    /// </summary>
    void Reset();
}
