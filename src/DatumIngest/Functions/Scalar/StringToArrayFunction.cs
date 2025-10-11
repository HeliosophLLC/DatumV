using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Splits a string at occurrences of a delimiter, producing a text array.
/// <c>string_to_array(string, delimiter [, null_string])</c>
/// If delimiter is NULL, each character becomes a separate element.
/// If null_string is provided, fields matching it are replaced by NULL.
/// </summary>
public sealed class StringToArrayFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "string_to_array";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException(
                "string_to_array() requires 2 or 3 arguments: string, delimiter [, null_string].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"string_to_array() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"string_to_array() second argument must be String, got {argumentKinds[1]}.");
        }

        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"string_to_array() third argument must be String, got {argumentKinds[2]}.");
        }

        return DataKind.Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.NullArray(DataKind.String);
        }

        string input = arguments[0].AsString();

        string? nullString = null;
        if (arguments.Length == 3 && !arguments[2].IsNull)
        {
            nullString = arguments[2].AsString();
        }

        string[] parts;
        if (arguments[1].IsNull)
        {
            // NULL delimiter: split into individual characters
            parts = new string[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                parts[i] = input[i].ToString();
            }
        }
        else
        {
            string delimiter = arguments[1].AsString();
            parts = delimiter.Length == 0
                ? [input]
                : input.Split(delimiter);
        }

        DataValue[] elements = new DataValue[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            elements[i] = nullString != null && parts[i] == nullString
                ? DataValue.Null(DataKind.String)
                : DataValue.FromString(parts[i]);
        }

        return DataValue.FromArray(DataKind.String, elements);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.NullArray(DataKind.String);
        }

        ReadOnlySpan<char> input = arguments[0].AsStringSpan(store, out char[] inputRented);

        ReadOnlySpan<char> nullStr = default;
        char[]? nullRented = null;
        bool hasNullStr = arguments.Length == 3 && !arguments[2].IsNull;
        if (hasNullStr)
            nullStr = arguments[2].AsStringSpan(store, out nullRented);

        try
        {
            if (arguments[1].IsNull)
            {
                // NULL delimiter: split into individual characters.
                DataValue[] charElements = new DataValue[input.Length];
                for (int i = 0; i < input.Length; i++)
                    charElements[i] = DataValue.FromCharSpan(input.Slice(i, 1), store);
                return DataValue.FromArray(DataKind.String, charElements);
            }

            ReadOnlySpan<char> delimiter = arguments[1].AsStringSpan(store, out char[] delimRented);

            try
            {
                if (delimiter.Length == 0)
                {
                    DataValue[] single = [DataValue.FromCharSpan(input, store)];
                    return DataValue.FromArray(DataKind.String, single);
                }

                // Count segments first, then split.
                int segmentCount = 1;
                ReadOnlySpan<char> scan = input;
                while (true)
                {
                    int pos = delimiter.Length == 1
                        ? scan.IndexOf(delimiter[0])
                        : scan.IndexOf(delimiter);
                    if (pos < 0) break;
                    segmentCount++;
                    scan = scan[(pos + delimiter.Length)..];
                }

                Span<Range> ranges = segmentCount <= 64
                    ? stackalloc Range[segmentCount]
                    : new Range[segmentCount];
                int count = delimiter.Length == 1
                    ? input.Split(ranges, delimiter[0])
                    : input.Split(ranges, delimiter);

                DataValue[] elements = new DataValue[count];
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<char> part = input[ranges[i]];
                    elements[i] = hasNullStr && part.SequenceEqual(nullStr)
                        ? DataValue.Null(DataKind.String)
                        : DataValue.FromCharSpan(part, store);
                }

                return DataValue.FromArray(DataKind.String, elements);
            }
            finally
            {
                System.Buffers.ArrayPool<char>.Shared.Return(delimRented);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(inputRented);
            if (nullRented is not null) System.Buffers.ArrayPool<char>.Shared.Return(nullRented);
        }
    }
}
