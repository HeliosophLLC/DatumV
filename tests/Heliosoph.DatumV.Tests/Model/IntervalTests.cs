using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Model;

/// <summary>
/// Unit tests for the <see cref="Interval"/> value struct: construction,
/// parsing, formatting, justify_*, and apply-against-anchor arithmetic.
/// SQL-level surface (CAST, INTERVAL '...', date_part, +/-, etc.) is covered
/// in <c>IntervalSqlTests</c>.
/// </summary>
public sealed class IntervalTests
{
    [Fact]
    public void Construction_StoresFieldsAsGiven()
    {
        Interval iv = new(months: 14, days: 5, microseconds: 12_345_678L);
        Assert.Equal(14, iv.Months);
        Assert.Equal(5, iv.Days);
        Assert.Equal(12_345_678L, iv.Microseconds);
    }

    [Fact]
    public void FromTimeSpan_SplitsIntoDaysAndMicros_NoMonths()
    {
        Interval iv = Interval.FromTimeSpan(TimeSpan.FromHours(49));
        Assert.Equal(0, iv.Months);
        Assert.Equal(2, iv.Days);
        Assert.Equal(Interval.MicrosPerHour, iv.Microseconds);
    }

    // ─── Format ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 0L, "00:00:00")]
    [InlineData(0, 0, 3_600_000_000L, "01:00:00")]
    [InlineData(0, 1, 0L, "1 day")]
    [InlineData(0, 5, 0L, "5 days")]
    [InlineData(0, -5, 0L, "-5 days")]
    [InlineData(1, 0, 0L, "1 mon")]
    [InlineData(2, 0, 0L, "2 mons")]
    [InlineData(12, 0, 0L, "1 year")]
    [InlineData(14, 3, 14_706_000_000L, "1 year 2 mons 3 days 04:05:06")]
    [InlineData(0, 1, -3_600_000_000L, "1 day -01:00:00")]
    public void Format_PostgresCanonical(int months, int days, long micros, string expected)
    {
        Interval iv = new(months, days, micros);
        Assert.Equal(expected, iv.Format());
    }

    [Fact]
    public void Format_FractionalSeconds_TrimsTrailingZeros()
    {
        // 1.5 seconds
        Interval iv = new(0, 0, 1_500_000L);
        Assert.Equal("00:00:01.5", iv.Format());
    }

    [Fact]
    public void Format_MicrosecondPrecision_PadsAndTrims()
    {
        // 1 microsecond
        Interval iv = new(0, 0, 1L);
        Assert.Equal("00:00:00.000001", iv.Format());
    }

    // ─── Parse ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1 day", 0, 1, 0L)]
    [InlineData("5 days", 0, 5, 0L)]
    [InlineData("-5 days", 0, -5, 0L)]
    [InlineData("1 year", 12, 0, 0L)]
    [InlineData("1 year 2 months", 14, 0, 0L)]
    [InlineData("1 month", 1, 0, 0L)]
    [InlineData("2 mons", 2, 0, 0L)]
    [InlineData("1 week", 0, 7, 0L)]
    [InlineData("2 weeks 3 days", 0, 17, 0L)]
    [InlineData("1 hour", 0, 0, 3_600_000_000L)]
    [InlineData("90 minutes", 0, 0, 5_400_000_000L)]
    [InlineData("30 seconds", 0, 0, 30_000_000L)]
    [InlineData("500 ms", 0, 0, 500_000L)]
    [InlineData("250 microseconds", 0, 0, 250L)]
    [InlineData("1 day -1 hour", 0, 1, -3_600_000_000L)]
    [InlineData("04:05:06", 0, 0, 14_706_000_000L)]
    [InlineData("-04:05:06", 0, 0, -14_706_000_000L)]
    [InlineData("1 day 04:05:06.5", 0, 1, 14_706_500_000L)]
    [InlineData("1 year 2 months 3 days 4 hours 5 mins 6 secs", 14, 3, 14_706_000_000L)]
    [InlineData("1.5 days", 0, 1, 12L * Interval.MicrosPerHour)]
    public void Parse_VerboseForm(string input, int months, int days, long micros)
    {
        Assert.True(Interval.TryParse(input, out Interval iv));
        Assert.Equal(months, iv.Months);
        Assert.Equal(days, iv.Days);
        Assert.Equal(micros, iv.Microseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("5 fortnights")]
    [InlineData("five days")]
    public void Parse_Rejects(string input)
    {
        Assert.False(Interval.TryParse(input, out _));
    }

    // ─── ISO 8601 parse forms ──────────────────────────────────────────

    [Theory]
    [InlineData("P1Y", 12, 0, 0L)]
    [InlineData("P2M", 2, 0, 0L)]
    [InlineData("P1Y2M3D", 14, 3, 0L)]
    [InlineData("P1W", 0, 7, 0L)]
    [InlineData("PT1H", 0, 0, 3_600_000_000L)]
    [InlineData("PT30M", 0, 0, 1_800_000_000L)]
    [InlineData("PT4H5M6S", 0, 0, 14_706_000_000L)]
    [InlineData("P1Y2M3DT4H5M6S", 14, 3, 14_706_000_000L)]
    [InlineData("PT0.5S", 0, 0, 500_000L)]
    [InlineData("-P1Y", -12, 0, 0L)]
    public void Parse_Iso8601(string input, int months, int days, long micros)
    {
        Assert.True(Interval.TryParse(input, out Interval iv));
        Assert.Equal(months, iv.Months);
        Assert.Equal(days, iv.Days);
        Assert.Equal(micros, iv.Microseconds);
    }

    [Theory]
    [InlineData("P0001-02-03", 14, 3, 0L)]
    [InlineData("P0001-02-03T04:05:06", 14, 3, 14_706_000_000L)]
    [InlineData("PT04:05:06", 0, 0, 14_706_000_000L)]
    public void Parse_Iso8601Alternate(string input, int months, int days, long micros)
    {
        Assert.True(Interval.TryParse(input, out Interval iv));
        Assert.Equal(months, iv.Months);
        Assert.Equal(days, iv.Days);
        Assert.Equal(micros, iv.Microseconds);
    }

    // ─── SQL-standard parse forms ──────────────────────────────────────

    [Theory]
    [InlineData("1-2", 14, 0, 0L)]                          // year-month
    [InlineData("-1-2", -14, 0, 0L)]
    [InlineData("0-6", 6, 0, 0L)]
    [InlineData("1-2 3", 14, 3, 0L)]                        // year-month + days
    [InlineData("1-2 3 04:05:06", 14, 3, 14_706_000_000L)]  // full
    [InlineData("3 04:05:06", 0, 3, 14_706_000_000L)]       // day-time only
    [InlineData("-3 04:05:06", 0, -3, -14_706_000_000L)]    // signed day-time
    public void Parse_SqlStandard(string input, int months, int days, long micros)
    {
        Assert.True(Interval.TryParse(input, out Interval iv));
        Assert.Equal(months, iv.Months);
        Assert.Equal(days, iv.Days);
        Assert.Equal(micros, iv.Microseconds);
    }

    // ─── Qualifier ─────────────────────────────────────────────────────

    [Fact]
    public void TryParseWithQualifier_BareNumber_RequiresQualifier()
    {
        Assert.False(Interval.TryParseWithQualifier("1", Interval.Qualifier.None, out _));
    }

    [Theory]
    [InlineData("1", Interval.Qualifier.Year, 12, 0, 0L)]
    [InlineData("1", Interval.Qualifier.Month, 1, 0, 0L)]
    [InlineData("1", Interval.Qualifier.Day, 0, 1, 0L)]
    [InlineData("1", Interval.Qualifier.Hour, 0, 0, 3_600_000_000L)]
    [InlineData("90", Interval.Qualifier.Minute, 0, 0, 5_400_000_000L)]
    [InlineData("1.5", Interval.Qualifier.Year, 18, 0, 0L)]
    public void TryParseWithQualifier_BareNumber_AppliesUnit(
        string input, Interval.Qualifier q, int months, int days, long micros)
    {
        Assert.True(Interval.TryParseWithQualifier(input, q, out Interval iv));
        Assert.Equal(months, iv.Months);
        Assert.Equal(days, iv.Days);
        Assert.Equal(micros, iv.Microseconds);
    }

    [Fact]
    public void TryParseWithQualifier_YearTruncates_SubYearFields()
    {
        Assert.True(Interval.TryParseWithQualifier(
            "1 year 2 months 3 days 1 hour", Interval.Qualifier.Year, out Interval iv));
        Assert.Equal(12, iv.Months);
        Assert.Equal(0, iv.Days);
        Assert.Equal(0L, iv.Microseconds);
    }

    [Fact]
    public void TryParseWithQualifier_DayToHourTruncates_SubHourFields()
    {
        Assert.True(Interval.TryParseWithQualifier(
            "1 day 1:30:45", Interval.Qualifier.DayToHour, out Interval iv));
        Assert.Equal(0, iv.Months);
        Assert.Equal(1, iv.Days);
        Assert.Equal(Interval.MicrosPerHour, iv.Microseconds);
    }

    // ─── Arithmetic / Apply ────────────────────────────────────────────

    [Fact]
    public void AddTo_DateTime_PreservesEndOfMonthSemantics()
    {
        // PG: 2024-01-31 + 1 month = 2024-02-29 (leap year clamp).
        DateTime jan31 = new(2024, 1, 31);
        Interval oneMonth = new(months: 1, days: 0, microseconds: 0);
        Assert.Equal(new DateTime(2024, 2, 29), oneMonth.AddTo(jan31));
    }

    [Fact]
    public void AddTo_DateTime_AppliesMonthsThenDaysThenMicros()
    {
        DateTime source = new(2026, 1, 15, 10, 0, 0);
        Interval iv = new(months: 1, days: 3, microseconds: Interval.MicrosPerHour);
        Assert.Equal(new DateTime(2026, 2, 18, 11, 0, 0), iv.AddTo(source));
    }

    [Fact]
    public void Negate_FlipsAllFields()
    {
        Interval iv = new(2, 3, 4_000_000L);
        Interval neg = iv.Negate();
        Assert.Equal(-2, neg.Months);
        Assert.Equal(-3, neg.Days);
        Assert.Equal(-4_000_000L, neg.Microseconds);
    }

    [Fact]
    public void Multiply_ScalesEveryComponent_CarriesFractions()
    {
        // 1 month × 0.5 = 0 months + 15 days (canonical 30-day month).
        Interval iv = new Interval(1, 0, 0).Multiply(0.5);
        Assert.Equal(0, iv.Months);
        Assert.Equal(15, iv.Days);
        Assert.Equal(0L, iv.Microseconds);
    }

    [Fact]
    public void Multiply_Doubles_BothMonthsAndMicros()
    {
        Interval iv = new Interval(1, 0, Interval.MicrosPerHour).Multiply(2);
        Assert.Equal(2, iv.Months);
        Assert.Equal(0, iv.Days);
        Assert.Equal(2 * Interval.MicrosPerHour, iv.Microseconds);
    }

    // ─── age() ────────────────────────────────────────────────────────

    [Fact]
    public void Age_OneYearExact()
    {
        Interval iv = Interval.Age(new DateTime(2027, 6, 11), new DateTime(2026, 6, 11));
        Assert.Equal(12, iv.Months);
        Assert.Equal(0, iv.Days);
        Assert.Equal(0L, iv.Microseconds);
    }

    [Fact]
    public void Age_BorrowsDaysAcrossMonth()
    {
        // 2026-03-01 minus 2026-01-15: 0 years, 1 month, 14 days.
        // Feb 2026 has 28 days, so Mar 1 - Feb 28 = 1 day, then 28 days
        // - 15 days = 13 days carried back into month-1 → 1 month 14 days.
        Interval iv = Interval.Age(new DateTime(2026, 3, 1), new DateTime(2026, 1, 15));
        Assert.Equal(1, iv.Months);
        Assert.Equal(14, iv.Days);
    }

    [Fact]
    public void Age_Negative_WhenLaterBeforeEarlier()
    {
        Interval iv = Interval.Age(new DateTime(2026, 1, 1), new DateTime(2027, 1, 1));
        Assert.Equal(-12, iv.Months);
        Assert.Equal(0, iv.Days);
    }

    [Fact]
    public void Age_SubDayIntervalSurfacesInMicros()
    {
        Interval iv = Interval.Age(
            new DateTime(2026, 6, 11, 12, 30, 0),
            new DateTime(2026, 6, 11, 10, 0, 0));
        Assert.Equal(0, iv.Months);
        Assert.Equal(0, iv.Days);
        Assert.Equal(2 * Interval.MicrosPerHour + 30 * Interval.MicrosPerMinute, iv.Microseconds);
    }

    // ─── Justify ───────────────────────────────────────────────────────

    [Fact]
    public void JustifyHours_Pushes24hMicrosIntoDays()
    {
        // 36 hours of micros → +1 day + 12 hours.
        Interval iv = new Interval(0, 0, 36 * Interval.MicrosPerHour).JustifyHours();
        Assert.Equal(1, iv.Days);
        Assert.Equal(12 * Interval.MicrosPerHour, iv.Microseconds);
    }

    [Fact]
    public void JustifyDays_Pushes30DaysIntoMonths()
    {
        Interval iv = new Interval(0, 35, 0).JustifyDays();
        Assert.Equal(1, iv.Months);
        Assert.Equal(5, iv.Days);
    }

    [Fact]
    public void JustifyInterval_NormalisesAcrossBoundaries()
    {
        // 35 days + 25 hours → 1 month 6 days 01:00:00.
        Interval iv = new Interval(0, 35, 25 * Interval.MicrosPerHour).JustifyInterval();
        Assert.Equal(1, iv.Months);
        Assert.Equal(6, iv.Days);
        Assert.Equal(Interval.MicrosPerHour, iv.Microseconds);
    }

    // ─── Equality / hashing ───────────────────────────────────────────

    [Fact]
    public void Equality_FieldWise()
    {
        Interval a = new(1, 2, 3);
        Interval b = new(1, 2, 3);
        Interval c = new(1, 2, 4);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.True(a == b);
        Assert.True(a != c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_OneMonthVsThirtyDays_AreNotEqual()
    {
        // PG: 1 month and 30 days are *representationally* distinct even
        // though justify_interval collapses them. Equals is the byte-
        // identity predicate consumed by DISTINCT / GROUP BY.
        Interval oneMonth = new(1, 0, 0);
        Interval thirtyDays = new(0, 30, 0);
        Assert.NotEqual(oneMonth, thirtyDays);
    }

    // ─── DataValue round-trip ─────────────────────────────────────────

    [Fact]
    public void DataValue_RoundTripsAllFieldsViaInline()
    {
        Interval source = new(months: 14, days: -5, microseconds: 12_345_678L);
        DataValue dv = DataValue.FromInterval(source);
        Assert.Equal(DataKind.Interval, dv.Kind);
        Assert.True(dv.IsInline);
        Interval recovered = dv.AsInterval();
        Assert.Equal(source, recovered);
    }

    [Fact]
    public void DataValue_NegativeMicroseconds_RoundTripBitClean()
    {
        Interval source = new(0, 0, -1L);
        DataValue dv = DataValue.FromInterval(source);
        Assert.Equal(-1L, dv.AsInterval().Microseconds);
    }

    [Fact]
    public void DataValue_NullInterval_PreservesKind()
    {
        DataValue dv = DataValue.Null(DataKind.Interval);
        Assert.True(dv.IsNull);
        Assert.Equal(DataKind.Interval, dv.Kind);
        Assert.Throws<InvalidOperationException>(() => dv.AsInterval());
    }

    [Fact]
    public void DataValue_ToDisplayString_UsesPostgresFormat()
    {
        Interval iv = new(14, 3, 14_706_000_000L);
        DataValue dv = DataValue.FromInterval(iv);
        Assert.Equal("1 year 2 mons 3 days 04:05:06", dv.ToDisplayString());
    }

    [Fact]
    public void DataValue_Equals_FieldWise()
    {
        DataValue a = DataValue.FromInterval(new Interval(1, 2, 3));
        DataValue b = DataValue.FromInterval(new Interval(1, 2, 3));
        DataValue c = DataValue.FromInterval(new Interval(1, 2, 4));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
