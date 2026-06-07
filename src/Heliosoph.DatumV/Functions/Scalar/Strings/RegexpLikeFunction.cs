using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>regexp_like(string, pattern [, flags])</c>. Returns
/// <c>true</c> when <c>pattern</c> matches anywhere in <c>string</c>.
/// Supports the <c>i</c> (case-insensitive), <c>c</c> (case-sensitive),
/// <c>n</c>/<c>m</c> (newline-sensitive), <c>s</c> (single-line) and
/// <c>x</c> (extended) flags. Null in any argument propagates to null.
/// </summary>
public sealed class RegexpLikeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "regexp_like";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when the POSIX regular expression matches anywhere in the input. " +
        "Supports the `i` (case-insensitive), `c` (case-sensitive), `n`/`m` (newline-sensitive), " +
        "`s` (single-line) and `x` (extended) flags.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),

        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("flags",   DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RegexpLikeFunction>(argumentKinds);

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
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Boolean));
        }

        string source = args[0].AsString();
        string pattern = args[1].AsString();
        string flagsText = args.Length == 3 ? args[2].AsString() : "";

        RegexOptions options = ParseFlags(flagsText);
        Regex regex = RegexCache.GetOrAdd(
            (pattern, options),
            static key => new Regex(key.Pattern, key.Options | RegexOptions.Compiled));

        return new ValueTask<ValueRef>(ValueRef.FromBoolean(regex.IsMatch(source)));
    }

    private static RegexOptions ParseFlags(string flags)
    {
        // PG default: `.` matches \n, ^/$ anchor only at string boundaries.
        RegexOptions options = RegexOptions.Singleline | RegexOptions.CultureInvariant;
        for (int i = 0; i < flags.Length; i++)
        {
            char c = flags[i];
            switch (c)
            {
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
                case 'p':
                case 'q':
                case 't':
                case 'w':
                    break;
                default:
                    throw new FunctionArgumentException(Name, $"invalid regexp flag '{c}'.");
            }
        }
        return options;
    }
}
