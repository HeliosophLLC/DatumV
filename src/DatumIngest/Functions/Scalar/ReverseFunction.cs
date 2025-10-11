using System.Buffers;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Reverses the characters in a string.
/// <c>reverse(string)</c> returns the input string with its character order reversed.
/// </summary>
public sealed class ReverseFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "reverse";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("reverse() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"reverse() requires a String argument, got {argumentKinds[0]}.");
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

        string inputString = input.AsString();
        string result = string.Create(inputString.Length, inputString, (span, state) =>
        {
            state.AsSpan().CopyTo(span);
            span.Reverse();
        });

        return DataValue.FromString(result);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        ReadOnlySpan<char> inputSpan = input.AsStringSpan(store, out char[] rented);
        char[] resultBuf = ArrayPool<char>.Shared.Rent(inputSpan.Length);
        inputSpan.CopyTo(resultBuf);
        resultBuf.AsSpan(0, inputSpan.Length).Reverse();
        DataValue result = DataValue.FromCharSpan(resultBuf.AsSpan(0, inputSpan.Length), store);
        ArrayPool<char>.Shared.Return(resultBuf);
        ArrayPool<char>.Shared.Return(rented);
        return result;
    }
}
