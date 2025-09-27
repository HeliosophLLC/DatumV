using System.Globalization;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts a named component from a Date, DateTime, or Time value as a scalar.
/// <c>date_part('month', date_col)</c> returns the month number (1–12) as a float.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL-compatible fields: year, month, day, hour, minute, second (fractional),
/// quarter, week, dow, doy, epoch, century, decade, millennium, isodow, isoyear,
/// julian, millisecond, microsecond, timezone, timezone_hour, timezone_minute.
/// </para>
/// <para>
/// DatumIngest extensions (kept for backward compatibility): day_of_week, day_of_year,
/// week_of_year, is_weekend.
/// </para>
/// </remarks>
public sealed class DatePartFunction : IScalarFunction
{
    /// <summary>Julian day number for the OLE Automation epoch (1899-12-30).</summary>
    private const double OleAutomationEpochJulianDay = 2415018.5;

    /// <inheritdoc />
    public string Name => "date_part";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("date_part() requires exactly 2 arguments: part name (String) and value (Date, DateTime, or Time).");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException("date_part() first argument must be a String (the part name).");
        }

        if (argumentKinds[1] is not (DataKind.Date or DataKind.DateTime or DataKind.Time))
        {
            throw new ArgumentException($"date_part() second argument must be Date, DateTime, or Time, got {argumentKinds[1]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue partValue = arguments[0];
        DataValue input = arguments[1];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        string partName = partValue.AsString();

        // Time-only inputs are handled separately — they only support time-related parts.
        if (input.Kind == DataKind.Time)
        {
            return DataValue.FromFloat32(ExtractFromTime(input.AsTime(), partName));
        }

        // Normalize to a DateTime for uniform extraction. For offset-aware parts,
        // read the offset from the original DateTimeOffset before calling .DateTime.
        DateTimeOffset dto = input.ToDateTimeOffset();
        DateTime dateTime = dto.DateTime;
        TimeSpan offset = dto.Offset;

        float result = partName.ToLowerInvariant() switch
        {
            // PostgreSQL-compatible fields
            "year" => dateTime.Year,
            "month" => dateTime.Month,
            "day" => dateTime.Day,
            "hour" => dateTime.Hour,
            "minute" => dateTime.Minute,
            "second" => dateTime.Second + dateTime.Millisecond / 1000f,
            "quarter" => (dateTime.Month - 1) / 3 + 1,
            "week" or "week_of_year" => ISOWeek.GetWeekOfYear(dateTime),
            "dow" or "day_of_week" => (int)dateTime.DayOfWeek,
            "doy" or "day_of_year" => dateTime.DayOfYear,
            "isodow" => dateTime.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)dateTime.DayOfWeek,
            "isoyear" => ISOWeek.GetYear(dateTime),
            "epoch" => (float)(dto.ToUnixTimeMilliseconds() / 1000.0),
            "century" => (int)System.Math.Ceiling(dateTime.Year / 100.0),
            "decade" => dateTime.Year / 10,
            "millennium" => (int)System.Math.Ceiling(dateTime.Year / 1000.0),
            "julian" => (float)(dateTime.ToOADate() + OleAutomationEpochJulianDay),
            "millisecond" or "milliseconds" => dateTime.Second * 1000f + dateTime.Millisecond,
            "microsecond" or "microseconds" => dateTime.Second * 1_000_000f + dateTime.Millisecond * 1000f,
            "timezone" => (float)offset.TotalSeconds,
            "timezone_hour" => offset.Hours,
            "timezone_minute" => offset.Minutes,

            // DatumIngest extension
            "is_weekend" => dateTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1f : 0f,

            _ => throw new ArgumentException(
                $"Unknown date part '{partName}'. Supported: year, month, day, hour, minute, second, quarter, week, dow, doy, " +
                "isodow, isoyear, epoch, century, decade, millennium, julian, millisecond, microsecond, " +
                "timezone, timezone_hour, timezone_minute, day_of_week, day_of_year, week_of_year, is_weekend."),
        };

        return DataValue.FromFloat32(result);
    }

    private static float ExtractFromTime(TimeOnly time, string partName)
    {
        return partName.ToLowerInvariant() switch
        {
            "hour" => time.Hour,
            "minute" => time.Minute,
            "second" => time.Second + time.Millisecond / 1000f,
            "millisecond" or "milliseconds" => time.Second * 1000f + time.Millisecond,
            "microsecond" or "microseconds" => time.Second * 1_000_000f + time.Millisecond * 1000f + time.Microsecond,
            "epoch" => (float)(time.Hour * 3600.0 + time.Minute * 60.0 + time.Second + time.Millisecond / 1000.0),
            _ => throw new ArgumentException(
                $"date_part '{partName}' is not supported for Time values. Supported: hour, minute, second, millisecond, microsecond, epoch."),
        };
    }
}
