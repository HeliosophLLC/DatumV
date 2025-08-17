using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for Date/Time extraction functions: <see cref="YearFunction"/>, <see cref="MonthFunction"/>,
/// <see cref="DayFunction"/>, <see cref="HourFunction"/>, <see cref="MinuteFunction"/>,
/// <see cref="SecondFunction"/>, <see cref="QuarterFunction"/>, <see cref="DayOfWeekFunction"/>,
/// and <see cref="DayOfYearFunction"/>.
/// </summary>
public class DateExtractionFunctionTests
{
    private static readonly DateOnly SampleDate = new(2026, 3, 16);
    private static readonly DateTimeOffset SampleDateTime = new(2026, 3, 16, 14, 30, 45, TimeSpan.Zero);

    // ───────────────────── year() ─────────────────────

    [Fact]
    public void Year_Name()
    {
        YearFunction function = new();
        Assert.Equal("year", function.Name);
    }

    [Fact]
    public void Year_FromDate()
    {
        YearFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(SampleDate)]);
        Assert.Equal(2026f, result.AsFloat32());
    }

    [Fact]
    public void Year_FromDateTime()
    {
        YearFunction function = new();
        DataValue result = function.Execute([DataValue.FromDateTime(SampleDateTime)]);
        Assert.Equal(2026f, result.AsFloat32());
    }

    [Fact]
    public void Year_NullReturnsNull()
    {
        YearFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Date)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public void Year_ValidateArguments_WrongCount()
    {
        YearFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([]));
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Date, DataKind.Date]));
    }

    [Fact]
    public void Year_ValidateArguments_WrongType()
    {
        YearFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.String]));
    }

    // ───────────────────── month() ─────────────────────

    [Fact]
    public void Month_Name()
    {
        MonthFunction function = new();
        Assert.Equal("month", function.Name);
    }

    [Fact]
    public void Month_FromDate()
    {
        MonthFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(SampleDate)]);
        Assert.Equal(3f, result.AsFloat32());
    }

    [Fact]
    public void Month_FromDateTime()
    {
        MonthFunction function = new();
        DataValue result = function.Execute([DataValue.FromDateTime(SampleDateTime)]);
        Assert.Equal(3f, result.AsFloat32());
    }

    [Fact]
    public void Month_NullReturnsNull()
    {
        MonthFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.DateTime)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    // ───────────────────── day() ─────────────────────

    [Fact]
    public void Day_Name()
    {
        DayFunction function = new();
        Assert.Equal("day", function.Name);
    }

    [Fact]
    public void Day_FromDate()
    {
        DayFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(SampleDate)]);
        Assert.Equal(16f, result.AsFloat32());
    }

    [Fact]
    public void Day_FromDateTime()
    {
        DayFunction function = new();
        DataValue result = function.Execute([DataValue.FromDateTime(SampleDateTime)]);
        Assert.Equal(16f, result.AsFloat32());
    }

    [Fact]
    public void Day_NullReturnsNull()
    {
        DayFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Date)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    // ───────────────────── hour() ─────────────────────

    [Fact]
    public void Hour_Name()
    {
        HourFunction function = new();
        Assert.Equal("hour", function.Name);
    }

    [Fact]
    public void Hour_FromDateTime()
    {
        HourFunction function = new();
        DataValue result = function.Execute([DataValue.FromDateTime(SampleDateTime)]);
        Assert.Equal(14f, result.AsFloat32());
    }

    [Fact]
    public void Hour_FromDate_ReturnsZero()
    {
        HourFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(SampleDate)]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void Hour_NullReturnsNull()
    {
        HourFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.DateTime)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    // ───────────────────── minute() ─────────────────────

    [Fact]
    public void Minute_Name()
    {
        MinuteFunction function = new();
        Assert.Equal("minute", function.Name);
    }

    [Fact]
    public void Minute_FromDateTime()
    {
        MinuteFunction function = new();
        DataValue result = function.Execute([DataValue.FromDateTime(SampleDateTime)]);
        Assert.Equal(30f, result.AsFloat32());
    }

    [Fact]
    public void Minute_FromDate_ReturnsZero()
    {
        MinuteFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(SampleDate)]);
        Assert.Equal(0f, result.AsFloat32());
    }

    // ───────────────────── second() ─────────────────────

    [Fact]
    public void Second_Name()
    {
        SecondFunction function = new();
        Assert.Equal("second", function.Name);
    }

    [Fact]
    public void Second_FromDateTime()
    {
        SecondFunction function = new();
        DataValue result = function.Execute([DataValue.FromDateTime(SampleDateTime)]);
        Assert.Equal(45f, result.AsFloat32());
    }

    [Fact]
    public void Second_FromDate_ReturnsZero()
    {
        SecondFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(SampleDate)]);
        Assert.Equal(0f, result.AsFloat32());
    }

    // ───────────────────── quarter() ─────────────────────

    [Fact]
    public void Quarter_Name()
    {
        QuarterFunction function = new();
        Assert.Equal("quarter", function.Name);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 3)]
    [InlineData(9, 3)]
    [InlineData(10, 4)]
    [InlineData(12, 4)]
    public void Quarter_AllQuarters(int month, int expectedQuarter)
    {
        QuarterFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(new DateOnly(2026, month, 1))]);
        Assert.Equal((float)expectedQuarter, result.AsFloat32());
    }

    [Fact]
    public void Quarter_NullReturnsNull()
    {
        QuarterFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Date)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    // ───────────────────── dayofweek() ─────────────────────

    [Fact]
    public void DayOfWeek_Name()
    {
        DayOfWeekFunction function = new();
        Assert.Equal("dayofweek", function.Name);
    }

    [Fact]
    public void DayOfWeek_Monday_Returns1()
    {
        // 2026-03-16 is a Monday.
        DayOfWeekFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void DayOfWeek_Sunday_Returns7()
    {
        // 2026-03-15 is a Sunday.
        DayOfWeekFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(new DateOnly(2026, 3, 15))]);
        Assert.Equal(7f, result.AsFloat32());
    }

    [Fact]
    public void DayOfWeek_Wednesday_Returns3()
    {
        // 2026-03-18 is a Wednesday.
        DayOfWeekFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(new DateOnly(2026, 3, 18))]);
        Assert.Equal(3f, result.AsFloat32());
    }

    [Fact]
    public void DayOfWeek_Saturday_Returns6()
    {
        // 2026-03-14 is a Saturday.
        DayOfWeekFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(new DateOnly(2026, 3, 14))]);
        Assert.Equal(6f, result.AsFloat32());
    }

    [Fact]
    public void DayOfWeek_FromDateTime()
    {
        DayOfWeekFunction function = new();
        DataValue result = function.Execute([DataValue.FromDateTime(SampleDateTime)]);
        Assert.Equal(1f, result.AsFloat32()); // Monday
    }

    [Fact]
    public void DayOfWeek_NullReturnsNull()
    {
        DayOfWeekFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Date)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    // ───────────────────── dayofyear() ─────────────────────

    [Fact]
    public void DayOfYear_Name()
    {
        DayOfYearFunction function = new();
        Assert.Equal("dayofyear", function.Name);
    }

    [Fact]
    public void DayOfYear_FirstDayOfYear()
    {
        DayOfYearFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(new DateOnly(2026, 1, 1))]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void DayOfYear_March16()
    {
        // 2026-03-16 is the 75th day of the year.
        DayOfYearFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(SampleDate)]);
        Assert.Equal(75f, result.AsFloat32());
    }

    [Fact]
    public void DayOfYear_LastDayOfYear()
    {
        DayOfYearFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(new DateOnly(2026, 12, 31))]);
        Assert.Equal(365f, result.AsFloat32());
    }

    [Fact]
    public void DayOfYear_LeapYear_Dec31()
    {
        DayOfYearFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(new DateOnly(2024, 12, 31))]);
        Assert.Equal(366f, result.AsFloat32());
    }

    [Fact]
    public void DayOfYear_FromDateTime()
    {
        DayOfYearFunction function = new();
        DataValue result = function.Execute([DataValue.FromDateTime(SampleDateTime)]);
        Assert.Equal(75f, result.AsFloat32());
    }

    [Fact]
    public void DayOfYear_NullReturnsNull()
    {
        DayOfYearFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Date)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }
}
