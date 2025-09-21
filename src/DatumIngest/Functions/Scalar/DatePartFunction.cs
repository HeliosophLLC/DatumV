using System.Globalization;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts a named component from a Date or DateTime value as a Scalar.
/// <c>date_part('month', date_col)</c> returns the month number (1–12) as a float.
/// </summary>
/// <remarks>
/// Supported part names: year, month, day, day_of_week (0=Sunday–6=Saturday),
/// hour, minute, second, day_of_year, week_of_year (ISO 8601), quarter, is_weekend (0 or 1).
/// For Date inputs, time-based parts (hour, minute, second) return 0.
/// </remarks>
public sealed class DatePartFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "date_part";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("date_part() requires exactly 2 arguments: part name (String) and value (Date or DateTime).");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException("date_part() first argument must be a String (the part name).");
        }

        if (argumentKinds[1] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"date_part() second argument must be Date or DateTime, got {argumentKinds[1]}.");
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

        // Normalize to a DateTime for uniform extraction. For offset-aware parts,
        // read the offset from the original DateTimeOffset before calling .DateTime.
        DateTimeOffset dto = input.ToDateTimeOffset();
        DateTime dateTime = dto.DateTime;
        TimeSpan offset = dto.Offset;

        float result = partName.ToLowerInvariant() switch
        {
            "year" => dateTime.Year,
            "month" => dateTime.Month,
            "day" => dateTime.Day,
            "day_of_week" => (int)dateTime.DayOfWeek,
            "hour" => dateTime.Hour,
            "minute" => dateTime.Minute,
            "second" => dateTime.Second,
            "day_of_year" => dateTime.DayOfYear,
            "week_of_year" => ISOWeek.GetWeekOfYear(dateTime),
            "quarter" => (dateTime.Month - 1) / 3 + 1,
            "is_weekend" => dateTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1f : 0f,
            "timezone" => (float)offset.TotalSeconds,
            "timezone_hour" => offset.Hours,
            "timezone_minute" => offset.Minutes,
            _ => throw new ArgumentException(
                $"Unknown date part '{partName}'. Supported: year, month, day, day_of_week, hour, minute, second, day_of_year, week_of_year, quarter, is_weekend, timezone, timezone_hour, timezone_minute."),
        };

        return DataValue.FromFloat32(result);
    }
}
