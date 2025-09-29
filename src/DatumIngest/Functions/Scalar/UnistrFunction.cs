using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Evaluates Unicode escape sequences in a string.
/// <c>unistr(text)</c> — PostgreSQL compatible.
/// Supports: <c>\XXXX</c> (4-hex), <c>\+XXXXXX</c> (6-hex),
/// <c>\uXXXX</c> (4-hex), <c>\UXXXXXXXX</c> (8-hex). <c>\\</c> produces a literal backslash.
/// </summary>
public sealed class UnistrFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "unistr";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("unistr() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"unistr() requires a String argument, got {argumentKinds[0]}.");
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
        StringBuilder sb = new(input.Length);

        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] != '\\')
            {
                sb.Append(input[i]);
                continue;
            }

            i++;
            if (i >= input.Length) break;

            switch (input[i])
            {
                case '\\':
                    sb.Append('\\');
                    break;

                case '+' when i + 6 < input.Length:
                {
                    // \+XXXXXX (6 hex digits)
                    string hex = input.Substring(i + 1, 6);
                    int codePoint = Convert.ToInt32(hex, 16);
                    sb.Append(char.ConvertFromUtf32(codePoint));
                    i += 6;
                    break;
                }

                case 'U' when i + 8 < input.Length:
                {
                    // \UXXXXXXXX (8 hex digits)
                    string hex = input.Substring(i + 1, 8);
                    int codePoint = Convert.ToInt32(hex, 16);
                    sb.Append(char.ConvertFromUtf32(codePoint));
                    i += 8;
                    break;
                }

                case 'u' when i + 4 < input.Length:
                {
                    // \uXXXX (4 hex digits)
                    string hex = input.Substring(i + 1, 4);
                    int codePoint = Convert.ToInt32(hex, 16);
                    sb.Append(char.ConvertFromUtf32(codePoint));
                    i += 4;
                    break;
                }

                default:
                {
                    // \XXXX (4 hex digits — PostgreSQL default form)
                    if (i + 3 < input.Length && IsHexSequence(input, i, 4))
                    {
                        string hex = input.Substring(i, 4);
                        int codePoint = Convert.ToInt32(hex, 16);
                        sb.Append(char.ConvertFromUtf32(codePoint));
                        i += 3;
                    }
                    else
                    {
                        // Not a recognized escape — keep literal
                        sb.Append('\\');
                        sb.Append(input[i]);
                    }
                    break;
                }
            }
        }

        return DataValue.FromString(sb.ToString());
    }

    private static bool IsHexSequence(string s, int start, int length)
    {
        if (start + length > s.Length) return false;
        for (int i = start; i < start + length; i++)
        {
            if (!char.IsAsciiHexDigit(s[i])) return false;
        }
        return true;
    }
}
