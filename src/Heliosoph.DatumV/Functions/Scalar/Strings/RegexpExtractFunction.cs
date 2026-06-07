using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// <c>regexp_extract(string, pattern [, group]) → text</c>. Returns the
/// first match of <c>pattern</c> in <c>value</c>, or the specified 1-based
/// capture <c>group</c> from that match. Returns null when there is no
/// match or when the requested group did not participate. Null in any
/// argument propagates to null.
/// </summary>
/// <remarks>
/// BigQuery/Spark-flavoured convenience function; PostgreSQL ships
/// <see cref="RegexpMatchFunction"/> (returns an Array of all groups) and
/// <see cref="RegexpSubstrFunction"/> (returns the Nth match) for the same
/// underlying task.
/// </remarks>
public sealed class RegexpExtractFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "regexp_extract";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the first POSIX regex match (or a specific 1-based capture group) from value.";

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
                new ParameterSpec("group",   DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RegexpExtractFunction>(argumentKinds);

    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

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
        int group = 0;
        if (args.Length == 3)
        {
            if (!args[2].TryToInt32(out group))
            {
                throw new FunctionArgumentException(Name, $"argument 'group' of kind {args[2].Kind} is out of range for Int32.");
            }
            if (group < 0)
            {
                throw new FunctionArgumentException(Name, "group must be non-negative.");
            }
        }

        Regex regex = RegexCache.GetOrAdd(
            pattern,
            static p => new Regex(p, RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled));

        Match match = regex.Match(source);
        if (!match.Success)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        Group g = match.Groups[group];
        if (!g.Success)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(g.Value));
    }
}
