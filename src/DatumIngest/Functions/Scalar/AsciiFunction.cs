using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the ASCII code of the first character in a string.
/// <c>ascii(string)</c> returns 0 for an empty string.
/// </summary>
public sealed class AsciiFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "ascii";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("ascii() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"ascii() requires a String argument, got {argumentKinds[0]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        string text = input.AsString();
        return DataValue.FromFloat32(text.Length == 0 ? 0 : text[0]);
    }
}
