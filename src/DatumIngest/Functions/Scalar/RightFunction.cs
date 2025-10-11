using System.Buffers;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the rightmost characters of a string.
/// <c>right(string, n)</c> returns the last <c>n</c> characters from the input string.
/// When <c>n</c> is negative, returns all but the first <c>|n|</c> characters.
/// </summary>
public sealed class RightFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "right";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("right() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"right() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (!DataValue.IsIntegerKind(argumentKinds[1]))
        {
            throw new ArgumentException($"right() requires an integer as the second argument, got {argumentKinds[1]}.");
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
            int start = System.Math.Abs(count);
            return DataValue.FromString(start >= inputString.Length ? string.Empty : inputString[start..]);
        }

        string result = inputString[System.Math.Max(0, inputString.Length - count)..];
        return DataValue.FromString(result);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        DataValue countValue = arguments[1];

        if (input.IsNull || countValue.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        ReadOnlySpan<char> span = input.AsStringSpan(store, out char[] rented);
        int count = countValue.ToInt32();

        DataValue result;
        if (count < 0)
        {
            int start = System.Math.Abs(count);
            result = start >= span.Length ? DataValue.FromCharSpan(ReadOnlySpan<char>.Empty, store) : DataValue.FromCharSpan(span[start..], store);
        }
        else
        {
            result = DataValue.FromCharSpan(span[System.Math.Max(0, span.Length - count)..], store);
        }

        ArrayPool<char>.Shared.Return(rented);
        return result;
    }
}
