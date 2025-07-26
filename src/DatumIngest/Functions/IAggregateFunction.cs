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
    /// The current aggregate result after all rows have been accumulated.
    /// </summary>
    DataValue Result { get; }
}
