using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// PG <c>make_interval(years, months, weeks, days, hours, mins, secs)</c> —
/// constructs an <see cref="DataKind.Interval"/> from named integer / fractional
/// components. All parameters default to 0; <c>secs</c> accepts a fractional
/// value down to microsecond precision.
/// </summary>
public sealed class MakeIntervalFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "make_interval";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Builds an interval from year/month/week/day/hour/minute/second components.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        // All parameters optional — callers may supply any prefix of the
        // (years, months, weeks, days, hours, mins, secs) sequence, with
        // omitted trailing arguments defaulting to 0. The 0-arg call
        // returns the zero interval.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("years",  DataKindMatcher.Family(DataKindFamily.NumericScalar), IsOptional: true),
                new ParameterSpec("months", DataKindMatcher.Family(DataKindFamily.NumericScalar), IsOptional: true),
                new ParameterSpec("weeks",  DataKindMatcher.Family(DataKindFamily.NumericScalar), IsOptional: true),
                new ParameterSpec("days",   DataKindMatcher.Family(DataKindFamily.NumericScalar), IsOptional: true),
                new ParameterSpec("hours",  DataKindMatcher.Family(DataKindFamily.NumericScalar), IsOptional: true),
                new ParameterSpec("mins",   DataKindMatcher.Family(DataKindFamily.NumericScalar), IsOptional: true),
                new ParameterSpec("secs",   DataKindMatcher.Family(DataKindFamily.NumericScalar), IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Interval)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MakeIntervalFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (AnyNull(args))
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Interval));
        }

        long years  = args.Length > 0 ? ToInt64(args[0]) : 0L;
        long months = args.Length > 1 ? ToInt64(args[1]) : 0L;
        long weeks  = args.Length > 2 ? ToInt64(args[2]) : 0L;
        long days   = args.Length > 3 ? ToInt64(args[3]) : 0L;
        long hours  = args.Length > 4 ? ToInt64(args[4]) : 0L;
        long mins   = args.Length > 5 ? ToInt64(args[5]) : 0L;
        double secs = args.Length > 6 ? ToDouble(args[6]) : 0.0;

        long totalMonths = checked(years * Interval.MonthsPerYear + months);
        long totalDays   = checked(weeks * 7 + days);
        long microsFromHm = checked(hours * Interval.MicrosPerHour + mins * Interval.MicrosPerMinute);
        long microsFromSecs = (long)System.Math.Round(secs * Interval.MicrosPerSecond, MidpointRounding.AwayFromZero);
        long totalMicros = checked(microsFromHm + microsFromSecs);

        return new ValueTask<ValueRef>(ValueRef.FromInterval(new Interval(
            checked((int)totalMonths),
            checked((int)totalDays),
            totalMicros)));
    }

    private static bool AnyNull(ReadOnlySpan<ValueRef> args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return true;
        }
        return false;
    }

    private static long ToInt64(ValueRef v) =>
        v.TryToInt64(out long i) ? i : 0;

    private static double ToDouble(ValueRef v) =>
        v.TryToDouble(out double d) ? d : 0.0;

    /// <inheritdoc />
    public bool IsPure => true;
}
