using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for Duration-related scalar functions:
/// <see cref="MakeDurationFunction"/>, <see cref="DurationSecondsFunction"/>,
/// <see cref="DurationMinutesFunction"/>, <see cref="DurationHoursFunction"/>,
/// <see cref="DurationDaysFunction"/>, <see cref="DateSpanFunction"/>,
/// <see cref="DateOffsetFunction"/>.
/// Also covers Duration cast paths and Duration → Scalar widening.
/// </summary>
public class DurationFunctionTests : ServiceTestBase
{
    // ───────────────── MakeDurationFunction ─────────────────

    [Fact]
    public void MakeDuration_CreatesExpectedDuration()
    {
        MakeDurationFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromFloat32(1),
            DataValue.FromFloat32(2),
            DataValue.FromFloat32(3),
            DataValue.FromFloat32(4),
        ]);

        Assert.Equal(DataKind.Duration, result.Kind);
        Assert.Equal(new TimeSpan(1, 2, 3, 4), result.AsDuration());
    }

    [Fact]
    public void MakeDuration_Zero()
    {
        MakeDurationFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(0),
        ]);

        Assert.Equal(TimeSpan.Zero, result.AsDuration());
    }

    [Fact]
    public void MakeDuration_NullArgReturnsNull()
    {
        MakeDurationFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromFloat32(1),
            DataValue.Null(DataKind.Float32),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(0),
        ]);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Duration, result.Kind);
    }

    [Fact]
    public void MakeDuration_ValidateArguments_ReturnsDuration()
    {
        MakeDurationFunction function = new();
        DataKind result = function.ValidateArguments([DataKind.Float32, DataKind.Float32, DataKind.Float32, DataKind.Float32]);
        Assert.Equal(DataKind.Duration, result);
    }

    [Fact]
    public void MakeDuration_ValidateArguments_WrongCount_Throws()
    {
        MakeDurationFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void MakeDuration_ValidateArguments_WrongType_Throws()
    {
        MakeDurationFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.String, DataKind.Float32, DataKind.Float32]));
    }

    // ───────────────── DurationSecondsFunction ─────────────────

    [Fact]
    public void DurationSeconds_ReturnsTotalSeconds()
    {
        DurationSecondsFunction function = new();
        TimeSpan duration = new(0, 1, 30, 0);
        DataValue result = function.Execute([DataValue.FromDuration(duration)]);

        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.Equal((float)duration.TotalSeconds, result.AsFloat32());
    }

    [Fact]
    public void DurationSeconds_Null()
    {
        DurationSecondsFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Duration)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void DurationSeconds_ValidateArguments_WrongType_Throws()
    {
        DurationSecondsFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32]));
    }

    // ───────────────── DurationMinutesFunction ─────────────────

    [Fact]
    public void DurationMinutes_ReturnsTotalMinutes()
    {
        DurationMinutesFunction function = new();
        TimeSpan duration = TimeSpan.FromHours(2);
        DataValue result = function.Execute([DataValue.FromDuration(duration)]);

        Assert.Equal(120f, result.AsFloat32());
    }

    [Fact]
    public void DurationMinutes_Null()
    {
        DurationMinutesFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Duration)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── DurationHoursFunction ─────────────────

    [Fact]
    public void DurationHours_ReturnsTotalHours()
    {
        DurationHoursFunction function = new();
        TimeSpan duration = TimeSpan.FromDays(1);
        DataValue result = function.Execute([DataValue.FromDuration(duration)]);

        Assert.Equal(24f, result.AsFloat32());
    }

    [Fact]
    public void DurationHours_Null()
    {
        DurationHoursFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Duration)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── DurationDaysFunction ─────────────────

    [Fact]
    public void DurationDays_ReturnsTotalDays()
    {
        DurationDaysFunction function = new();
        TimeSpan duration = TimeSpan.FromHours(48);
        DataValue result = function.Execute([DataValue.FromDuration(duration)]);

        Assert.Equal(2f, result.AsFloat32());
    }

    [Fact]
    public void DurationDays_FractionalDays()
    {
        DurationDaysFunction function = new();
        TimeSpan duration = TimeSpan.FromHours(36);
        DataValue result = function.Execute([DataValue.FromDuration(duration)]);

        Assert.Equal(1.5f, result.AsFloat32());
    }

    [Fact]
    public void DurationDays_Null()
    {
        DurationDaysFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Duration)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── DateSpanFunction ─────────────────

    [Fact]
    public void DateSpan_BetweenDates()
    {
        DateSpanFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromDate(new DateOnly(2024, 1, 1)),
            DataValue.FromDate(new DateOnly(2024, 1, 11)),
        ]);

        Assert.Equal(DataKind.Duration, result.Kind);
        Assert.Equal(TimeSpan.FromDays(10), result.AsDuration());
    }

    [Fact]
    public void DateSpan_BetweenDateTimes()
    {
        DateSpanFunction function = new();
        DateTimeOffset start = new(2024, 6, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = new(2024, 6, 1, 12, 30, 0, TimeSpan.Zero);
        DataValue result = function.Execute(
        [
            DataValue.FromDateTime(start),
            DataValue.FromDateTime(end),
        ]);

        Assert.Equal(TimeSpan.FromHours(2.5), result.AsDuration());
    }

    [Fact]
    public void DateSpan_MixedDateAndDateTime()
    {
        DateSpanFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromDate(new DateOnly(2024, 1, 1)),
            DataValue.FromDateTime(new DateTimeOffset(2024, 1, 2, 12, 0, 0, TimeSpan.Zero)),
        ]);

        Assert.Equal(TimeSpan.FromHours(36), result.AsDuration());
    }

    [Fact]
    public void DateSpan_NegativeSpan()
    {
        DateSpanFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromDate(new DateOnly(2024, 6, 15)),
            DataValue.FromDate(new DateOnly(2024, 6, 10)),
        ]);

        Assert.True(result.AsDuration() < TimeSpan.Zero);
    }

    [Fact]
    public void DateSpan_Null()
    {
        DateSpanFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.Null(DataKind.Date),
            DataValue.FromDate(new DateOnly(2024, 1, 1)),
        ]);

        Assert.True(result.IsNull);
    }

    [Fact]
    public void DateSpan_ValidateArguments_WrongType_Throws()
    {
        DateSpanFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String, DataKind.Date]));
    }

    // ───────────────── DateOffsetFunction ─────────────────

    [Fact]
    public void DateOffset_AddsToDate()
    {
        DateOffsetFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromDate(new DateOnly(2024, 1, 1)),
            DataValue.FromDuration(TimeSpan.FromDays(10)),
        ]);

        Assert.Equal(DataKind.DateTime, result.Kind);
        Assert.Equal(new DateTimeOffset(2024, 1, 11, 0, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateOffset_AddsToDateTime()
    {
        DateOffsetFunction function = new();
        DateTimeOffset start = new(2024, 6, 1, 10, 0, 0, TimeSpan.Zero);
        DataValue result = function.Execute(
        [
            DataValue.FromDateTime(start),
            DataValue.FromDuration(TimeSpan.FromHours(2.5)),
        ]);

        Assert.Equal(new DateTimeOffset(2024, 6, 1, 12, 30, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateOffset_NegativeDuration()
    {
        DateOffsetFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromDate(new DateOnly(2024, 6, 15)),
            DataValue.FromDuration(TimeSpan.FromDays(-5)),
        ]);

        Assert.Equal(new DateTimeOffset(2024, 6, 10, 0, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void DateOffset_Null()
    {
        DateOffsetFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromDate(new DateOnly(2024, 1, 1)),
            DataValue.Null(DataKind.Duration),
        ]);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.DateTime, result.Kind);
    }

    [Fact]
    public void DateOffset_ValidateArguments_WrongDurationType_Throws()
    {
        DateOffsetFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Date, DataKind.Float32]));
    }

    // ───────────────── Cast paths ─────────────────

    [Fact]
    public void Cast_StringToDuration()
    {
        CastFunction cast = new();
        DataValue result = cast.Execute(
        [
            DataValue.FromString("1.02:03:04"),
            DataValue.FromString("duration"),
        ]);

        Assert.Equal(DataKind.Duration, result.Kind);
        Assert.Equal(new TimeSpan(1, 2, 3, 4), result.AsDuration());
    }

    [Fact]
    public void Cast_DurationToString()
    {
        CastFunction cast = new();
        DataValue result = cast.Execute(
        [
            DataValue.FromDuration(new TimeSpan(1, 2, 3, 4)),
            DataValue.FromString("String"),
        ]);

        Assert.Equal("1.02:03:04", result.AsString());
    }

    [Fact]
    public void Cast_DurationToScalar_TotalSeconds()
    {
        CastFunction cast = new();
        DataValue result = cast.Execute(
        [
            DataValue.FromDuration(TimeSpan.FromMinutes(90)),
            DataValue.FromString("Float32"),
        ]);

        Assert.Equal(5400f, result.AsFloat32());
    }

    [Fact]
    public void Cast_ScalarToDuration()
    {
        CastFunction cast = new();
        DataValue result = cast.Execute(
        [
            DataValue.FromFloat32(3600f),
            DataValue.FromString("duration"),
        ]);

        Assert.Equal(TimeSpan.FromHours(1), result.AsDuration());
    }

    [Fact]
    public void Cast_NullDurationPreservesKind()
    {
        CastFunction cast = new();
        DataValue result = cast.Execute(
        [
            DataValue.Null(DataKind.String),
            DataValue.FromString("duration"),
        ]);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Duration, result.Kind);
    }

    // ───────────────── Duration → Scalar widening ─────────────────

    [Fact]
    public void TypeCoercion_DurationWidensToFloat64()
    {
        Assert.True(TypeCoercion.CanWiden(DataKind.Duration, DataKind.Float64));
    }

    [Fact]
    public void TypeCoercion_DurationWidensToVector()
    {
        Assert.True(TypeCoercion.CanWiden(DataKind.Duration, DataKind.Vector));
    }

    [Fact]
    public void TypeCoercion_DurationWidenValue_ProducesTotalSeconds()
    {
        DataValue duration = DataValue.FromDuration(TimeSpan.FromMinutes(2));
        DataValue widened = TypeCoercion.Widen(duration, DataKind.Float64);

        Assert.Equal(DataKind.Float64, widened.Kind);
        Assert.Equal(120.0, widened.AsFloat64());
    }

    [Fact]
    public void TypeCoercion_TimeDoesNotWiden()
    {
        Assert.False(TypeCoercion.CanWiden(DataKind.Time, DataKind.Float32));
    }

    [Fact]
    public void TypeCoercion_FindCommonKind_DurationAndFloat32()
    {
        DataKind? common = TypeCoercion.FindCommonKind(DataKind.Duration, DataKind.Float32);
        Assert.Equal(DataKind.Float64, common);
    }

    // ───────────────── DateOffset with Time ─────────────────

    [Fact]
    public void DateOffset_AddsToTime()
    {
        DateOffsetFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromTime(new TimeOnly(14, 0, 0)),
            DataValue.FromDuration(TimeSpan.FromMinutes(90)),
        ]);

        Assert.Equal(DataKind.Time, result.Kind);
        Assert.Equal(new TimeOnly(15, 30, 0), result.AsTime());
    }

    [Fact]
    public void DateOffset_Time_WrapsAroundMidnight()
    {
        DateOffsetFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromTime(new TimeOnly(23, 0, 0)),
            DataValue.FromDuration(TimeSpan.FromHours(3)),
        ]);

        Assert.Equal(new TimeOnly(2, 0, 0), result.AsTime());
    }

    [Fact]
    public void DateOffset_Time_NullReturnsNullTime()
    {
        DateOffsetFunction function = new();
        DataValue result = function.Execute(
        [
            DataValue.FromTime(new TimeOnly(10, 0, 0)),
            DataValue.Null(DataKind.Duration),
        ]);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Time, result.Kind);
    }

    [Fact]
    public void DateOffset_ValidateArguments_AcceptsTime()
    {
        DateOffsetFunction function = new();
        DataKind returnKind = function.ValidateArguments([DataKind.Time, DataKind.Duration]);
        Assert.Equal(DataKind.Time, returnKind);
    }
}
