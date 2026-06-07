using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// Oracle-style <c>regexp_instr(string, pattern [, start [, N [, endoption [, flags [, subexpr]]]]]) → integer</c>.
/// Returns the 1-based character position of the Nth match of
/// <c>pattern</c> in <c>value</c>, or 0 if no such match exists.
/// When <c>endoption</c> is <c>1</c>, returns the position immediately
/// after the match (the first character not in the match) instead of the
/// match's start. <c>subexpr</c> selects a specific capture group. Null in
/// any argument propagates to null.
/// </summary>
public sealed class RegexpInstrFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "regexp_instr";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the 1-based position of the Nth POSIX regex match (0 if no match); endoption=1 returns end+1.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        BuildVariant(2),
        BuildVariant(3),
        BuildVariant(4),
        BuildVariant(5),
        BuildVariant(6),
        BuildVariant(7),
    ];

    private static FunctionSignatureVariant BuildVariant(int arity)
    {
        ParameterSpec[] parameters = arity switch
        {
            2 =>
            [
                new ParameterSpec("value",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern",   DataKindMatcher.Exact(DataKind.String)),
            ],
            3 =>
            [
                new ParameterSpec("value",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",     DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            4 =>
            [
                new ParameterSpec("value",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",     DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("n",         DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            5 =>
            [
                new ParameterSpec("value",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",     DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("n",         DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("endoption", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            6 =>
            [
                new ParameterSpec("value",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",     DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("n",         DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("endoption", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("flags",     DataKindMatcher.Exact(DataKind.String)),
            ],
            _ =>
            [
                new ParameterSpec("value",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("pattern",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",     DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("n",         DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("endoption", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("flags",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("subexpr",   DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
        };
        return new FunctionSignatureVariant(
            Parameters: parameters,
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32));
    }

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RegexpInstrFunction>(argumentKinds);

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
        int nth = 1;
        int endoption = 0;
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
            if (!args[4].TryToInt32(out endoption)) throw new FunctionArgumentException(Name, $"argument 'endoption' of kind {args[4].Kind} is out of range for Int32.");
            if (endoption != 0 && endoption != 1) throw new FunctionArgumentException(Name, "endoption must be 0 or 1.");
        }
        if (args.Length >= 6)
        {
            flagsText = args[5].AsString();
        }
        if (args.Length == 7)
        {
            if (!args[6].TryToInt32(out subexpr)) throw new FunctionArgumentException(Name, $"argument 'subexpr' of kind {args[6].Kind} is out of range for Int32.");
            if (subexpr < 0) throw new FunctionArgumentException(Name, "subexpr must be non-negative.");
        }

        RegexOptions options = RegexpFlags.Parse(flagsText, Name, out _);
        Regex regex = RegexCache.GetOrAdd(
            (pattern, options),
            static key => new Regex(key.Pattern, key.Options | RegexOptions.Compiled));

        MatchCollection matches = regex.Matches(source, startIndex);
        if (nth > matches.Count)
        {
            return new ValueTask<ValueRef>(ValueRef.FromInt32(0));
        }
        Match match = matches[nth - 1];
        Group group = subexpr == 0 ? match : match.Groups[subexpr];
        if (!group.Success)
        {
            return new ValueTask<ValueRef>(ValueRef.FromInt32(0));
        }

        int oneBasedPosition = group.Index + 1;
        if (endoption == 1)
        {
            oneBasedPosition = group.Index + group.Length + 1;
        }
        return new ValueTask<ValueRef>(ValueRef.FromInt32(oneBasedPosition));
    }
}
