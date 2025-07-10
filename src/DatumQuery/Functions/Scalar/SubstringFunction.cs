using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Functions.Scalar;

/// <summary>
/// Extracts a substring from a start position to the end, or with an optional length.
/// <c>substring(str, start)</c> or <c>substring(str, start, length)</c>
/// Uses 0-based indexing.
/// </summary>
public sealed class SubstringFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "substring";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2 || argumentKinds.Length > 3)
        {
            throw new ArgumentException("substring() requires 2 or 3 arguments: string, start, [length].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"substring() first argument must be String, got {argumentKinds[0]}.");
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
        int start = (int)arguments[1].AsScalar();

        if (start < 0)
        {
            start = 0;
        }

        if (start >= text.Length)
        {
            return DataValue.FromString(string.Empty);
        }

        if (arguments.Length == 3)
        {
            int length = (int)arguments[2].AsScalar();
            int availableLength = text.Length - start;
            if (length > availableLength)
            {
                length = availableLength;
            }
            return DataValue.FromString(text.Substring(start, length));
        }

        return DataValue.FromString(text[start..]);
    }
}
