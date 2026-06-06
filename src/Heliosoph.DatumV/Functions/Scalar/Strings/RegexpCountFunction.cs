using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>regexp_count(string, pattern [, start [, flags]]) → integer</c>.
/// Returns the number of times <c>pattern</c> matches in <c>value</c>,
/// optionally starting from 1-based <c>start</c>. Null in any argument
/// propagates to null.
/// </summary>
public sealed class RegexpCountFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "regexp_count";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the number of POSIX regex matches in value (optionally starting at 1-based start).";

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
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),

        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",   DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),

        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",   DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("flags",   DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RegexpCountFunction>(argumentKinds);

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
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }

        string source = args[0].AsString();
        string pattern = args[1].AsString();

        int startIndex = 0;
        if (args.Length >= 3)
        {
            if (!args[2].TryToInt32(out int startArg))
            {
                throw new FunctionArgumentException(Name, $"argument 'start' of kind {args[2].Kind} is out of range for Int32.");
            }
            startIndex = System.Math.Clamp(startArg - 1, 0, source.Length);
        }

        string flagsText = args.Length == 4 ? args[3].AsString() : "";
        RegexOptions options = RegexpFlags.Parse(flagsText, Name, out _);
        Regex regex = RegexCache.GetOrAdd(
            (pattern, options),
            static key => new Regex(key.Pattern, key.Options | RegexOptions.Compiled));

        int count = regex.Matches(source, startIndex).Count;
        return new ValueTask<ValueRef>(ValueRef.FromInt32(count));
    }
}
