using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Splits a qualified SQL identifier into an array of identifiers, removing quoting.
/// <c>parse_ident(qualified_identifier [, strict_mode])</c>
/// Default strict_mode is true — extra characters after the last identifier cause an error.
/// </summary>
public sealed class ParseIdentFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "parse_ident";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException(
                "parse_ident() requires 1 or 2 arguments: qualified_identifier [, strict_mode].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"parse_ident() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.Boolean)
        {
            throw new ArgumentException(
                $"parse_ident() second argument must be Boolean, got {argumentKinds[1]}.");
        }

        return DataKind.Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.NullArray(DataKind.String);
        }

        string input = arguments[0].AsString();
        bool strict = true;
        if (arguments.Length == 2 && !arguments[1].IsNull)
        {
            strict = arguments[1].AsBoolean();
        }

        List<string> identifiers = [];
        int pos = 0;

        while (pos < input.Length)
        {
            // Skip whitespace
            while (pos < input.Length && char.IsWhiteSpace(input[pos])) pos++;
            if (pos >= input.Length) break;

            if (input[pos] == '"')
            {
                // Quoted identifier
                pos++;
                StringBuilder sb = new();
                while (pos < input.Length)
                {
                    if (input[pos] == '"')
                    {
                        pos++;
                        if (pos < input.Length && input[pos] == '"')
                        {
                            sb.Append('"');
                            pos++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        sb.Append(input[pos]);
                        pos++;
                    }
                }

                identifiers.Add(sb.ToString());
            }
            else if (char.IsLetter(input[pos]) || input[pos] == '_')
            {
                // Unquoted identifier — fold to lowercase
                int start = pos;
                while (pos < input.Length && (char.IsLetterOrDigit(input[pos]) || input[pos] == '_'))
                {
                    pos++;
                }

                identifiers.Add(input[start..pos].ToLowerInvariant());
            }
            else if (input[pos] == '.')
            {
                pos++;
                continue;
            }
            else
            {
                if (strict)
                {
                    throw new InvalidOperationException(
                        $"parse_ident(): unexpected character '{input[pos]}' at position {pos + 1}.");
                }

                break;
            }
        }

        DataValue[] elements = new DataValue[identifiers.Count];
        for (int i = 0; i < identifiers.Count; i++)
        {
            elements[i] = DataValue.FromString(identifiers[i]);
        }

        return DataValue.FromArray(DataKind.String, elements);
    }
}
