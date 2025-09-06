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
}
