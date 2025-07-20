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
        Assert.Equal(2026f, result.AsScalar());
    }

    [Fact]
    public void DatePart_Month()
    {
        DataValue result = _function.Execute([DataValue.FromString("month"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(3f, result.AsScalar());
    }

    [Fact]
    public void DatePart_Day()
    {
        DataValue result = _function.Execute([DataValue.FromString("day"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(16f, result.AsScalar());
    }

    [Fact]
    public void DatePart_DayOfWeek()
    {
        // 2026-03-16 is a Monday (DayOfWeek.Monday = 1).
        DataValue result = _function.Execute([DataValue.FromString("day_of_week"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void DatePart_DayOfWeek_Sunday()
    {
        // 2026-03-15 is a Sunday (DayOfWeek.Sunday = 0).
        DataValue result = _function.Execute([DataValue.FromString("day_of_week"), DataValue.FromDate(new DateOnly(2026, 3, 15))]);
        Assert.Equal(0f, result.AsScalar());
    }

    [Fact]
    public void DatePart_Hour_FromDateTime()
    {
        DataValue result = _function.Execute([DataValue.FromString("hour"), DataValue.FromDateTime(new DateTimeOffset(2026, 3, 16, 14, 30, 45, TimeSpan.Zero))]);
        Assert.Equal(14f, result.AsScalar());
    }

    [Fact]
    public void DatePart_Minute_FromDateTime()
    {
        DataValue result = _function.Execute([DataValue.FromString("minute"), DataValue.FromDateTime(new DateTimeOffset(2026, 3, 16, 14, 30, 45, TimeSpan.Zero))]);
        Assert.Equal(30f, result.AsScalar());
    }

    [Fact]
    public void DatePart_Second_FromDateTime()
    {
        DataValue result = _function.Execute([DataValue.FromString("second"), DataValue.FromDateTime(new DateTimeOffset(2026, 3, 16, 14, 30, 45, TimeSpan.Zero))]);
        Assert.Equal(45f, result.AsScalar());
    }

    [Fact]
    public void DatePart_Hour_FromDate_ReturnsZero()
    {
        DataValue result = _function.Execute([DataValue.FromString("hour"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(0f, result.AsScalar());
    }

    [Fact]
    public void DatePart_DayOfYear()
    {
        // 2026-03-16 is the 75th day of the year.
        DataValue result = _function.Execute([DataValue.FromString("day_of_year"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(75f, result.AsScalar());
    }

    [Fact]
    public void DatePart_WeekOfYear()
    {
        // 2026-03-16 (Monday) is ISO week 12.
        DataValue result = _function.Execute([DataValue.FromString("week_of_year"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(12f, result.AsScalar());
    }

    [Fact]
    public void DatePart_Quarter()
    {
        DataValue result = _function.Execute([DataValue.FromString("quarter"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void DatePart_Quarter_Q4()
    {
        DataValue result = _function.Execute([DataValue.FromString("quarter"), DataValue.FromDate(new DateOnly(2026, 12, 1))]);
        Assert.Equal(4f, result.AsScalar());
    }

    [Fact]
    public void DatePart_IsWeekend_Weekday()
    {
        // Monday is not a weekend.
        DataValue result = _function.Execute([DataValue.FromString("is_weekend"), DataValue.FromDate(new DateOnly(2026, 3, 16))]);
        Assert.Equal(0f, result.AsScalar());
    }

    [Fact]
    public void DatePart_IsWeekend_Saturday()
    {
        DataValue result = _function.Execute([DataValue.FromString("is_weekend"), DataValue.FromDate(new DateOnly(2026, 3, 14))]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void DatePart_IsWeekend_Sunday()
    {
        DataValue result = _function.Execute([DataValue.FromString("is_weekend"), DataValue.FromDate(new DateOnly(2026, 3, 15))]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void DatePart_NullInput_ReturnsTypedNull()
    {
        DataValue result = _function.Execute([DataValue.FromString("year"), DataValue.Null(DataKind.Date)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Scalar, result.Kind);
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
        Assert.Equal(2026f, result.AsScalar());
    }

    [Fact]
    public void ValidateArguments_RejectsNonStringPartName()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Scalar, DataKind.Date]));
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
}
