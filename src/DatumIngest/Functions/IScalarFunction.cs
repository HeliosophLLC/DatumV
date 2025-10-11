using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Interface for scalar SQL functions that take one or more <see cref="DataValue"/>
/// arguments and produce a single <see cref="DataValue"/> result.
/// </summary>
public interface IScalarFunction
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
    /// Executes the function with the given arguments.
    /// </summary>
    /// <param name="arguments">The argument values.</param>
    /// <returns>The computed result.</returns>
    DataValue Execute(ReadOnlySpan<DataValue> arguments);

    /// <summary>
    /// Executes the function with the given arguments and an explicit value store
    /// for resolving reference-type payloads (strings, arrays, etc.).
    /// </summary>
    /// <param name="arguments">The argument values.</param>
    /// <param name="store">The value store for reading/writing reference-type payloads.</param>
    /// <returns>The computed result.</returns>
    DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store) => Execute(arguments);

    /// <summary>
    /// The cost weight of a single invocation of this function, measured in Query Units (QU).
    /// Used for billing, governance budgets, and pre-execution cost estimation.
    /// Higher values indicate more expensive operations (e.g. image transforms vs scalar math).
    /// </summary>
    int QueryUnitCost => 1;
}
