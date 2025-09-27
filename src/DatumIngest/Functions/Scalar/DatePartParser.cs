namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Supported date part names for date arithmetic functions.
/// </summary>
internal enum DatePartName
{
    /// <summary>Year component.</summary>
    Year,

    /// <summary>Quarter (3-month period).</summary>
    Quarter,

    /// <summary>Month component.</summary>
    Month,

    /// <summary>ISO week number.</summary>
    Week,

    /// <summary>Day component.</summary>
    Day,

    /// <summary>Hour component.</summary>
    Hour,

    /// <summary>Minute component.</summary>
    Minute,

    /// <summary>Second component.</summary>
    Second,

    /// <summary>Millisecond component.</summary>
    Millisecond,

    /// <summary>Microsecond component.</summary>
    Microsecond,

    /// <summary>Decade (year / 10).</summary>
    Decade,

    /// <summary>Century (1-based, e.g. 2001–2100 is century 21).</summary>
    Century,

    /// <summary>Millennium (1-based, e.g. 2001–3000 is millennium 3).</summary>
    Millennium,
}

/// <summary>
/// Parses date part name strings into <see cref="DatePartName"/> values.
/// </summary>
internal static class DatePartParser
{
    /// <summary>
    /// Parses a date part name string (case-insensitive) into a <see cref="DatePartName"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the part name is not recognized.</exception>
    internal static DatePartName Parse(string partName)
    {
        return partName.ToLowerInvariant() switch
        {
            "year" or "years" or "y" => DatePartName.Year,
            "quarter" or "quarters" or "q" => DatePartName.Quarter,
            "month" or "months" or "m" => DatePartName.Month,
            "week" or "weeks" or "w" => DatePartName.Week,
            "day" or "days" or "d" => DatePartName.Day,
            "hour" or "hours" or "h" => DatePartName.Hour,
            "minute" or "minutes" or "min" => DatePartName.Minute,
            "second" or "seconds" or "s" => DatePartName.Second,
            "millisecond" or "milliseconds" or "ms" => DatePartName.Millisecond,
            "microsecond" or "microseconds" or "us" => DatePartName.Microsecond,
            "decade" or "decades" => DatePartName.Decade,
            "century" or "centuries" => DatePartName.Century,
            "millennium" or "millennia" => DatePartName.Millennium,
            _ => throw new ArgumentException(
                $"Unknown date part '{partName}'. Supported: year, quarter, month, week, day, hour, minute, second, millisecond, microsecond, decade, century, millennium."),
        };
    }
}
