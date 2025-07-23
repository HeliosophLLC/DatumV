using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Removes leading and trailing whitespace from a string.
/// <c>trim(string)</c> returns the input with whitespace stripped from both ends.
/// </summary>
public sealed class TrimFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "trim";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("trim() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"trim() requires a String argument, got {argumentKinds[0]}.");
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

        return DataValue.FromString(input.AsString().Trim());
    }
}
