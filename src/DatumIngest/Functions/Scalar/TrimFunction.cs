using System.Buffers;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Removes leading and trailing characters from a string.
/// <c>trim(string)</c> removes whitespace from both ends.
/// <c>trim(string, characters)</c> removes characters that appear in the set from both ends.
/// </summary>
public sealed class TrimFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "trim";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("trim() requires 1 or 2 arguments: string [, characters].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"trim() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"trim() second argument (characters) must be String, got {argumentKinds[1]}.");
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
