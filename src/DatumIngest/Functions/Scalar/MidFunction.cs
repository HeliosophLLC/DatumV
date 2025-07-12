using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts a substring using start position and length.
/// <c>mid(str, start, length)</c>
/// Uses 0-based indexing.
/// </summary>
public sealed class MidFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "mid";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException("mid() requires exactly 3 arguments: string, start, length.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"mid() first argument must be String, got {argumentKinds[0]}.");
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
        int length = (int)arguments[2].AsScalar();

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
}
