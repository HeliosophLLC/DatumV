using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Removes trailing whitespace from a string.
/// <c>rtrim(string)</c> returns the input with whitespace stripped from the end.
/// </summary>
public sealed class RtrimFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "rtrim";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("rtrim() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"rtrim() requires a String argument, got {argumentKinds[0]}.");
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

        return DataValue.FromString(input.AsString().TrimEnd());
    }
}
