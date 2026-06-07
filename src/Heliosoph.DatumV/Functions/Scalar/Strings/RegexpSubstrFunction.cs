using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL/Oracle <c>regexp_substr(string, pattern [, start [, N [, flags [, subexpr]]]]) → text</c>.
/// Returns the N-th match of <c>pattern</c> in <c>value</c> (default
/// N=1, 1-based) starting at 1-based <c>start</c>. <c>subexpr</c>
/// selects a capture group (1-based). Returns null when there is no match,
/// when <c>N</c> exceeds the number of matches, or when <c>subexpr</c>
/// references a missing group. Null in any argument propagates to null.
/// </summary>
public sealed class RegexpSubstrFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "regexp_substr";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the N-th POSIX regex match (or a specific capture group) from value.";

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
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),

        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",   DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),

        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",   DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("n",       DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),

        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",   DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("n",       DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("flags",   DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),

        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",   DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("n",       DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("flags",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("subexpr", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RegexpSubstrFunction>(argumentKinds);

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
        int startIndex = 0;
        int nth = 1;
        string flagsText = "";
        int subexpr = 0;

        if (args.Length >= 3)
        {
            if (!args[2].TryToInt32(out int s)) throw new FunctionArgumentException(Name, $"argument 'start' of kind {args[2].Kind} is out of range for Int32.");
            startIndex = System.Math.Clamp(s - 1, 0, source.Length);
        }
        if (args.Length >= 4)
        {
            if (!args[3].TryToInt32(out nth)) throw new FunctionArgumentException(Name, $"argument 'n' of kind {args[3].Kind} is out of range for Int32.");
            if (nth < 1) throw new FunctionArgumentException(Name, "N must be at least 1.");
        }
        if (args.Length >= 5)
        {
            flagsText = args[4].AsString();
        }
        if (args.Length == 6)
        {
            if (!args[5].TryToInt32(out subexpr)) throw new FunctionArgumentException(Name, $"argument 'subexpr' of kind {args[5].Kind} is out of range for Int32.");
            if (subexpr < 0) throw new FunctionArgumentException(Name, "subexpr must be non-negative.");
        }

        RegexOptions options = RegexpFlags.Parse(flagsText, Name, out _);
        Regex regex = RegexCache.GetOrAdd(
            (pattern, options),
            static key => new Regex(key.Pattern, key.Options | RegexOptions.Compiled));

        MatchCollection matches = regex.Matches(source, startIndex);
        if (nth > matches.Count)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        Match match = matches[nth - 1];
        Group group = subexpr == 0 ? match : match.Groups[subexpr];
        if (!group.Success)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(group.Value));
    }
}
