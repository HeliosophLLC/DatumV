using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="DatePartFunction"/>.
/// </summary>
public class DatePartFunctionTests
{
    private readonly DatePartFunction _function = new();

    [Fact]
    public void Name_IsDatePart()
    {
        Assert.Equal("date_part", _function.Name);
    }

    [Fact]
    public void DatePart_Year()
    {
        DataValue result = _function.Execute([DataValue.FromString("year"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(2026f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Month()
    {
        DataValue result = _function.Execute([DataValue.FromString("month"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(3f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Day()
    {
        DataValue result = _function.Execute([DataValue.FromString("day"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(16f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_DayOfWeek()
    {
        // 2026-03-16 is a Monday (DayOfWeek.Monday = 1).
        DataValue result = _function.Execute([DataValue.FromString("day_of_week"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_DayOfWeek_Sunday()
    {
        // 2026-03-15 is a Sunday (DayOfWeek.Sunday = 0).
        DataValue result = _function.Execute([DataValue.FromString("day_of_week"), DataValue.FromDate(new DateOnly(2026, 3, 15))]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Hour_FromDateTime()
    {
        DataValue result = _function.Execute([DataValue.FromString("hour"), DataValue.FromDateTime(new DateTimeOffset(2026, 3, 16, 14, 30, 45, TimeSpan.Zero))]);
        Assert.Equal(14f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Minute_FromDateTime()
    {
        DataValue result = _function.Execute([DataValue.FromString("minute"), DataValue.FromDateTime(new DateTimeOffset(2026, 3, 16, 14, 30, 45, TimeSpan.Zero))]);
        Assert.Equal(30f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Second_FromDateTime()
    {
        DataValue result = _function.Execute([DataValue.FromString("second"), DataValue.FromDateTime(new DateTimeOffset(2026, 3, 16, 14, 30, 45, TimeSpan.Zero))]);
        Assert.Equal(45f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Second_Fractional()
    {
        // 45 seconds + 500ms = 45.5 (PostgreSQL returns fractional seconds)
        DataValue result = _function.Execute([DataValue.FromString("second"), DataValue.FromDateTime(new DateTimeOffset(2026, 3, 16, 14, 30, 45, 500, TimeSpan.Zero))]);
        Assert.Equal(45.5f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Hour_FromDate_ReturnsZero()
    {
        DataValue result = _function.Execute([DataValue.FromString("hour"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_DayOfYear()
    {
        // 2026-03-16 is the 75th day of the year.
        DataValue result = _function.Execute([DataValue.FromString("day_of_year"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(75f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_WeekOfYear()
    {
        // 2026-03-16 (Monday) is ISO week 12.
        DataValue result = _function.Execute([DataValue.FromString("week_of_year"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(12f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Quarter()
    {
        DataValue result = _function.Execute([DataValue.FromString("quarter"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Quarter_Q4()
    {
        DataValue result = _function.Execute([DataValue.FromString("quarter"), DataValue.FromDate(new DateOnly(2026, 12, 1))]);
        Assert.Equal(4f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_IsWeekend_Weekday()
    {
        // Monday is not a weekend.
        DataValue result = _function.Execute([DataValue.FromString("is_weekend"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_IsWeekend_Saturday()
    {
        DataValue result = _function.Execute([DataValue.FromString("is_weekend"), DataValue.FromDate(new DateOnly(2026, 3, 14))]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_IsWeekend_Sunday()
    {
        DataValue result = _function.Execute([DataValue.FromString("is_weekend"), DataValue.FromDate(new DateOnly(2026, 3, 15))]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_NullInput_ReturnsTypedNull()
    {
        DataValue result = _function.Execute([DataValue.FromString("year"), DataValue.Null(DataKind.Date)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public void DatePart_InvalidPartName_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.Execute([
            DataValue.FromString("invalid"),
            DataValue.FromDate(new DateOnly(2026, 1, 1))
        ]));
    }

    [Fact]
    public void DatePart_CaseInsensitive()
    {
        DataValue result = _function.Execute([DataValue.FromString("YEAR"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(2026f, result.AsFloat32());
    }

    [Fact]
    public void ValidateArguments_RejectsNonStringPartName()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Float32, DataKind.Date]));
    }

    [Fact]
    public void ValidateArguments_RejectsNonTemporalValue()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void ValidateArguments_RejectsWrongArgumentCount()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void ValidateArguments_AcceptsTimeInput()
    {
        DataKind result = _function.ValidateArguments([DataKind.String, DataKind.Time]);
        Assert.Equal(DataKind.Float32, result);
    }

    // ───────────────────── PostgreSQL aliases ─────────────────────

    [Fact]
    public void DatePart_Dow_MatchesDayOfWeek()
    {
        // 2026-03-15 is a Sunday → dow = 0
        DataValue result = _function.Execute([DataValue.FromString("dow"), DataValue.FromDate(new DateOnly(2026, 3, 15))]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Doy_MatchesDayOfYear()
    {
        DataValue result = _function.Execute([DataValue.FromString("doy"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(75f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Week_MatchesWeekOfYear()
    {
        DataValue result = _function.Execute([DataValue.FromString("week"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(12f, result.AsFloat32());
    }

    // ───────────────────── new PostgreSQL fields ─────────────────────

    [Fact]
    public void DatePart_Century()
    {
        // Year 2001 is century 21 (ceil(2001/100) = 21)
        DataValue result = _function.Execute([DataValue.FromString("century"), DataValue.FromDate(new DateOnly(2001, 1, 1))]);
        Assert.Equal(21f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Century_Year2000()
    {
        // Year 2000 is century 20 (ceil(2000/100) = 20)
        DataValue result = _function.Execute([DataValue.FromString("century"), DataValue.FromDate(new DateOnly(2000, 12, 31))]);
        Assert.Equal(20f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Decade()
    {
        // 2026 / 10 = 202
        DataValue result = _function.Execute([DataValue.FromString("decade"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(202f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Millennium()
    {
        // Year 2001 is millennium 3 (ceil(2001/1000) = 3)
        DataValue result = _function.Execute([DataValue.FromString("millennium"), DataValue.FromDate(new DateOnly(2001, 1, 1))]);
        Assert.Equal(3f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Isodow_Sunday()
    {
        // 2026-03-15 is Sunday → isodow = 7 (ISO: 1=Mon, 7=Sun)
        DataValue result = _function.Execute([DataValue.FromString("isodow"), DataValue.FromDate(new DateOnly(2026, 3, 15))]);
        Assert.Equal(7f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Isodow_Monday()
    {
        // 2026-03-16 is Monday → isodow = 1
        DataValue result = _function.Execute([DataValue.FromString("isodow"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Isoyear()
    {
        DataValue result = _function.Execute([DataValue.FromString("isoyear"), DataValue.FromDate(new DateOnly(2026, 1, 1))]);
        Assert.Equal(2026f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Epoch_UnixEpoch()
    {
        DataValue result = _function.Execute([DataValue.FromString("epoch"), DataValue.FromDateTime(new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero))]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Epoch_KnownValue()
    {
        // 2020-01-01 00:00:00 UTC = 1577836800 seconds since epoch
        DataValue result = _function.Execute([DataValue.FromString("epoch"), DataValue.FromDateTime(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))]);
        Assert.Equal(1577836800f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Julian_J2000()
    {
        // 2000-01-01 12:00 (J2000.0) -> Julian day 2451545.0
        DataValue result = _function.Execute([DataValue.FromString("julian"), DataValue.FromDateTime(new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero))]);
        Assert.Equal(2451545.0f, result.AsFloat32(), 0.5f);
    }

    [Fact]
    public void DatePart_Millisecond()
    {
        // 45 seconds + 500ms -> 45500 milliseconds
        DataValue result = _function.Execute([DataValue.FromString("millisecond"), DataValue.FromDateTime(new DateTimeOffset(2026, 3, 16, 14, 30, 45, 500, TimeSpan.Zero))]);
        Assert.Equal(45500f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Microsecond()
    {
        // 45 seconds + 500ms -> 45500000 microseconds
        DataValue result = _function.Execute([DataValue.FromString("microsecond"), DataValue.FromDateTime(new DateTimeOffset(2026, 3, 16, 14, 30, 45, 500, TimeSpan.Zero))]);
        Assert.Equal(45500000f, result.AsFloat32());
    }

    // ───────────────────── Time inputs ─────────────────────

    [Fact]
    public void DatePart_Hour_FromTime()
    {
        DataValue result = _function.Execute([DataValue.FromString("hour"), DataValue.FromTime(new TimeOnly(14, 30, 45))]);
        Assert.Equal(14f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Minute_FromTime()
    {
        DataValue result = _function.Execute([DataValue.FromString("minute"), DataValue.FromTime(new TimeOnly(14, 30, 45))]);
        Assert.Equal(30f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Second_FromTime()
    {
        DataValue result = _function.Execute([DataValue.FromString("second"), DataValue.FromTime(new TimeOnly(14, 30, 45))]);
        Assert.Equal(45f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Year_FromTime_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.Execute([DataValue.FromString("year"), DataValue.FromTime(new TimeOnly(14, 30, 45))]));
    }

    // ───────────────────── timezone parts ─────────────────────

    [Fact]
    public void DatePart_Timezone_UtcIsZero()
    {
        DataValue input = DataValue.FromDateTime(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        DataValue result = _function.Execute([DataValue.FromString("timezone"), input]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Timezone_PositiveOffset()
    {
        // +05:30 India Standard Time = 19800 seconds
        DataValue input = DataValue.FromDateTime(new DateTimeOffset(2026, 1, 1, 12, 0, 0, new TimeSpan(5, 30, 0)));
        DataValue result = _function.Execute([DataValue.FromString("timezone"), input]);
        Assert.Equal(19800f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_Timezone_NegativeOffset()
    {
        // -05:00 EST = -18000 seconds
        DataValue input = DataValue.FromDateTime(new DateTimeOffset(2026, 1, 1, 12, 0, 0, new TimeSpan(-5, 0, 0)));
        DataValue result = _function.Execute([DataValue.FromString("timezone"), input]);
        Assert.Equal(-18000f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_TimezoneHour_Positive()
    {
        DataValue input = DataValue.FromDateTime(new DateTimeOffset(2026, 1, 1, 12, 0, 0, new TimeSpan(5, 30, 0)));
        DataValue result = _function.Execute([DataValue.FromString("timezone_hour"), input]);
        Assert.Equal(5f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_TimezoneHour_Negative()
    {
        DataValue input = DataValue.FromDateTime(new DateTimeOffset(2026, 1, 1, 12, 0, 0, new TimeSpan(-5, 0, 0)));
        DataValue result = _function.Execute([DataValue.FromString("timezone_hour"), input]);
        Assert.Equal(-5f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_TimezoneMinute_HalfHourZone()
    {
        // India +05:30 → minutes = 30
        DataValue input = DataValue.FromDateTime(new DateTimeOffset(2026, 1, 1, 12, 0, 0, new TimeSpan(5, 30, 0)));
        DataValue result = _function.Execute([DataValue.FromString("timezone_minute"), input]);
        Assert.Equal(30f, result.AsFloat32());
    }

    [Fact]
    public void DatePart_TimezoneMinute_WholeHourZone()
    {
        DataValue input = DataValue.FromDateTime(new DateTimeOffset(2026, 1, 1, 12, 0, 0, new TimeSpan(-5, 0, 0)));
        DataValue result = _function.Execute([DataValue.FromString("timezone_minute"), input]);
        Assert.Equal(0f, result.AsFloat32());
    }
}
