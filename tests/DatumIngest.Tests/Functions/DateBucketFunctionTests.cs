using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="DateBucketFunction"/>.
/// </summary>
public class DateBucketFunctionTests : ServiceTestBase
{
    private readonly DateBucketFunction _function = new();

    [Fact]
    public void Name_IsDateBucket()
    {
        Assert.Equal("date_bucket", _function.Name);
    }

    [Fact]
    public void DateBucket_15MinuteIntervals()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("minute"),
            DataValue.FromFloat32(15),
            DataValue.FromDateTime(new DateTimeOffset(2024, 6, 15, 14, 37, 0, TimeSpan.Zero))
        ]);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 14, 30, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateBucket_HourlyIntervals()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("hour"),
            DataValue.FromFloat32(1),
            DataValue.FromDateTime(new DateTimeOffset(2024, 6, 15, 14, 37, 0, TimeSpan.Zero))
        ]);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 14, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateBucket_DailyBuckets()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromFloat32(7),
            DataValue.FromDate(new DateOnly(2024, 1, 10))
        ]);
        // Default origin: 2000-01-01. Days since origin = 8775. 8775 / 7 = 1253 buckets.
        // 1253 * 7 = 8771 days from origin = 2024-01-06.
        Assert.Equal(new DateOnly(2024, 1, 6), result.AsDate());
    }

    [Fact]
    public void DateBucket_MonthlyBuckets()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("month"),
            DataValue.FromFloat32(3),
            DataValue.FromDate(new DateOnly(2024, 5, 15))
        ]);
        // Months from 2000-01: 292 months. 292 / 3 = 97 * 3 = 291 months = Apr 2024.
        Assert.Equal(new DateOnly(2024, 4, 1), result.AsDate());
    }

    [Fact]
    public void DateBucket_QuarterlyBuckets()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("quarter"),
            DataValue.FromFloat32(1),
            DataValue.FromDate(new DateOnly(2024, 8, 15))
        ]);
        // Quarter = 3 months. Months from origin: 295. 295 / 3 = 98 * 3 = 294 months = Jul 2024.
        Assert.Equal(new DateOnly(2024, 7, 1), result.AsDate());
    }

    [Fact]
    public void DateBucket_YearlyBuckets()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("year"),
            DataValue.FromFloat32(5),
            DataValue.FromDate(new DateOnly(2023, 6, 15))
        ]);
        // Year = 12 months per year, 5-year = 60 months. Months from 2000-01: 281. 281 / 60 = 4 * 60 = 240 months = 2020.
        Assert.Equal(new DateOnly(2020, 1, 1), result.AsDate());
    }

    [Fact]
    public void DateBucket_WithCustomOrigin()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromFloat32(7),
            DataValue.FromDate(new DateOnly(2024, 1, 10)),
            DataValue.FromDate(new DateOnly(2024, 1, 1))
        ]);
        // Days since 2024-01-01: 9. 9 / 7 = 1 * 7 = 7 days = 2024-01-08.
        Assert.Equal(new DateOnly(2024, 1, 8), result.AsDate());
    }

    [Fact]
    public void DateBucket_ExactBoundary()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("minute"),
            DataValue.FromFloat32(15),
            DataValue.FromDateTime(new DateTimeOffset(2024, 6, 15, 14, 30, 0, TimeSpan.Zero))
        ]);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 14, 30, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateBucket_NullDate_ReturnsNull()
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
    public void DateBucket_ZeroWidth_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromFloat32(0),
            DataValue.FromDate(new DateOnly(2024, 1, 1))
        ]));
    }

    [Fact]
    public void DateBucket_NegativeWidth_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.Execute([
            DataValue.FromString("day"),
            DataValue.FromFloat32(-1),
            DataValue.FromDate(new DateOnly(2024, 1, 1))
        ]));
    }

    [Fact]
    public void DateBucket_PreservesDateKind()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("month"),
            DataValue.FromFloat32(1),
            DataValue.FromDate(new DateOnly(2024, 6, 15))
        ]);
        Assert.Equal(DataKind.Date, result.Kind);
    }

    [Fact]
    public void DateBucket_PreservesDateTimeKind()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("minute"),
            DataValue.FromFloat32(15),
            DataValue.FromDateTime(new DateTimeOffset(2024, 6, 15, 14, 37, 0, TimeSpan.Zero))
        ]);
        Assert.Equal(DataKind.DateTime, result.Kind);
    }

    [Fact]
    public void ValidateArguments_AcceptsThreeArguments()
    {
        DataKind result = _function.ValidateArguments([DataKind.String, DataKind.Float32, DataKind.Date]);
        Assert.Equal(DataKind.Date, result);
    }

    [Fact]
    public void ValidateArguments_AcceptsFourArguments()
    {
        DataKind result = _function.ValidateArguments([DataKind.String, DataKind.Float32, DataKind.DateTime, DataKind.DateTime]);
        Assert.Equal(DataKind.DateTime, result);
    }

    [Fact]
    public void ValidateArguments_RejectsTooFewArguments()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String, DataKind.Float32]));
    }

    [Fact]
    public void ValidateArguments_RejectsTooManyArguments()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments(
            [DataKind.String, DataKind.Float32, DataKind.Date, DataKind.Date, DataKind.Date]));
    }
}
