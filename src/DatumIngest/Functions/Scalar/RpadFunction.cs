using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Pads a string on the right to a specified length using a fill string.
/// <c>rpad(string, length, fill)</c> returns the input padded on the right to reach <c>length</c> characters.
/// If the input is already longer than <c>length</c>, it is truncated to <c>length</c>.
/// </summary>
public sealed class RpadFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "rpad";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException("rpad() requires 2 or 3 arguments: string, length [, fill].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"rpad() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.Float32)
        {
            throw new ArgumentException($"rpad() requires a Scalar as the second argument, got {argumentKinds[1]}.");
        }

        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException($"rpad() requires a String as the third argument, got {argumentKinds[2]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue lengthValue = arguments[1];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        if (lengthValue.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        if (arguments.Length == 3 && arguments[2].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string inputString = input.AsString();
        int targetLength = (int)lengthValue.AsFloat32();
        string fillString = arguments.Length == 3 ? arguments[2].AsString() : " ";

        if (targetLength <= 0)
        {
            return DataValue.FromString(string.Empty);
        }

        if (inputString.Length >= targetLength)
        {
            return DataValue.FromString(inputString[..targetLength]);
        }

        if (fillString.Length == 1)
        {
            return DataValue.FromString(inputString.PadRight(targetLength, fillString[0]));
        }

        int paddingNeeded = targetLength - inputString.Length;
        char[] padded = new char[targetLength];
        inputString.CopyTo(0, padded, 0, inputString.Length);

        int fillIndex = 0;
        for (int i = inputString.Length; i < targetLength; i++)
        {
            padded[i] = fillString[fillIndex];
            fillIndex = (fillIndex + 1) % fillString.Length;
        }

        return DataValue.FromString(new string(padded));
    }
}
