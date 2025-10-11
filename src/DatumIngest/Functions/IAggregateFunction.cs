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
    /// Creates a new accumulator instance for a single group.
    /// Each group in a GROUP BY query gets its own accumulator.
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
    /// Incorporates one row's argument values into the running aggregate.
    /// </summary>
    /// <param name="arguments">The evaluated argument values for this row.</param>
    void Accumulate(ReadOnlySpan<DataValue> arguments);

    /// <summary>
    /// Incorporates one row's argument values with an explicit value store
    /// for resolving reference-type payloads.
    /// </summary>
    /// <param name="arguments">The evaluated argument values for this row.</param>
    /// <param name="store">The value store for reading/writing reference-type payloads.</param>
    void Accumulate(ReadOnlySpan<DataValue> arguments, IValueStore store) => Accumulate(arguments);

    /// <summary>
    /// Merges the state of another accumulator (of the same concrete type) into
    /// this one. Used by parallel hash aggregate to combine thread-local partial
    /// aggregations into a single result per group. The <paramref name="other"/>
    /// accumulator must not be used after merging.
    /// </summary>
    /// <param name="other">
    /// The accumulator to merge into this one. Must be the same concrete type.
    /// </param>
    void Merge(IAggregateAccumulator other);

    /// <summary>
    /// The current aggregate result after all rows have been accumulated.
    /// </summary>
    DataValue Result { get; }

    /// <summary>
    /// Resets the accumulator to its initial (empty) state so it can be reused
    /// for a different group without allocating a new instance. Implementations
    /// must clear all mutable state but should retain allocated collection
    /// capacity to avoid re-allocation on the next group.
    /// </summary>
    void Reset();
}
