using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="DateBinFunction"/> — PostgreSQL-compatible date_bin().
/// </summary>
public class DateBinFunctionTests : ServiceTestBase
{
    private readonly DateBinFunction _function = new();

    private static readonly DateTimeOffset Origin = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Name_IsDateBin()
    {
        Assert.Equal("date_bin", _function.Name);
    }

    // ───────────────────── Core bucketing ─────────────────────

    [Fact]
    public void DateBin_15Minutes()
    {
        // 00:17 in 15-minute buckets → 00:15
        DataValue result = _function.Execute([
            DataValue.FromString("15 minutes"),
            DataValue.FromDateTime(new DateTimeOffset(2020, 1, 1, 0, 17, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(Origin),
        ]);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 15, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateBin_1Hour()
    {
        // 05:35 in 1-hour buckets → 05:00
        DataValue result = _function.Execute([
            DataValue.FromString("1 hour"),
            DataValue.FromDateTime(new DateTimeOffset(2020, 1, 1, 5, 35, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(Origin),
        ]);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 5, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateBin_1Day()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("1 day"),
            DataValue.FromDateTime(new DateTimeOffset(2020, 1, 15, 12, 30, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(Origin),
        ]);
        Assert.Equal(new DateTimeOffset(2020, 1, 15, 0, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateBin_1Week()
    {
        // 2020-01-15 (Wednesday) with origin 2001-01-01 (Monday)
        // Week bucket should align to Monday 2020-01-13
        DataValue result = _function.Execute([
            DataValue.FromString("1 week"),
            DataValue.FromDateTime(new DateTimeOffset(2020, 1, 15, 0, 0, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(Origin),
        ]);
        Assert.Equal(new DateTimeOffset(2020, 1, 13, 0, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateBin_30Seconds()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("30 seconds"),
            DataValue.FromDateTime(new DateTimeOffset(2020, 1, 1, 0, 0, 45, TimeSpan.Zero)),
            DataValue.FromDateTime(Origin),
        ]);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 30, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateBin_CompoundInterval()
    {
        // 1 day 12 hours = 36-hour buckets
        DataValue result = _function.Execute([
            DataValue.FromString("1 day 12 hours"),
            DataValue.FromDateTime(new DateTimeOffset(2020, 1, 3, 10, 0, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        ]);
        // Day 1 00:00, Day 2 12:00, Day 4 00:00 → 10:00 on day 3 falls in bucket starting day 2 12:00
        Assert.Equal(new DateTimeOffset(2020, 1, 2, 12, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    // ───────────────────── Kind preservation ─────────────────────

    [Fact]
    public void DateBin_PreservesDateTimeKind()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("1 day"),
            DataValue.FromDateTime(new DateTimeOffset(2020, 6, 15, 12, 0, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(Origin),
        ]);
        Assert.Equal(DataKind.DateTime, result.Kind);
    }

    [Fact]
    public void DateBin_PreservesDateKind()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("7 days"),
            DataValue.FromDate(new DateOnly(2020, 6, 15)),
            DataValue.FromDate(new DateOnly(2001, 1, 1)),
        ]);
        Assert.Equal(DataKind.Date, result.Kind);
    }

    // ───────────────────── Null handling ─────────────────────

    [Fact]
    public void DateBin_NullSource_ReturnsTypedNull()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("1 hour"),
            DataValue.Null(DataKind.DateTime),
            DataValue.FromDateTime(Origin),
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.DateTime, result.Kind);
    }

    // ───────────────────── Edge: source equals origin ─────────────────────

    [Fact]
    public void DateBin_SourceEqualsOrigin_ReturnOrigin()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("15 minutes"),
            DataValue.FromDateTime(Origin),
            DataValue.FromDateTime(Origin),
        ]);
        Assert.Equal(Origin, result.AsDateTime());
    }

    // ───────────────────── Validation ─────────────────────

    [Fact]
    public void ValidateArguments_RequiresThreeArgs()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String, DataKind.DateTime]));
    }

    [Fact]
    public void ValidateArguments_RejectsNonStringInterval()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Float32, DataKind.DateTime, DataKind.DateTime]));
    }

    [Fact]
    public void ValidateArguments_RejectsNonTemporalSource()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String, DataKind.String, DataKind.DateTime]));
    }

    [Fact]
    public void ValidateArguments_RejectsNonTemporalOrigin()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String, DataKind.DateTime, DataKind.String]));
    }

    [Fact]
    public void DateBin_ZeroInterval_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.Execute([
            DataValue.FromString("0 minutes"),
            DataValue.FromDateTime(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(Origin),
        ]));
    }

    [Fact]
    public void DateBin_MonthInterval_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.Execute([
            DataValue.FromString("1 month"),
            DataValue.FromDateTime(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(Origin),
        ]));
    }
}
