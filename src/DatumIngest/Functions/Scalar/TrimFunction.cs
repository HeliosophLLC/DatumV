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
}
