using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="DateTruncFunction"/>.
/// </summary>
public class DateTruncFunctionTests : ServiceTestBase
{
    private readonly DateTruncFunction _function = new();

    [Fact]
    public void Name_IsDateTrunc()
    {
        Assert.Equal("date_trunc", _function.Name);
    }

    [Fact]
    public void DateTrunc_Year()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("year"),
            DataValue.FromDate(new DateOnly(2024, 6, 15))
        ]);
        Assert.Equal(new DateOnly(2024, 1, 1), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Quarter_Q1()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("quarter"),
            DataValue.FromDate(new DateOnly(2024, 3, 15))
        ]);
        Assert.Equal(new DateOnly(2024, 1, 1), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Quarter_Q2()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("quarter"),
            DataValue.FromDate(new DateOnly(2024, 5, 20))
        ]);
        Assert.Equal(new DateOnly(2024, 4, 1), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Quarter_Q3()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("quarter"),
            DataValue.FromDate(new DateOnly(2024, 9, 30))
        ]);
        Assert.Equal(new DateOnly(2024, 7, 1), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Quarter_Q4()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("quarter"),
            DataValue.FromDate(new DateOnly(2024, 12, 25))
        ]);
        Assert.Equal(new DateOnly(2024, 10, 1), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Month()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("month"),
            DataValue.FromDate(new DateOnly(2024, 6, 15))
        ]);
        Assert.Equal(new DateOnly(2024, 6, 1), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Week_Monday()
    {
        // 2024-06-12 is a Wednesday; should truncate to 2024-06-10 (Monday).
        DataValue result = _function.Execute([
            DataValue.FromString("week"),
            DataValue.FromDate(new DateOnly(2024, 6, 12))
        ]);
        Assert.Equal(new DateOnly(2024, 6, 10), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Week_AlreadyMonday()
    {
        // 2024-06-10 is a Monday; should stay at Monday.
        DataValue result = _function.Execute([
            DataValue.FromString("week"),
            DataValue.FromDate(new DateOnly(2024, 6, 10))
        ]);
        Assert.Equal(new DateOnly(2024, 6, 10), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Week_Sunday_GoesToPreviousMonday()
    {
        // 2024-06-16 is a Sunday; should truncate to 2024-06-10 (Monday).
        DataValue result = _function.Execute([
            DataValue.FromString("week"),
            DataValue.FromDate(new DateOnly(2024, 6, 16))
        ]);
        Assert.Equal(new DateOnly(2024, 6, 10), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Day_FromDateTime()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromDateTime(new DateTimeOffset(2024, 6, 15, 14, 30, 45, TimeSpan.Zero))
        ]);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateTrunc_Hour()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("hour"),
            DataValue.FromDateTime(new DateTimeOffset(2024, 6, 15, 14, 30, 45, TimeSpan.Zero))
        ]);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 14, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateTrunc_Minute()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("minute"),
            DataValue.FromDateTime(new DateTimeOffset(2024, 6, 15, 14, 30, 45, TimeSpan.Zero))
        ]);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 14, 30, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateTrunc_NullReturnsNull()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("year"),
            DataValue.Null(DataKind.Date)
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Date, result.Kind);
    }

    [Fact]
    public void DateTrunc_PreservesDateKind()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("year"),
            DataValue.FromDate(new DateOnly(2024, 6, 15))
        ]);
        Assert.Equal(DataKind.Date, result.Kind);
    }

    [Fact]
    public void DateTrunc_PreservesDateTimeKind()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("year"),
            DataValue.FromDateTime(new DateTimeOffset(2024, 6, 15, 14, 30, 0, TimeSpan.Zero))
        ]);
        Assert.Equal(DataKind.DateTime, result.Kind);
    }

    // ───────────────────── new PostgreSQL precisions ─────────────────────

    [Fact]
    public void DateTrunc_Decade()
    {
        // 2026 → 2020-01-01
        DataValue result = _function.Execute([
            DataValue.FromString("decade"),
            DataValue.FromDate(new DateOnly(2026, 6, 15))
        ]);
        Assert.Equal(new DateOnly(2020, 1, 1), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Decade_ExactBoundary()
    {
        // 2020 → 2020-01-01 (already at decade boundary)
        DataValue result = _function.Execute([
            DataValue.FromString("decade"),
            DataValue.FromDate(new DateOnly(2020, 1, 1))
        ]);
        Assert.Equal(new DateOnly(2020, 1, 1), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Century()
    {
        // 2026 → 2001-01-01 (21st century starts at 2001)
        DataValue result = _function.Execute([
            DataValue.FromString("century"),
            DataValue.FromDate(new DateOnly(2026, 6, 15))
        ]);
        Assert.Equal(new DateOnly(2001, 1, 1), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Century_Year2000()
    {
        // 2000 → 1901-01-01 (20th century)
        DataValue result = _function.Execute([
            DataValue.FromString("century"),
            DataValue.FromDate(new DateOnly(2000, 12, 31))
        ]);
        Assert.Equal(new DateOnly(1901, 1, 1), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Millennium()
    {
        // 2026 → 2001-01-01 (3rd millennium starts at 2001)
        DataValue result = _function.Execute([
            DataValue.FromString("millennium"),
            DataValue.FromDate(new DateOnly(2026, 6, 15))
        ]);
        Assert.Equal(new DateOnly(2001, 1, 1), result.AsDate());
    }

    [Fact]
    public void DateTrunc_Millennium_Year2000()
    {
        // 2000 → 1001-01-01 (2nd millennium)
        DataValue result = _function.Execute([
            DataValue.FromString("millennium"),
            DataValue.FromDate(new DateOnly(2000, 12, 31))
        ]);
        Assert.Equal(new DateOnly(1001, 1, 1), result.AsDate());
    }

    // ───────────────────── validation ─────────────────────

    [Fact]
    public void ValidateArguments_RejectsWrongCount()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void ValidateArguments_RejectsNonStringPart()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Float32, DataKind.Date]));
    }

    [Fact]
    public void ValidateArguments_RejectsNonTemporalDate()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void ValidateArguments_ReturnsInputKind()
    {
        Assert.Equal(DataKind.Date, _function.ValidateArguments([DataKind.String, DataKind.Date]));
        Assert.Equal(DataKind.DateTime, _function.ValidateArguments([DataKind.String, DataKind.DateTime]));
    }
}
