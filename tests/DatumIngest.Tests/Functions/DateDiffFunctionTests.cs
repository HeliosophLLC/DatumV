using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="DateDiffFunction"/>.
/// </summary>
public class DateDiffFunctionTests : ServiceTestBase
{
    private readonly DateDiffFunction _function = new();

    [Fact]
    public void Name_IsDateDiff()
    {
        Assert.Equal("date_diff", _function.Name);
    }

    [Fact]
    public void DateDiff_Days_BetweenDates()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromDate(new DateOnly(2024, 1, 1)),
            DataValue.FromDate(new DateOnly(2024, 1, 31))
        ]);
        Assert.Equal(30f, result.AsFloat32());
    }

    [Fact]
    public void DateDiff_Days_Negative()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromDate(new DateOnly(2024, 1, 31)),
            DataValue.FromDate(new DateOnly(2024, 1, 1))
        ]);
        Assert.Equal(-30f, result.AsFloat32());
    }

    [Fact]
    public void DateDiff_Months_BoundariesCrossed()
    {
        // Jan 31 → Feb 1: one month boundary crossed.
        DataValue result = _function.Execute([
            DataValue.FromString("month"),
            DataValue.FromDate(new DateOnly(2024, 1, 31)),
            DataValue.FromDate(new DateOnly(2024, 2, 1))
        ]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void DateDiff_Years()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("year"),
            DataValue.FromDate(new DateOnly(2020, 6, 15)),
            DataValue.FromDate(new DateOnly(2024, 3, 10))
        ]);
        Assert.Equal(4f, result.AsFloat32());
    }

    [Fact]
    public void DateDiff_Quarters()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("quarter"),
            DataValue.FromDate(new DateOnly(2024, 1, 1)),
            DataValue.FromDate(new DateOnly(2024, 10, 1))
        ]);
        Assert.Equal(3f, result.AsFloat32());
    }

    [Fact]
    public void DateDiff_Hours_BetweenDateTimes()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("hour"),
            DataValue.FromDateTime(new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(new DateTimeOffset(2024, 1, 1, 14, 30, 0, TimeSpan.Zero))
        ]);
        Assert.Equal(4f, result.AsFloat32());
    }

    [Fact]
    public void DateDiff_Minutes()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("minute"),
            DataValue.FromDateTime(new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(new DateTimeOffset(2024, 1, 1, 10, 45, 30, TimeSpan.Zero))
        ]);
        Assert.Equal(45f, result.AsFloat32());
    }

    [Fact]
    public void DateDiff_Seconds()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("second"),
            DataValue.FromDateTime(new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(new DateTimeOffset(2024, 1, 1, 10, 0, 30, TimeSpan.Zero))
        ]);
        Assert.Equal(30f, result.AsFloat32());
    }

    [Fact]
    public void DateDiff_Weeks()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("week"),
            DataValue.FromDate(new DateOnly(2024, 1, 1)),
            DataValue.FromDate(new DateOnly(2024, 1, 22))
        ]);
        Assert.Equal(3f, result.AsFloat32());
    }

    [Fact]
    public void DateDiff_NullStart_ReturnsNull()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.Null(DataKind.Date),
            DataValue.FromDate(new DateOnly(2024, 1, 1))
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public void DateDiff_NullEnd_ReturnsNull()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromDate(new DateOnly(2024, 1, 1)),
            DataValue.Null(DataKind.Date)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void DateDiff_SameDate_ReturnsZero()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromDate(new DateOnly(2024, 6, 15)),
            DataValue.FromDate(new DateOnly(2024, 6, 15))
        ]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void DateDiff_Alias_D()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("d"),
            DataValue.FromDate(new DateOnly(2024, 1, 1)),
            DataValue.FromDate(new DateOnly(2024, 1, 11))
        ]);
        Assert.Equal(10f, result.AsFloat32());
    }

    [Fact]
    public void ValidateArguments_RejectsWrongCount()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String, DataKind.Date]));
    }

    [Fact]
    public void ValidateArguments_RejectsNonStringPart()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Float32, DataKind.Date, DataKind.Date]));
    }

    [Fact]
    public void ValidateArguments_RejectsNonTemporalStart()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String, DataKind.String, DataKind.Date]));
    }

    [Fact]
    public void ValidateArguments_RejectsNonTemporalEnd()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String, DataKind.Date, DataKind.Float32]));
    }
}
