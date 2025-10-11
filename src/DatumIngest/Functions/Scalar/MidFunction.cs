using System.Buffers;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts a substring using start position and length.
/// <c>mid(str, start, length)</c>
/// Uses 1-based indexing (PostgreSQL semantics).
/// </summary>
public sealed class MidFunction : IScalarFunction
{
    private static readonly string[] ArgumentNamesArray = ["string", "start", "length"];

    /// <inheritdoc />
    public string Name => "mid";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        FunctionArgumentException.ThrowIfArgumentCountMismatch(Name, argumentKinds.Length, ArgumentNamesArray);
        FunctionArgumentException.ThrowIfArgumentKindMismatch(Name, 0, "string", DataKind.String, argumentKinds[0]);
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 1, "start", argumentKinds[1]);
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 2, "length", argumentKinds[2]);

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
        int start = arguments[1].ToInt32() - 1;
        int length = arguments[2].ToInt32();

        if (start < 0)
        {
            start = 0;
        }

        if (start >= text.Length)
        {
            return DataValue.FromString(string.Empty);
        }

        // Clamp length to available characters.
        int availableLength = text.Length - start;
        if (length > availableLength)
        {
            length = availableLength;
        }

        return DataValue.FromString(text.Substring(start, length));
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
        int start = arguments[1].ToInt32() - 1;
        int length = arguments[2].ToInt32();

        if (start < 0)
        {
            start = 0;
        }

        if (start >= span.Length || length <= 0)
        {
            ArrayPool<char>.Shared.Return(rented);
            return DataValue.FromCharSpan(ReadOnlySpan<char>.Empty, store);
        }

        int availableLength = span.Length - start;
        if (length > availableLength)
        {
            length = availableLength;
        }

        DataValue result = DataValue.FromCharSpan(span.Slice(start, length), store);
        ArrayPool<char>.Shared.Return(rented);
        return result;
    }
}
