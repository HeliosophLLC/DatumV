using System.Buffers;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Splits a string on a delimiter and returns the field at the given 1-based position.
/// <c>split_part(string, delimiter, n)</c> returns an empty string if the field number
/// is out of range. Negative field numbers count from the end.
/// </summary>
public sealed class SplitPartFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "split_part";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException("split_part() requires exactly 3 arguments: string, delimiter, field_number.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"split_part() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"split_part() second argument (delimiter) must be String, got {argumentKinds[1]}.");
        }

        if (!DataValue.IsIntegerKind(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"split_part() third argument (field number) requires an integer, got {argumentKinds[2]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull || arguments[2].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string text = arguments[0].AsString();
        string delimiter = arguments[1].AsString();
        int fieldNumber = arguments[2].ToInt32();

        if (fieldNumber == 0)
        {
            return DataValue.FromString(string.Empty);
        }

        string[] parts = text.Split(delimiter, StringSplitOptions.None);

        // Negative counts from end: -1 = last, -2 = second to last, etc.
        int index = fieldNumber > 0
            ? fieldNumber - 1
            : parts.Length + fieldNumber;

        if (index < 0 || index >= parts.Length)
        {
            return DataValue.FromString(string.Empty);
        }

        return DataValue.FromString(parts[index]);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull || arguments[1].IsNull || arguments[2].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        ReadOnlySpan<char> text      = arguments[0].AsStringSpan(store, out char[] textRented);
        ReadOnlySpan<char> delimiter = arguments[1].AsStringSpan(store, out char[] delimRented);
        int fieldNumber = arguments[2].ToInt32();

        try
        {
            if (fieldNumber == 0)
                return DataValue.FromCharSpan(ReadOnlySpan<char>.Empty, store);

            // Split into ranges on the stack. If more segments than the buffer,
            // the last range captures the remainder — still correct for forward access.
            Span<Range> ranges = stackalloc Range[64];
            int count = delimiter.Length == 1
                ? text.Split(ranges, delimiter[0])
                : text.Split(ranges, delimiter);

            // Resolve 1-based (positive) or end-relative (negative) to 0-based index.
            int index = fieldNumber > 0
                ? fieldNumber - 1
                : count + fieldNumber;

            if (index < 0 || index >= count)
                return DataValue.FromCharSpan(ReadOnlySpan<char>.Empty, store);

            return DataValue.FromCharSpan(text[ranges[index]], store);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(textRented);
            ArrayPool<char>.Shared.Return(delimRented);
        }
    }
}
