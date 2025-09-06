using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Performs character-by-character translation.
/// <c>translate(string, from, to)</c> replaces each character in the <c>from</c> string
/// with the corresponding character in the <c>to</c> string. Characters in
/// <c>from</c> that have no corresponding character in <c>to</c>
/// are deleted from the result.
/// </summary>
public sealed class TranslateFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "translate";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException("translate() requires exactly 3 arguments: string, from, to.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"translate() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"translate() second argument (from) must be String, got {argumentKinds[1]}.");
        }

        if (argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException($"translate() third argument (to) must be String, got {argumentKinds[2]}.");
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
        string from = arguments[1].AsString();
        string to = arguments[2].AsString();

        StringBuilder builder = new(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            int mappingIndex = from.IndexOf(text[i]);
            if (mappingIndex < 0)
            {
                builder.Append(text[i]);
            }
            else if (mappingIndex < to.Length)
            {
                builder.Append(to[mappingIndex]);
            }
            // else: character is in 'from' but has no 'to' counterpart — delete it
        }

        return DataValue.FromString(builder.ToString());
    }
}
