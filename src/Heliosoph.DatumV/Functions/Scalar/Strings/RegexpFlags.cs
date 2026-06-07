using System.Text.RegularExpressions;
using Heliosoph.DatumV.Functions;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// Shared PostgreSQL-flavoured regex flag parsing used by the
/// <c>regexp_*</c> functions. PostgreSQL default semantics map onto
/// <see cref="RegexOptions.Singleline"/> + <see cref="RegexOptions.CultureInvariant"/>:
/// <c>.</c> matches any character including <c>\n</c>, and <c>^</c>/<c>$</c>
/// anchor only at string boundaries.
/// </summary>
internal static class RegexpFlags
{
    public static RegexOptions Parse(string flags, string functionName, out bool global)
    {
        RegexOptions options = RegexOptions.Singleline | RegexOptions.CultureInvariant;
        global = false;
        for (int i = 0; i < flags.Length; i++)
        {
            char c = flags[i];
            switch (c)
            {
                case 'g': global = true; break;
                case 'i': options |= RegexOptions.IgnoreCase; break;
                case 'c': options &= ~RegexOptions.IgnoreCase; break;
                case 'n':
                case 'm':
                    options |= RegexOptions.Multiline;
                    options &= ~RegexOptions.Singleline;
                    break;
                case 's':
                    options &= ~RegexOptions.Multiline;
                    options |= RegexOptions.Singleline;
                    break;
                case 'x': options |= RegexOptions.IgnorePatternWhitespace; break;
                // PG-specific flags (p/q/t/w) have no clean .NET mapping; silently accept.
                case 'p':
                case 'q':
                case 't':
                case 'w':
                    break;
                default:
                    throw new FunctionArgumentException(functionName, $"invalid regexp flag '{c}'.");
            }
        }
        return options;
    }
}
