using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for Time-related scalar functions:
/// <see cref="MakeTimeFunction"/>, <see cref="CurrentTimeFunction"/>,
/// and the Time-input paths of <see cref="HourFunction"/>, <see cref="MinuteFunction"/>,
/// <see cref="SecondFunction"/>, <see cref="TimeDiffFunction"/>.
/// Also covers Time cast paths in <see cref="CastFunction"/>.
/// </summary>
public class TimeFunctionTests
{
    // ───────────────── MakeTimeFunction ─────────────────

    [Fact]
    public void MakeTime_CreatesExpectedTime()
    {
        MakeTimeFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromScalar(14),
            DataValue.FromScalar(30),
            DataValue.FromScalar(45),
        ]);

        Assert.Equal(DataKind.Time, result.Kind);
        Assert.Equal(new TimeOnly(14, 30, 45), result.AsTime());
    }

    [Fact]
    public void MakeTime_Midnight()
    {
        MakeTimeFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromScalar(0),
            DataValue.FromScalar(0),
            DataValue.FromScalar(0),
        ]);

        Assert.Equal(new TimeOnly(0, 0, 0), result.AsTime());
    }

    [Fact]
    public void MakeTime_NullArgReturnsNull()
    {
        MakeTimeFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromScalar(10),
            DataValue.Null(DataKind.Scalar),
            DataValue.FromScalar(30),
        ]);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Time, result.Kind);
    }

    [Fact]
    public void MakeTime_ValidateArguments_ReturnsTime()
    {
        MakeTimeFunction function = new();
        DataKind result = function.ValidateArguments([DataKind.Scalar, DataKind.Scalar, DataKind.Scalar]);
        Assert.Equal(DataKind.Time, result);
    }

    [Fact]
    public void MakeTime_ValidateArguments_WrongCount_Throws()
    {
        MakeTimeFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar, DataKind.Scalar]));
    }

    [Fact]
    public void MakeTime_ValidateArguments_WrongType_Throws()
    {
        MakeTimeFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar, DataKind.String, DataKind.Scalar]));
    }

    // ───────────────── CurrentTimeFunction ─────────────────

    [Fact]
    public void CurrentTime_ReturnsTimeKind()
    {
        CurrentTimeFunction function = new();
        DataValue result = function.Execute([]);

        Assert.Equal(DataKind.Time, result.Kind);
        Assert.False(result.IsNull);
    }

    [Fact]
    public void CurrentTime_ValidateArguments_ReturnsTime()
    {
        CurrentTimeFunction function = new();
        DataKind kind = function.ValidateArguments([]);
        Assert.Equal(DataKind.Time, kind);
    }

    [Fact]
    public void CurrentTime_ValidateArguments_WithArgs_Throws()
    {
        CurrentTimeFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar]));
    }

    // ───────────────── Hour/Minute/Second with Time input ─────────────────

    [Fact]
    public void Hour_WithTimeInput_ReturnsHour()
    {
        HourFunction function = new();
        DataValue result = function.Execute([DataValue.FromTime(new TimeOnly(14, 30, 0))]);
        Assert.Equal(14f, result.AsScalar());
    }

    [Fact]
    public void Minute_WithTimeInput_ReturnsMinute()
    {
        MinuteFunction function = new();
        DataValue result = function.Execute([DataValue.FromTime(new TimeOnly(14, 30, 0))]);
        Assert.Equal(30f, result.AsScalar());
    }

    [Fact]
    public void Second_WithTimeInput_ReturnsSecond()
    {
        SecondFunction function = new();
        DataValue result = function.Execute([DataValue.FromTime(new TimeOnly(14, 30, 45))]);
        Assert.Equal(45f, result.AsScalar());
    }

    [Fact]
    public void Hour_ValidateArguments_AcceptsTime()
    {
        HourFunction function = new();
        DataKind kind = function.ValidateArguments([DataKind.Time]);
        Assert.Equal(DataKind.Scalar, kind);
    }

    [Fact]
    public void Minute_ValidateArguments_AcceptsTime()
    {
        MinuteFunction function = new();
        DataKind kind = function.ValidateArguments([DataKind.Time]);
        Assert.Equal(DataKind.Scalar, kind);
    }

    [Fact]
    public void Second_ValidateArguments_AcceptsTime()
    {
        SecondFunction function = new();
        DataKind kind = function.ValidateArguments([DataKind.Time]);
        Assert.Equal(DataKind.Scalar, kind);
    }

    [Fact]
    public void Hour_WithNullTime_ReturnsNull()
    {
        HourFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Time)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── TimeDiffFunction ─────────────────

    [Fact]
    public void TimeDiff_ReturnsExpectedDuration()
    {
        TimeDiffFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromTime(new TimeOnly(10, 0, 0)),
            DataValue.FromTime(new TimeOnly(12, 30, 0)),
        ]);

        Assert.Equal(DataKind.Duration, result.Kind);
        Assert.Equal(TimeSpan.FromHours(2.5), result.AsDuration());
    }

    [Fact]
    public void TimeDiff_WrapsAroundMidnight()
    {
        TimeDiffFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromTime(new TimeOnly(15, 0, 0)),
            DataValue.FromTime(new TimeOnly(10, 0, 0)),
        ]);

        // TimeOnly subtraction wraps forward through midnight: 15:00→10:00 = 19 hours.
        Assert.Equal(TimeSpan.FromHours(19), result.AsDuration());
    }

    [Fact]
    public void TimeDiff_NullReturnsNull()
    {
        TimeDiffFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromTime(new TimeOnly(10, 0, 0)),
            DataValue.Null(DataKind.Time),
        ]);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Duration, result.Kind);
    }

    [Fact]
    public void TimeDiff_ValidateArguments_WrongType_Throws()
    {
        TimeDiffFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.DateTime, DataKind.Time]));
    }

    // ───────────────── Cast paths ─────────────────

    [Fact]
    public void Cast_StringToTime()
    {
        CastFunction cast = new();
        DataValue result = cast.Execute(
        [
            DataValue.FromString("14:30:00"),
            DataValue.FromString("time"),
        ]);

        Assert.Equal(DataKind.Time, result.Kind);
        Assert.Equal(new TimeOnly(14, 30, 0), result.AsTime());
    }

    [Fact]
    public void Cast_TimeToString()
    {
        CastFunction cast = new();
        DataValue result = cast.Execute(
        [
            DataValue.FromTime(new TimeOnly(14, 30, 0)),
            DataValue.FromString("String"),
        ]);

        Assert.Equal("14:30:00", result.AsString());
    }

    [Fact]
    public void Cast_DateTimeToTime()
    {
        CastFunction cast = new();
        DateTimeOffset dateTime = new(2024, 6, 15, 14, 30, 45, TimeSpan.Zero);
        DataValue result = cast.Execute(
        [
            DataValue.FromDateTime(dateTime),
            DataValue.FromString("time"),
        ]);

        Assert.Equal(DataKind.Time, result.Kind);
        Assert.Equal(new TimeOnly(14, 30, 45), result.AsTime());
    }

    [Fact]
    public void Cast_TimeToScalar_SecondsSinceMidnight()
    {
        CastFunction cast = new();
        DataValue result = cast.Execute(
        [
            DataValue.FromTime(new TimeOnly(1, 0, 0)),
            DataValue.FromString("Scalar"),
        ]);

        Assert.Equal(3600f, result.AsScalar());
    }

    [Fact]
    public void Cast_ScalarToTime()
    {
        CastFunction cast = new();
        DataValue result = cast.Execute(
        [
            DataValue.FromScalar(3600f),
            DataValue.FromString("time"),
        ]);

        Assert.Equal(new TimeOnly(1, 0, 0), result.AsTime());
    }

    [Fact]
    public void Cast_NullTimePreservesKind()
    {
        CastFunction cast = new();
        DataValue result = cast.Execute(
        [
            DataValue.Null(DataKind.String),
            DataValue.FromString("time"),
        ]);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Time, result.Kind);
    }
}
