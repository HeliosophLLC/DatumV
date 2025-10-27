using DatumIngest.Functions.Scalar;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="IntervalParser"/> — PostgreSQL-style interval string parsing.
/// </summary>
public class IntervalParserTests : ServiceTestBase
{
    // ───────────────────── Simple units ─────────────────────

    [Theory]
    [InlineData("15 minutes", 0, 0, 15, 0)]
    [InlineData("1 hour", 0, 1, 0, 0)]
    [InlineData("2 hours", 0, 2, 0, 0)]
    [InlineData("1 day", 1, 0, 0, 0)]
    [InlineData("7 days", 7, 0, 0, 0)]
    [InlineData("1 week", 7, 0, 0, 0)]
    [InlineData("3 weeks", 21, 0, 0, 0)]
    [InlineData("30 seconds", 0, 0, 0, 30)]
    public void Parse_SimpleUnits(string input, int days, int hours, int minutes, int seconds)
    {
        TimeSpan result = IntervalParser.Parse(input);
        Assert.Equal(new TimeSpan(days, hours, minutes, seconds), result);
    }

    // ───────────────────── Unit aliases ─────────────────────

    [Theory]
    [InlineData("1 hr")]
    [InlineData("1 hrs")]
    [InlineData("1 h")]
    public void Parse_HourAliases(string input)
    {
        Assert.Equal(TimeSpan.FromHours(1), IntervalParser.Parse(input));
    }

    [Theory]
    [InlineData("5 min")]
    [InlineData("5 mins")]
    [InlineData("5 minute")]
    public void Parse_MinuteAliases(string input)
    {
        Assert.Equal(TimeSpan.FromMinutes(5), IntervalParser.Parse(input));
    }

    [Theory]
    [InlineData("10 sec")]
    [InlineData("10 secs")]
    [InlineData("10 s")]
    public void Parse_SecondAliases(string input)
    {
        Assert.Equal(TimeSpan.FromSeconds(10), IntervalParser.Parse(input));
    }

    [Theory]
    [InlineData("1 d")]
    [InlineData("1 day")]
    [InlineData("1 days")]
    public void Parse_DayAliases(string input)
    {
        Assert.Equal(TimeSpan.FromDays(1), IntervalParser.Parse(input));
    }

    [Theory]
    [InlineData("1 w")]
    [InlineData("1 week")]
    [InlineData("1 weeks")]
    public void Parse_WeekAliases(string input)
    {
        Assert.Equal(TimeSpan.FromDays(7), IntervalParser.Parse(input));
    }

    // ───────────────────── Compound intervals ─────────────────────

    [Fact]
    public void Parse_Compound_DayAndHours()
    {
        TimeSpan result = IntervalParser.Parse("1 day 2 hours");
        Assert.Equal(new TimeSpan(1, 2, 0, 0), result);
    }

    [Fact]
    public void Parse_Compound_DayHoursMinutes()
    {
        TimeSpan result = IntervalParser.Parse("1 day 2 hours 30 minutes");
        Assert.Equal(new TimeSpan(1, 2, 30, 0), result);
    }

    // ───────────────────── HH:MM:SS notation ─────────────────────

    [Fact]
    public void Parse_TimeNotation()
    {
        TimeSpan result = IntervalParser.Parse("1:30:00");
        Assert.Equal(new TimeSpan(1, 30, 0), result);
    }

    // ───────────────────── Mixed notation ─────────────────────

    [Fact]
    public void Parse_Mixed_DayAndTime()
    {
        TimeSpan result = IntervalParser.Parse("1 day 02:30:00");
        Assert.Equal(new TimeSpan(1, 2, 30, 0), result);
    }

    // ───────────────────── Sub-second units ─────────────────────

    [Fact]
    public void Parse_Milliseconds()
    {
        TimeSpan result = IntervalParser.Parse("500 milliseconds");
        Assert.Equal(TimeSpan.FromMilliseconds(500), result);
    }

    [Fact]
    public void Parse_Microseconds()
    {
        TimeSpan result = IntervalParser.Parse("100 microseconds");
        Assert.Equal(TimeSpan.FromTicks(100 * TimeSpan.TicksPerMicrosecond), result);
    }

    // ───────────────────── Case insensitivity ─────────────────────

    [Theory]
    [InlineData("15 MINUTES")]
    [InlineData("15 Minutes")]
    [InlineData("15 minutes")]
    public void Parse_CaseInsensitive(string input)
    {
        Assert.Equal(TimeSpan.FromMinutes(15), IntervalParser.Parse(input));
    }

    // ───────────────────── Whitespace tolerance ─────────────────────

    [Fact]
    public void Parse_LeadingTrailingWhitespace()
    {
        Assert.Equal(TimeSpan.FromHours(1), IntervalParser.Parse("  1 hour  "));
    }

    // ───────────────────── Error cases ─────────────────────

    [Fact]
    public void Parse_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => IntervalParser.Parse(""));
    }

    [Fact]
    public void Parse_Whitespace_Throws()
    {
        Assert.Throws<ArgumentException>(() => IntervalParser.Parse("   "));
    }

    [Fact]
    public void Parse_Month_Throws()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => IntervalParser.Parse("1 month"));
        Assert.Contains("variable length", ex.Message);
    }

    [Fact]
    public void Parse_Year_Throws()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => IntervalParser.Parse("1 year"));
        Assert.Contains("variable length", ex.Message);
    }

    [Fact]
    public void Parse_UnknownUnit_Throws()
    {
        Assert.Throws<ArgumentException>(() => IntervalParser.Parse("5 fortnights"));
    }

    [Fact]
    public void Parse_Garbage_Throws()
    {
        Assert.Throws<ArgumentException>(() => IntervalParser.Parse("not an interval"));
    }
}
