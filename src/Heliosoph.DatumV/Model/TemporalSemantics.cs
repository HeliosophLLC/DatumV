using System.Globalization;

namespace Heliosoph.DatumV.Model;

/// <summary>
/// PostgreSQL-conformant temporal string parsing and session-time-zone
/// arithmetic, shared by every string→temporal coercion site (CAST, implicit
/// comparison coercion, INSERT literal coercion) and the zone-aware temporal
/// functions (<c>date_part</c>, <c>date_trunc</c>).
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing rule: <b>the machine's local time zone never
/// participates.</b> .NET's parse defaults (<c>DateTimeStyles.AssumeLocal</c>,
/// and <c>DateTimeOffset</c>'s local-offset assumption for offset-less input)
/// make results differ between hosts; every helper here is deterministic —
/// bare wall clocks resolve against an explicit zone argument (null = UTC),
/// matching PG where bare <c>timestamptz</c> input is interpreted in the
/// session <c>TimeZone</c>.
/// </para>
/// <para>
/// DST edges never throw: a wall clock in a spring-forward gap or fall-back
/// overlap resolves through <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/>
/// (ambiguous times take the zone's standard-time offset).
/// </para>
/// </remarks>
public static class TemporalSemantics
{
    /// <summary>
    /// Parses a <c>timestamptz</c> input string with PG semantics: an
    /// explicit offset (<c>+05</c>, <c>Z</c>) is honored; a bare wall clock
    /// is interpreted in <paramref name="sessionZone"/> (null = UTC). The
    /// result is an absolute instant; the input offset itself is not
    /// retained (TimestampTz stores UTC ticks).
    /// </summary>
    public static bool TryParseTimestampTz(string text, TimeZoneInfo? sessionZone, out DateTimeOffset result)
    {
        if (!DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            result = default;
            return false;
        }

        if (HasExplicitOffset(text))
        {
            result = parsed;
            return true;
        }

        // Bare wall clock: AssumeUniversal made the parse deterministic and
        // parsed.DateTime is the wall clock exactly as written; re-anchor it
        // in the session zone.
        result = InterpretInZone(parsed.DateTime, sessionZone ?? TimeZoneInfo.Utc);
        return true;
    }

    /// <summary>
    /// Parses a naive <c>timestamp</c> input string with PG semantics: the
    /// wall clock is taken exactly as written; an explicit offset, if
    /// present, is ignored (PG discards it for <c>timestamp without time
    /// zone</c>). No time zone — machine or session — participates.
    /// </summary>
    public static bool TryParseTimestamp(string text, out DateTime result)
    {
        // DateTimeOffset preserves the written wall clock in .DateTime for
        // both bare and offset-carrying input, so one parse covers both;
        // DateTime.TryParse would shift offset-carrying input to the
        // machine's local zone.
        if (!DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            result = default;
            return false;
        }

        result = DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Unspecified);
        return true;
    }

    /// <summary>
    /// Anchors a naive wall clock in <paramref name="zone"/>, producing the
    /// absolute instant. Never throws on DST edges: gap and overlap wall
    /// clocks resolve via <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/>.
    /// </summary>
    public static DateTimeOffset InterpretInZone(DateTime wallClock, TimeZoneInfo zone)
    {
        DateTime naive = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        TimeSpan offset = zone.GetUtcOffset(naive);
        return new DateTimeOffset(naive, offset);
    }

    /// <summary>
    /// Converts an absolute instant to its wall clock in
    /// <paramref name="zone"/> (a <see cref="DateTimeOffset"/> whose
    /// <c>DateTime</c> is the zone-local reading and whose offset is the
    /// zone's offset at that instant).
    /// </summary>
    public static DateTimeOffset ToZoneWallClock(DateTimeOffset instant, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTime(instant, zone);

    /// <summary>
    /// Detects whether a temporal input string carries an explicit UTC
    /// offset (<c>+05:30</c>, <c>-07</c>, <c>Z</c>). Uses the round-trip
    /// parse's <see cref="DateTimeKind"/>: offset-less input yields
    /// <see cref="DateTimeKind.Unspecified"/>. Input that only
    /// <see cref="DateTimeOffset"/> can parse is treated as offset-carrying.
    /// </summary>
    private static bool HasExplicitOffset(string text)
    {
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime probe))
        {
            return probe.Kind != DateTimeKind.Unspecified;
        }
        return true;
    }
}
