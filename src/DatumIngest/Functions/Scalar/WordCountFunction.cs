using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the number of whitespace-separated words in a string.
/// <c>word_count(text)</c> splits on <c>\s+</c> and counts non-empty segments.
/// Returns <c>0</c> for empty or whitespace-only strings, <c>null</c> for null input.
/// </summary>
public sealed class WordCountFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "word_count";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("word_count() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not DataKind.String)
        {
            throw new ArgumentException($"word_count() requires a String argument, got {argumentKinds[0]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        string text = input.AsString();
        if (text.Length == 0)
        {
            return DataValue.FromScalar(0);
        }

        int count = 0;
        bool inWord = false;

        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return DataValue.FromScalar(count);
    }
}
