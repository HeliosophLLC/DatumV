using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// PG <c>date_part(field, source)</c> — extracts a named field from a temporal
/// value as a <see cref="DataKind.Float64"/>. Backs the <c>EXTRACT(field FROM
/// source)</c> SQL form, which the parser desugars to a call into this function.
/// Accepts every temporal kind: <c>Date</c>, <c>Time</c>, <c>Timestamp</c>,
/// <c>TimestampTz</c>, <c>Duration</c>, <c>Interval</c>.
/// </summary>
/// <remarks>
/// <para>
/// Field names are matched case-insensitively and follow the PG canonical
/// set: <c>year</c>, <c>isoyear</c>, <c>month</c>, <c>day</c>, <c>hour</c>,
/// <c>minute</c>, <c>second</c>, <c>millisecond</c>, <c>microsecond</c>,
/// <c>epoch</c>, <c>dow</c>, <c>isodow</c>, <c>doy</c>, <c>quarter</c>,
/// <c>week</c>, <c>decade</c>, <c>century</c>, <c>millennium</c>,
/// <c>julian</c>. DatumV extensions: <c>day_of_week</c> (= <c>dow</c>),
/// <c>day_of_year</c> (= <c>doy</c>), <c>week_of_year</c> (= <c>week</c>),
/// <c>is_weekend</c> (1 for Saturday/Sunday, 0 otherwise).
/// </para>
/// <para>
/// For <c>Interval</c>, the semantics follow PG: <c>year</c> = <c>months / 12</c>,
/// <c>month</c> = <c>months % 12</c>, <c>day</c> = <c>days</c>, time fields are
/// read out of the microsecond component. <c>epoch</c> uses canonical
/// 30-day months and 24-hour days for the second total.
/// </para>
/// </remarks>
public sealed class DatePartFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "date_part";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Extracts a named field (year, month, hour, epoch, …) from a temporal value as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("field",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("source", DataKindMatcher.Family(DataKindFamily.Temporal)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DatePartFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));
        }

        string field = args[0].AsString().ToLowerInvariant();
        ValueRef src = args[1];
        double result = src.Kind switch
        {
            DataKind.Date => FromDate(field, src.AsDate()),
            DataKind.Time => FromTime(field, src.AsTime()),
            DataKind.Timestamp => FromDateTime(field, src.AsTimestamp()),
            DataKind.TimestampTz => FromDateTime(field, src.AsTimestampTz().UtcDateTime),
            DataKind.Duration => FromDuration(field, src.AsDuration()),
            DataKind.Interval => FromInterval(field, src.AsInterval()),
            _ => throw new InvalidOperationException(
                $"date_part: unsupported source kind {src.Kind}."),
        };
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(result));
    }

    private static double FromDate(string field, DateOnly d) => field switch
    {
        "year"       => d.Year,
        "isoyear"    => System.Globalization.ISOWeek.GetYear(d.ToDateTime(TimeOnly.MinValue)),
        "month"      => d.Month,
        "day"        => d.Day,
        "quarter"    => (d.Month + 2) / 3,
        "dow" or "day_of_week"       => (int)d.DayOfWeek,
        "isodow"                     => ((int)d.DayOfWeek + 6) % 7 + 1,
        "doy" or "day_of_year"       => d.DayOfYear,
        "week" or "week_of_year"     => System.Globalization.ISOWeek.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue)),
        "is_weekend"                 => IsWeekend(d.DayOfWeek),
        "decade"     => d.Year / 10,
        "century"    => (d.Year - 1) / 100 + 1,
        "millennium" => (d.Year - 1) / 1000 + 1,
        "epoch"      => (d.ToDateTime(TimeOnly.MinValue) - DateTime.UnixEpoch).TotalSeconds,
        "julian"     => JulianDay(d.Year, d.Month, d.Day),
        _ => 0.0,
    };

    private static double FromTime(string field, TimeOnly t) => field switch
    {
        "hour"        => t.Hour,
        "minute"      => t.Minute,
        "second"      => t.Second + t.Millisecond / 1000.0 + (t.Ticks % 10_000_000L) / 10_000_000.0,
        "millisecond" => t.Second * 1000.0 + t.Millisecond + (t.Ticks % 10_000) / 10_000.0,
        "microsecond" => t.Second * 1_000_000.0 + (t.Ticks % 10_000_000L) / 10.0,
        "epoch"       => t.ToTimeSpan().TotalSeconds,
        _ => 0.0,
    };

    private static double FromDateTime(string field, DateTime dt) => field switch
    {
        "year"        => dt.Year,
        "isoyear"     => System.Globalization.ISOWeek.GetYear(dt),
        "month"       => dt.Month,
        "day"         => dt.Day,
        "hour"        => dt.Hour,
        "minute"      => dt.Minute,
        "second"      => dt.Second + dt.Millisecond / 1000.0 + (dt.Ticks % 10_000_000L) / 10_000_000.0,
        "millisecond" => dt.Second * 1000.0 + dt.Millisecond + (dt.Ticks % 10_000) / 10_000.0,
        "microsecond" => dt.Second * 1_000_000.0 + (dt.Ticks % 10_000_000L) / 10.0,
        "quarter"     => (dt.Month + 2) / 3,
        "dow" or "day_of_week"       => (int)dt.DayOfWeek,
        "isodow"                     => ((int)dt.DayOfWeek + 6) % 7 + 1,
        "doy" or "day_of_year"       => dt.DayOfYear,
        "week" or "week_of_year"     => System.Globalization.ISOWeek.GetWeekOfYear(dt),
        "is_weekend"                 => IsWeekend(dt.DayOfWeek),
        "decade"      => dt.Year / 10,
        "century"     => (dt.Year - 1) / 100 + 1,
        "millennium"  => (dt.Year - 1) / 1000 + 1,
        "epoch"       => (dt - DateTime.UnixEpoch).TotalSeconds,
        "julian"      => JulianDay(dt.Year, dt.Month, dt.Day)
                         + (dt.TimeOfDay.TotalSeconds / 86400.0),
        // Naive timestamps have no zone info — PG returns 0 for the
        // timezone family; mirror that. TimestampTz routes here through
        // its UTC-DateTime view, so timezone fields collapse to 0
        // (UTC offset is zero); a real session-TZ surface lands in a
        // follow-up.
        "timezone" or "timezone_hour" or "timezone_minute" => 0.0,
        _ => 0.0,
    };

    /// <summary>
    /// DatumV extension: returns 1.0 on Saturday or Sunday, 0.0 otherwise.
    /// No PG equivalent; documented in <c>docs/functions/temporal.md</c>.
    /// </summary>
    private static double IsWeekend(DayOfWeek dow) =>
        dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday ? 1.0 : 0.0;

    /// <summary>
    /// Computes the Julian Day Number for a Gregorian date using Fliegel
    /// &amp; Van Flandern's integer-arithmetic recipe (correct for all
    /// dates ≥ 1 March −4900).
    /// </summary>
    private static double JulianDay(int year, int month, int day)
    {
        int a = (14 - month) / 12;
        int y = year + 4800 - a;
        int m = month + 12 * a - 3;
        return day + (153 * m + 2) / 5 + 365L * y + y / 4 - y / 100 + y / 400 - 32045;
    }

    private static double FromDuration(string field, TimeSpan ts) => field switch
    {
        "day"         => ts.Days,
        "hour"        => ts.Hours,
        "minute"      => ts.Minutes,
        "second"      => ts.Seconds + ts.Milliseconds / 1000.0,
        "millisecond" => ts.Seconds * 1000.0 + ts.Milliseconds,
        "microsecond" => ts.Seconds * 1_000_000.0 + ts.Milliseconds * 1000.0,
        "epoch"       => ts.TotalSeconds,
        _ => 0.0,
    };

    private static double FromInterval(string field, Interval iv) => field switch
    {
        "year"        => iv.Months / Interval.MonthsPerYear,
        "month"       => iv.Months % Interval.MonthsPerYear,
        "day"         => iv.Days,
        "hour"        => iv.Microseconds / Interval.MicrosPerHour % 24,
        "minute"      => iv.Microseconds / Interval.MicrosPerMinute % 60,
        "second"      => iv.Microseconds % Interval.MicrosPerMinute / (double)Interval.MicrosPerSecond,
        "millisecond" => iv.Microseconds % Interval.MicrosPerMinute / 1000.0,
        "microsecond" => iv.Microseconds % Interval.MicrosPerMinute,
        "decade"      => iv.Months / Interval.MonthsPerYear / 10,
        "century"     => iv.Months / Interval.MonthsPerYear / 100,
        "millennium"  => iv.Months / Interval.MonthsPerYear / 1000,
        "quarter"     => (iv.Months % Interval.MonthsPerYear) / 3 + 1,
        // PG canonical-month / 24h-day epoch: total seconds the interval represents.
        "epoch"       =>
            iv.Months * (double)Interval.DaysPerMonth * Interval.MicrosPerDay / Interval.MicrosPerSecond
            + iv.Days * (double)Interval.MicrosPerDay / Interval.MicrosPerSecond
            + iv.Microseconds / (double)Interval.MicrosPerSecond,
        _ => 0.0,
    };

    /// <inheritdoc />
    public bool IsPure => true;
}
