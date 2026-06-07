using System.Text;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// Shared formatters used by <see cref="QuoteIdentFunction"/>,
/// <see cref="QuoteLiteralFunction"/>, <see cref="QuoteNullableFunction"/>,
/// and <see cref="FormatFunction"/>.
/// </summary>
internal static class SqlQuoting
{
    /// <summary>
    /// Returns <paramref name="value"/> as a SQL identifier, double-quoting
    /// when the bare form would be unsafe — anything other than
    /// <c>[a-z_][a-z0-9_]*</c> or anything that collides with a reserved
    /// keyword would re-parse incorrectly, so we conservatively quote.
    /// </summary>
    public static string QuoteIdentifier(string value)
    {
        if (NeedsIdentifierQuotes(value))
        {
            StringBuilder sb = new(value.Length + 2);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"') sb.Append('"');
                sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
        return value;
    }

    /// <summary>
    /// Returns <paramref name="value"/> as a SQL string literal — wrapped
    /// in single quotes with internal single quotes doubled. If the value
    /// contains a backslash, the result uses the PG <c>E'…'</c> prefix and
    /// backslash-escapes both <c>\</c> and <c>'</c>.
    /// </summary>
    public static string QuoteLiteral(string value)
    {
        bool hasBackslash = value.Contains('\\');
        StringBuilder sb = new(value.Length + 2);
        if (hasBackslash)
        {
            sb.Append('E').Append('\'');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\'' || c == '\\') sb.Append('\\');
                sb.Append(c);
            }
            sb.Append('\'');
        }
        else
        {
            sb.Append('\'');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\'') sb.Append('\'');
                sb.Append(c);
            }
            sb.Append('\'');
        }
        return sb.ToString();
    }

    private static bool NeedsIdentifierQuotes(string value)
    {
        if (value.Length == 0) return true;
        char first = value[0];
        if (!(first == '_' || (first >= 'a' && first <= 'z'))) return true;
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            bool ok = c == '_' || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (!ok) return true;
        }
        return false;
    }
}
