using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the leftmost characters of a string.
/// <c>left(string, n)</c> returns the first <c>n</c> characters from the input string.
/// When <c>n</c> is negative, returns all but the last <c>|n|</c> characters.
/// </summary>
public sealed class LeftFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "left";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("left() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"left() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (!DataValue.IsIntegerKind(argumentKinds[1]))
        {
            throw new ArgumentException($"left() requires an integer as the second argument, got {argumentKinds[1]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue countValue = arguments[1];

        if (input.IsNull || countValue.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string inputString = input.AsString();
        int count = countValue.ToInt32();

        if (count < 0)
        {
            int end = inputString.Length + count;
            return DataValue.FromString(end <= 0 ? string.Empty : inputString[..end]);
        }

        string result = inputString[..System.Math.Min(count, inputString.Length)];
        return DataValue.FromString(result);
    }
}
