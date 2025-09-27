using System.Text.RegularExpressions;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Parses PostgreSQL-style interval strings into <see cref="TimeSpan"/> values.
/// Supports formats like <c>'15 minutes'</c>, <c>'1 day 2 hours'</c>,
/// <c>'1:30:00'</c>, and <c>'1 day 02:30:00'</c>.
/// </summary>
/// <remarks>
/// Only fixed-length intervals are supported (weeks, days, hours, minutes, seconds,
/// milliseconds, microseconds). Month and year intervals are rejected because they
/// have variable length, matching PostgreSQL's <c>date_bin</c> behavior.
/// </remarks>
internal static partial class IntervalParser
{
    /// <summary>
    /// Parses a PostgreSQL-style interval string into a <see cref="TimeSpan"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the string is empty, contains unsupported units (month/year),
    /// or cannot be parsed.
    /// </exception>
    internal static TimeSpan Parse(string interval)
    {
        if (string.IsNullOrWhiteSpace(interval))
        {
            throw new ArgumentException("Interval string must not be empty.");
        }

        string trimmed = interval.Trim();

        // Try pure HH:MM:SS or HH:MM:SS.fff format first.
        if (TimeSpan.TryParse(trimmed, out TimeSpan directResult))
        {
            return directResult;
        }

        // Parse "N unit [N unit ...]" pairs, optionally followed by HH:MM:SS.
        TimeSpan total = TimeSpan.Zero;
        string remaining = trimmed;
        bool matched = false;

        foreach (Match match in QuantityUnitPattern().Matches(remaining))
        {
            matched = true;
            double quantity = double.Parse(match.Groups["qty"].Value);
            string unit = match.Groups["unit"].Value.ToLowerInvariant();

            total += unit switch
            {
                "week" or "weeks" or "w" => TimeSpan.FromDays(quantity * 7),
                "day" or "days" or "d" => TimeSpan.FromDays(quantity),
                "hour" or "hours" or "hrs" or "hr" or "h" => TimeSpan.FromHours(quantity),
                "minute" or "minutes" or "mins" or "min" => TimeSpan.FromMinutes(quantity),
                "second" or "seconds" or "secs" or "sec" or "s" => TimeSpan.FromSeconds(quantity),
                "millisecond" or "milliseconds" or "ms" => TimeSpan.FromMilliseconds(quantity),
                "microsecond" or "microseconds" or "us" =>
                    TimeSpan.FromTicks((long)(quantity * TimeSpan.TicksPerMicrosecond)),

                "month" or "months" or "year" or "years" =>
                    throw new ArgumentException(
                        $"date_bin does not support month or year intervals because they have variable length. Got: '{interval}'."),

                _ => throw new ArgumentException($"Unknown interval unit '{unit}' in '{interval}'."),
            };
        }

        if (matched)
        {
            // Check for trailing HH:MM:SS after the quantity-unit pairs.
            string afterPairs = QuantityUnitPattern().Replace(remaining, "").Trim();
            if (afterPairs.Length > 0)
            {
                if (TimeSpan.TryParse(afterPairs, out TimeSpan timePart))
                {
                    total += timePart;
                }
                else
                {
                    throw new ArgumentException($"Cannot parse trailing interval component '{afterPairs}' in '{interval}'.");
                }
            }

            return total;
        }

        throw new ArgumentException(
            $"Cannot parse interval '{interval}'. Expected format like '15 minutes', '1 day 2 hours', or '1:30:00'.");
    }

    [GeneratedRegex(@"(?<qty>-?\d+(?:\.\d+)?)\s*(?<unit>[a-zA-Z]+)", RegexOptions.Compiled)]
    private static partial Regex QuantityUnitPattern();
}
