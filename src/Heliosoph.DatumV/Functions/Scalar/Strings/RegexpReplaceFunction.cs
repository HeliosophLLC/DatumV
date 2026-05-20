using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>regexp_replace</c>. Two overload shapes:
/// <c>regexp_replace(string, pattern, replacement [, flags])</c> and
/// <c>regexp_replace(string, pattern, replacement, start [, N [, flags]])</c>.
/// Substitutes the first match of <c>pattern</c> in <c>string</c> with
/// <c>replacement</c>, or all matches when the <c>g</c> flag is supplied.
/// When <c>start</c> is given (1-based character position) the characters
/// before it pass through unchanged and matching begins from <c>start</c>;
/// when <c>N</c> (≥ 1) is given, only the Nth match at-or-after <c>start</c>
/// is replaced (and the <c>g</c> flag is ignored). Null in any argument
/// propagates to null output.
/// </summary>
public sealed class RegexpReplaceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "regexp_replace";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Replaces substring(s) matching a POSIX regular expression. " +
        "Supports the `g` (global), `i` (case-insensitive), `c` (case-sensitive), " +
        "`n`/`m` (newline-sensitive), `s` (single-line), and `x` (extended) flags.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        // (string, pattern, replacement)
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",       DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("replacement", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),

        // (string, pattern, replacement, flags)
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",       DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("replacement", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("flags",       DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),

        // (string, pattern, replacement, start)
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",       DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("replacement", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",       DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),

        // (string, pattern, replacement, start, N)
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",       DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("replacement", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",       DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("n",           DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),

        // (string, pattern, replacement, start, flags)
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",       DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("replacement", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",       DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("flags",       DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),

        // (string, pattern, replacement, start, N, flags)
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",       DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("replacement", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",       DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("n",           DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("flags",       DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RegexpReplaceFunction>(argumentKinds);

    // Pattern cache keyed on (pattern, options) — the same literal pattern
    // may legitimately appear with different flag-derived options.
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex> RegexCache = new();

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }

        string source = args[0].AsString();
        string pattern = args[1].AsString();
        string replacement = args[2].AsString();

        int? start = null;
        int? n = null;
        string flagsText = "";

        switch (args.Length)
        {
            case 3:
                break;
            case 4:
                if (args[3].Kind == DataKind.String)
                {
                    flagsText = args[3].AsString();
                }
                else
                {
                    start = ReadInt32(args[3]);
                }
                break;
            case 5:
                start = ReadInt32(args[3]);
                if (args[4].Kind == DataKind.String)
                {
                    flagsText = args[4].AsString();
                }
                else
                {
                    n = ReadInt32(args[4]);
                }
                break;
            case 6:
                start = ReadInt32(args[3]);
                n = ReadInt32(args[4]);
                flagsText = args[5].AsString();
                break;
        }

        (RegexOptions options, bool global) = ParseFlags(flagsText);
        Regex regex = RegexCache.GetOrAdd(
            (pattern, options),
            static key => new Regex(key.Pattern, key.Options | RegexOptions.Compiled));

        string netReplacement = TranslateReplacement(replacement);

        int startIndex = 0;
        if (start is int s)
        {
            // PG is 1-based; values ≤ 1 mean "from the beginning". Clamp at length.
            startIndex = System.Math.Clamp(s - 1, 0, source.Length);
        }
        ReadOnlySpan<char> prefix = source.AsSpan(0, startIndex);
        string body = source[startIndex..];

        string replaced;
        if (n is int nv && nv >= 1)
        {
            // Replace exactly the Nth match (1-based). g is ignored when N is set.
            MatchCollection matches = regex.Matches(body);
            if (nv > matches.Count)
            {
                replaced = body;
            }
            else
            {
                Match m = matches[nv - 1];
                StringBuilder sb = new(body.Length);
                sb.Append(body, 0, m.Index);
                sb.Append(m.Result(netReplacement));
                sb.Append(body, m.Index + m.Length, body.Length - m.Index - m.Length);
                replaced = sb.ToString();
            }
        }
        else
        {
            int count = global ? -1 : 1;
            replaced = regex.Replace(body, netReplacement, count);
        }

        string result = prefix.Length == 0 ? replaced : string.Concat(prefix, replaced);
        return new ValueTask<ValueRef>(ValueRef.FromString(result));
    }

    private static int ReadInt32(ValueRef value)
    {
        if (!value.TryToInt32(out int v))
        {
            throw new FunctionArgumentException(Name, $"argument of kind {value.Kind} is out of range for Int32.");
        }
        return v;
    }

    private static (RegexOptions Options, bool Global) ParseFlags(string flags)
    {
        // PG default: newline-insensitive matching — `.` matches any character
        // including \n, and ^/$ anchor only at string boundaries. .NET's
        // RegexOptions.Singleline gives the first half; Multiline OFF gives
        // the second.
        RegexOptions options = RegexOptions.Singleline | RegexOptions.CultureInvariant;
        bool global = false;
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
                    throw new FunctionArgumentException(Name, $"invalid regexp flag '{c}'.");
            }
        }
        return (options, global);
    }

    // Translates a PG-style replacement (\1..\9, \&, \\) to the .NET form
    // ($1..$9, $0, \\). Literal $ in the input must be escaped as $$.
    private static string TranslateReplacement(string pg)
    {
        StringBuilder sb = new(pg.Length);
        for (int i = 0; i < pg.Length; i++)
        {
            char c = pg[i];
            if (c == '\\' && i + 1 < pg.Length)
            {
                char next = pg[i + 1];
                if (next >= '1' && next <= '9')
                {
                    sb.Append('$').Append(next);
                    i++;
                }
                else if (next == '&')
                {
                    sb.Append("$0");
                    i++;
                }
                else if (next == '\\')
                {
                    sb.Append('\\');
                    i++;
                }
                else
                {
                    sb.Append(next);
                    i++;
                }
            }
            else if (c == '$')
            {
                sb.Append("$$");
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
