using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Removes trailing characters from a string.
/// <c>rtrim(string)</c> removes trailing whitespace.
/// <c>rtrim(string, characters)</c> removes trailing characters that appear in the set.
/// </summary>
public sealed class RtrimFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "rtrim";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("rtrim() requires 1 or 2 arguments: string [, characters].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"rtrim() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"rtrim() second argument (characters) must be String, got {argumentKinds[1]}.");
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
            return DataValue.FromString(text.TrimEnd());
        }

        char[] trimChars = arguments[1].AsString().ToCharArray();
        return DataValue.FromString(text.TrimEnd(trimChars));
    }
}
