using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>regexp_match(string, pattern [, flags]) → text[]</c>.
/// Returns the captured substrings from the first match of <c>pattern</c> in
/// <c>value</c>. When the pattern has no parenthesised capture groups the
/// result is a single-element array holding the entire match. Returns null
/// when there is no match. Null in any argument propagates to null.
/// </summary>
public sealed class RegexpMatchFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "regexp_match";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns an Array of the capture groups from the first POSIX regex match (or [whole match] when the pattern has no groups).";

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
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.String))),

        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("flags",   DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.String))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RegexpMatchFunction>(argumentKinds);

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
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.String));
        }

        string source = args[0].AsString();
        string pattern = args[1].AsString();
        string flagsText = args.Length == 3 ? args[2].AsString() : "";

        RegexOptions options = RegexpFlags.Parse(flagsText, Name, out _);
        Regex regex = RegexCache.GetOrAdd(
            (pattern, options),
            static key => new Regex(key.Pattern, key.Options | RegexOptions.Compiled));

        Match match = regex.Match(source);
        if (!match.Success)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.String));
        }

        // PG returns text[] of capture groups; if the pattern has no
        // parenthesised groups, the result is a 1-element array of the whole
        // match. Groups[0] is the whole match — skip it when there are
        // numbered groups.
        int groupCount = match.Groups.Count;
        ValueRef[] elements;
        if (groupCount <= 1)
        {
            elements = [ValueRef.FromString(match.Value)];
        }
        else
        {
            elements = new ValueRef[groupCount - 1];
            for (int i = 1; i < groupCount; i++)
            {
                Group g = match.Groups[i];
                elements[i - 1] = g.Success
                    ? ValueRef.FromString(g.Value)
                    : ValueRef.Null(DataKind.String);
            }
        }
        return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.String, elements));
    }
}
