using System.Buffers;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Trims specified characters from both sides of a string.
/// <c>btrim(string)</c> trims whitespace (same as <c>trim</c>).
/// <c>btrim(string, characters)</c> trims any character in the set from both ends.
/// </summary>
public sealed class BtrimFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "btrim";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("btrim() requires 1 or 2 arguments: string [, characters].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"btrim() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"btrim() second argument (characters) must be String, got {argumentKinds[1]}.");
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

        string text = input.AsString();

        if (arguments.Length == 1 || arguments[1].IsNull)
        {
            return DataValue.FromString(text.Trim());
        }

        char[] trimChars = arguments[1].AsString().ToCharArray();
        return DataValue.FromString(text.Trim(trimChars));
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        ReadOnlySpan<char> span = input.AsStringSpan(store, out char[] rented);

        DataValue result;
        if (arguments.Length == 1 || arguments[1].IsNull)
        {
            result = DataValue.FromCharSpan(span.Trim(), store);
            ArrayPool<char>.Shared.Return(rented);
        }
        else
        {
            ReadOnlySpan<char> trimSpan = arguments[1].AsStringSpan(store, out char[] rentedTrim);
            result = DataValue.FromCharSpan(span.Trim(trimSpan), store);
            ArrayPool<char>.Shared.Return(rented);
            ArrayPool<char>.Shared.Return(rentedTrim);
        }

        return result;
    }
}
