using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Assertion;

/// <summary>
/// Returns the string input verbatim when it contains a match for the
/// supplied regex; throws otherwise. Null in either operand passes through
/// unchecked. Compiled patterns are cached per process — repeated calls
/// with a literal pattern (the common case) avoid recompilation.
/// </summary>
public sealed class AssertMatchesFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_matches";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the string input when it matches the supplied regex; throws otherwise. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertMatchesFunction>(argumentKinds);

    // Pattern cache — bounded by the set of literal patterns the workload uses.
    // Concurrent because scalar functions run across worker threads in batched
    // execution.
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef pattern = args[1];
        if (value.IsNull || pattern.IsNull) return new ValueTask<ValueRef>(value);

        string patternText = pattern.AsString();
        Regex regex = RegexCache.GetOrAdd(
            patternText,
            static p => new Regex(p, RegexOptions.Compiled | RegexOptions.CultureInvariant));

        string text = value.AsString();
        if (regex.IsMatch(text)) return new ValueTask<ValueRef>(value);

        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 2),
            $"value {AssertHelpers.Display(value)} did not match pattern '{patternText}'");
        return default;
    }

}
