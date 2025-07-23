using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Converts a string to upper-case using invariant culture rules.
/// <c>upper(string)</c> returns the input with all characters converted to their upper-case equivalents.
/// </summary>
public sealed class UpperFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "upper";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("upper() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"upper() requires a String argument, got {argumentKinds[0]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        return DataValue.FromString(input.AsString().ToUpperInvariant());
    }
}
