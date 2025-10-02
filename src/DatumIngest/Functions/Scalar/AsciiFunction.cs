using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the ASCII code of the first character in a string.
/// <c>ascii(string)</c> returns 0 for an empty string.
/// </summary>
public sealed class AsciiFunction : IScalarFunction
{
    private static readonly string[] ArgumentNamesArray = ["value"];

    /// <inheritdoc />
    public string Name => "ascii";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        FunctionArgumentException.ThrowIfArgumentCountMismatch(Name, argumentKinds.Length, ArgumentNamesArray);
        FunctionArgumentException.ThrowIfNotStringArgument(Name, 0, ArgumentNamesArray[0], argumentKinds[0]);

        return DataKind.Int32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Int32);
        }

        string text = input.AsString();
        return DataValue.FromInt32(text.Length == 0 ? 0 : text[0]);
    }
}
