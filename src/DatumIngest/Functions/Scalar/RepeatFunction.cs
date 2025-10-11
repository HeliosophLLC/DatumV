using System.Buffers;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Repeats a string a specified number of times.
/// <c>repeat(string, count)</c> returns the input string repeated <c>count</c> times.
/// </summary>
public sealed class RepeatFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "repeat";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("repeat() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"repeat() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (!DataValue.IsIntegerKind(argumentKinds[1]))
        {
            throw new ArgumentException($"repeat() requires an integer as the second argument, got {argumentKinds[1]}.");
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

        int count = countValue.ToInt32();
        if (count <= 0)
        {
            return DataValue.FromString(string.Empty);
        }

        string result = string.Concat(Enumerable.Repeat(input.AsString(), count));
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

        int count = countValue.ToInt32();
        if (count <= 0)
        {
            return DataValue.FromString(string.Empty, store);
        }

        ReadOnlySpan<char> span = input.AsStringSpan(store, out char[] rented);
        int totalLength = span.Length * count;
        char[] resultBuf = ArrayPool<char>.Shared.Rent(totalLength);
        for (int i = 0; i < count; i++)
        {
            span.CopyTo(resultBuf.AsSpan(i * span.Length));
        }
        DataValue result = DataValue.FromCharSpan(resultBuf.AsSpan(0, totalLength), store);
        ArrayPool<char>.Shared.Return(resultBuf);
        ArrayPool<char>.Shared.Return(rented);
        return result;
    }
}
