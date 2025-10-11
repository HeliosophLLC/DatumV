using System.Buffers;
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

        if (!DataValue.IsIntegerKind(argumentKinds[1]))
        {
            throw new ArgumentException($"rpad() requires an integer as the second argument, got {argumentKinds[1]}.");
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
        int targetLength = lengthValue.ToInt32();
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

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        DataValue lengthValue = arguments[1];

        if (input.IsNull || lengthValue.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        if (arguments.Length == 3 && arguments[2].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        ReadOnlySpan<char> inputSpan = input.AsStringSpan(store, out char[] rentedInput);
        int targetLength = lengthValue.ToInt32();

        if (targetLength <= 0)
        {
            ArrayPool<char>.Shared.Return(rentedInput);
            return DataValue.FromCharSpan(ReadOnlySpan<char>.Empty, store);
        }

        if (inputSpan.Length >= targetLength)
        {
            DataValue truncated = DataValue.FromCharSpan(inputSpan[..targetLength], store);
            ArrayPool<char>.Shared.Return(rentedInput);
            return truncated;
        }

        char[]? rentedFillArray = null;
        ReadOnlySpan<char> fillSpan = arguments.Length == 3
            ? arguments[2].AsStringSpan(store, out rentedFillArray)
            : " ".AsSpan();

        int paddingNeeded = targetLength - inputSpan.Length;
        char[] padded = ArrayPool<char>.Shared.Rent(targetLength);

        inputSpan.CopyTo(padded.AsSpan(0));

        for (int i = 0; i < paddingNeeded; i++)
        {
            padded[inputSpan.Length + i] = fillSpan[i % fillSpan.Length];
        }

        DataValue result = DataValue.FromCharSpan(padded.AsSpan(0, targetLength), store);
        ArrayPool<char>.Shared.Return(padded);
        ArrayPool<char>.Shared.Return(rentedInput);
        if (rentedFillArray is not null)
        {
            ArrayPool<char>.Shared.Return(rentedFillArray);
        }

        return result;
    }
}
