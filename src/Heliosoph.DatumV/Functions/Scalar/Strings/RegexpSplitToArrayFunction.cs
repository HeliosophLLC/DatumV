using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>regexp_split_to_array(string, pattern [, flags]) → text[]</c>.
/// Splits <c>value</c> on every match of the POSIX regular expression
/// <c>pattern</c> and returns the parts as an Array of strings. Null in any
/// argument propagates to a null array.
/// </summary>
public sealed class RegexpSplitToArrayFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "regexp_split_to_array";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Splits the string on each POSIX regex match and returns the parts as an Array.";

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
        FunctionMetadata.Validate<RegexpSplitToArrayFunction>(argumentKinds);

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

        string[] parts = regex.Split(source);
        ValueRef[] elements = new ValueRef[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            elements[i] = ValueRef.FromString(parts[i]);
        }
        return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.String, elements));
    }
}
