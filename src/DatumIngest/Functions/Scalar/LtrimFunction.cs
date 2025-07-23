using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Removes leading whitespace from a string.
/// <c>ltrim(string)</c> returns the input with whitespace stripped from the start.
/// </summary>
public sealed class LtrimFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "ltrim";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("ltrim() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"ltrim() requires a String argument, got {argumentKinds[0]}.");
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

        return DataValue.FromString(input.AsString().TrimStart());
    }
}
