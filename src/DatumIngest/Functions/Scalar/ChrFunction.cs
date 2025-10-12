using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns a single character from an ASCII/Unicode code point.
/// <c>chr(code)</c> accepts a numeric argument and returns the corresponding character.
/// </summary>
public sealed class ChrFunction : IScalarFunction
{
    private static readonly string[] ArgumentNamesArray = ["code"];

    /// <inheritdoc />
    public string Name => "chr";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        FunctionArgumentException.ThrowIfArgumentCountMismatch(Name, argumentKinds.Length, ArgumentNamesArray);
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 0, ArgumentNamesArray[0], argumentKinds[0]);

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

        int codePoint = input.ToInt32();

        return DataValue.FromString(((char)codePoint).ToString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        int codePoint = input.ToInt32();
        return DataValue.FromString(((char)codePoint).ToString(), store);
    }
}
