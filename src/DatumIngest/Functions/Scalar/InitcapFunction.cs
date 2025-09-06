using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Converts the first letter of each word to uppercase and the rest to lowercase.
/// <c>initcap(string)</c> treats any non-alphanumeric character as a word separator.
/// </summary>
public sealed class InitcapFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "initcap";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("initcap() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"initcap() requires a String argument, got {argumentKinds[0]}.");
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
        char[] result = new char[text.Length];
        bool capitalizeNext = true;

        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (!char.IsLetterOrDigit(character))
            {
                result[i] = character;
                capitalizeNext = true;
            }
            else if (capitalizeNext)
            {
                result[i] = char.ToUpperInvariant(character);
                capitalizeNext = false;
            }
            else
            {
                result[i] = char.ToLowerInvariant(character);
            }
        }

        return DataValue.FromString(new string(result));
    }
}
