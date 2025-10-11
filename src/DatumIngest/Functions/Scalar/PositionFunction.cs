using System.Buffers;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the 1-based index of the first occurrence of a substring within a string.
/// <c>position(string, substring)</c> returns the index as a Scalar, or 0 if the substring is not found.
/// </summary>
public sealed class PositionFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "position";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("position() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"position() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"position() requires a String as the second argument, got {argumentKinds[1]}.");
        }

        return DataKind.Int32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue substring = arguments[1];

        if (input.IsNull || substring.IsNull)
        {
            return DataValue.Null(DataKind.Int32);
        }

        int index = input.AsString().IndexOf(substring.AsString(), StringComparison.Ordinal);
        return DataValue.FromInt32(index + 1);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        DataValue substring = arguments[1];

        if (input.IsNull || substring.IsNull)
        {
            return DataValue.Null(DataKind.Int32);
        }

        ReadOnlySpan<char> inputSpan = input.AsStringSpan(store, out char[] rentedInput);
        ReadOnlySpan<char> substringSpan = substring.AsStringSpan(store, out char[] rentedSubstring);
        int index = inputSpan.IndexOf(substringSpan, StringComparison.Ordinal);
        ArrayPool<char>.Shared.Return(rentedInput);
        ArrayPool<char>.Shared.Return(rentedSubstring);
        return DataValue.FromInt32(index + 1);
    }
}
