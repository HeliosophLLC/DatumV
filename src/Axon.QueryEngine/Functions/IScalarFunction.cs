using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Functions;

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
}
