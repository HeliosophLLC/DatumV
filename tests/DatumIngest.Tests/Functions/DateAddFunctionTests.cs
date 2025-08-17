using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="DateAddFunction"/>.
/// </summary>
public class DateAddFunctionTests
{
    private readonly DateAddFunction _function = new();

    [Fact]
    public void Name_IsDateAdd()
    {
        Assert.Equal("date_add", _function.Name);
    }

    [Fact]
    public void DateAdd_Days_ToDate()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromFloat32(10),
            DataValue.FromDate(new DateOnly(2024, 1, 1))
        ]);
        Assert.Equal(DataKind.Date, result.Kind);
        Assert.Equal(new DateOnly(2024, 1, 11), result.AsDate());
    }

    [Fact]
    public void DateAdd_Months_ToDate()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("month"),
            DataValue.FromFloat32(3),
            DataValue.FromDate(new DateOnly(2024, 1, 15))
        ]);
        Assert.Equal(new DateOnly(2024, 4, 15), result.AsDate());
    }

    [Fact]
    public void DateAdd_Years_ToDate()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("year"),
            DataValue.FromFloat32(2),
            DataValue.FromDate(new DateOnly(2024, 6, 15))
        ]);
        Assert.Equal(new DateOnly(2026, 6, 15), result.AsDate());
    }

    [Fact]
    public void DateAdd_Hours_ToDateTime()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("hour"),
            DataValue.FromFloat32(5),
            DataValue.FromDateTime(new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero))
        ]);
        Assert.Equal(DataKind.DateTime, result.Kind);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 15, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateAdd_Minutes_ToDateTime()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("minute"),
            DataValue.FromFloat32(90),
            DataValue.FromDateTime(new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero))
        ]);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 11, 30, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateAdd_Negative_SubtractsDays()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromFloat32(-5),
            DataValue.FromDate(new DateOnly(2024, 1, 10))
        ]);
        Assert.Equal(new DateOnly(2024, 1, 5), result.AsDate());
    }

    [Fact]
    public void DateAdd_Weeks()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("week"),
            DataValue.FromFloat32(2),
            DataValue.FromDate(new DateOnly(2024, 1, 1))
        ]);
        Assert.Equal(new DateOnly(2024, 1, 15), result.AsDate());
    }

    [Fact]
    public void DateAdd_Quarters()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("quarter"),
            DataValue.FromFloat32(2),
            DataValue.FromDate(new DateOnly(2024, 1, 15))
        ]);
        Assert.Equal(new DateOnly(2024, 7, 15), result.AsDate());
    }

    [Fact]
    public void DateAdd_NullDate_ReturnsNull()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromFloat32(1),
            DataValue.Null(DataKind.Date)
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Date, result.Kind);
    }

    [Fact]
    public void DateAdd_NullAmount_ReturnsNull()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.Null(DataKind.Float32),
            DataValue.FromDate(new DateOnly(2024, 1, 1))
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void DateAdd_PreservesDateKind()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromFloat32(1),
            DataValue.FromDate(new DateOnly(2024, 1, 1))
        ]);
        Assert.Equal(DataKind.Date, result.Kind);
    }

    [Fact]
    public void DateAdd_PreservesDateTimeKind()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromFloat32(1),
            DataValue.FromDateTime(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
        ]);
        Assert.Equal(DataKind.DateTime, result.Kind);
    }

    [Fact]
    public void ValidateArguments_ReturnsInputKind()
    {
        DataKind result = _function.ValidateArguments([DataKind.String, DataKind.Float32, DataKind.Date]);
        Assert.Equal(DataKind.Date, result);

        result = _function.ValidateArguments([DataKind.String, DataKind.Float32, DataKind.DateTime]);
        Assert.Equal(DataKind.DateTime, result);
    }

    [Fact]
    public void ValidateArguments_RejectsWrongCount()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String, DataKind.Float32]));
    }

    [Fact]
    public void ValidateArguments_RejectsNonScalarAmount()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String, DataKind.String, DataKind.Date]));
    }
}
