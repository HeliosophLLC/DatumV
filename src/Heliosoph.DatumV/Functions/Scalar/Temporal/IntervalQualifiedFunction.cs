using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// Backs the SQL <c>INTERVAL '...' &lt;qualifier&gt;</c> form. The qualifier
/// (<c>YEAR TO MONTH</c>, <c>DAY TO SECOND</c>, etc.) is lowered to a string
/// argument by the parser; the function dispatches into
/// <see cref="Interval.TryParseWithQualifier"/>. Direct user calls are
/// uncommon but permitted; the bare-number disambiguation makes
/// <c>interval_qualified('1', 'HOUR')</c> handy for programmatic interval
/// construction.
/// </summary>
public sealed class IntervalQualifiedFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "interval_qualified";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Parses an interval literal with a PG-style qualifier (e.g. 'YEAR TO MONTH').";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("literal",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("qualifier", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Interval)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<IntervalQualifiedFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Interval));
        }

        string literal = args[0].AsString();
        string qualifierText = args[1].AsString();
        Interval.Qualifier qualifier = ParseQualifier(qualifierText);
        if (!Interval.TryParseWithQualifier(literal, qualifier, out Interval iv))
        {
            throw new ExecutionException(
                $"Invalid interval literal '{literal}' for qualifier '{qualifierText}'.");
        }
        return new ValueTask<ValueRef>(ValueRef.FromInterval(iv));
    }

    /// <summary>
    /// Maps a textual qualifier name (case-insensitive; underscore-separated
    /// or PascalCase) to the <see cref="Interval.Qualifier"/> enum.
    /// </summary>
    internal static Interval.Qualifier ParseQualifier(string text)
    {
        string normalised = text.Replace(" ", string.Empty)
            .Replace("_", string.Empty);
        return normalised.ToUpperInvariant() switch
        {
            "YEAR" => Interval.Qualifier.Year,
            "MONTH" => Interval.Qualifier.Month,
            "DAY" => Interval.Qualifier.Day,
            "HOUR" => Interval.Qualifier.Hour,
            "MINUTE" => Interval.Qualifier.Minute,
            "SECOND" => Interval.Qualifier.Second,
            "YEARTOMONTH" => Interval.Qualifier.YearToMonth,
            "DAYTOHOUR" => Interval.Qualifier.DayToHour,
            "DAYTOMINUTE" => Interval.Qualifier.DayToMinute,
            "DAYTOSECOND" => Interval.Qualifier.DayToSecond,
            "HOURTOMINUTE" => Interval.Qualifier.HourToMinute,
            "HOURTOSECOND" => Interval.Qualifier.HourToSecond,
            "MINUTETOSECOND" => Interval.Qualifier.MinuteToSecond,
            _ => throw new ExecutionException(
                $"Unknown interval qualifier '{text}'. " +
                "Expected one of YEAR, MONTH, DAY, HOUR, MINUTE, SECOND, " +
                "YEAR TO MONTH, DAY TO HOUR, DAY TO MINUTE, DAY TO SECOND, " +
                "HOUR TO MINUTE, HOUR TO SECOND, MINUTE TO SECOND."),
        };
    }

    /// <inheritdoc />
    public bool IsPure => true;
}
