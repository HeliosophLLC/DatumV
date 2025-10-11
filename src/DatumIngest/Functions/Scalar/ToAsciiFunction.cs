using System.Buffers;
using System.Globalization;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Converts a string to ASCII by removing diacritical marks (accents).
/// <c>to_ascii(string)</c> — PostgreSQL compatible.
/// </summary>
public sealed class ToAsciiFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "to_ascii";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("to_ascii() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"to_ascii() requires a String argument, got {argumentKinds[0]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string input = arguments[0].AsString();
        string normalized = input.Normalize(NormalizationForm.FormD);
        StringBuilder sb = new(normalized.Length);

        foreach (char c in normalized)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return DataValue.FromString(sb.ToString().Normalize(NormalizationForm.FormC));
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        ReadOnlySpan<char> input = arguments[0].AsStringSpan(store, out char[] inputRented);

        // NFD normalization is only available as a string method, so we convert once here.
        // The string is allocated from the decoded span, not fetched from the store again.
        string normalized = new string(input).Normalize(NormalizationForm.FormD);

        if (inputRented is not null) ArrayPool<char>.Shared.Return(inputRented);

        // Filter out non-spacing marks (diacritics) into a rented buffer.
        char[] outputRented = ArrayPool<char>.Shared.Rent(normalized.Length);
        int written = 0;

        foreach (char c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                outputRented[written++] = c;
            }
        }

        // FormC re-compose any remaining combining sequences, then emit via span.
        string formC = new string(outputRented, 0, written).Normalize(NormalizationForm.FormC);
        ArrayPool<char>.Shared.Return(outputRented);

        return DataValue.FromCharSpan(formC.AsSpan(), store);
    }
}
