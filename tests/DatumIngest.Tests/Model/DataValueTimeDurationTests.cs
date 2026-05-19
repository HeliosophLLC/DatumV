using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Model;

/// <summary>
/// Tests for <see cref="DataValue"/> Time and Duration factory methods,
/// accessors, equality, hashing, and display formatting.
/// </summary>
public class DataValueTimeDurationTests : ServiceTestBase
{
    // ───────────────── FromTime / AsTime ─────────────────

    [Fact]
    public void FromTime_RoundTrips()
    {
        TimeOnly time = new(14, 30, 45);
        DataValue value = DataValue.FromTime(time);

        Assert.Equal(DataKind.Time, value.Kind);
        Assert.False(value.IsNull);
        Assert.Equal(time, value.AsTime());
    }

    [Fact]
    public void FromTime_MidnightRoundTrips()
    {
        TimeOnly midnight = TimeOnly.MinValue;
        DataValue value = DataValue.FromTime(midnight);
        Assert.Equal(midnight, value.AsTime());
    }

    [Fact]
    public void FromTime_WithMillisecondsRoundTrips()
    {
        TimeOnly time = new(10, 20, 30, 456);
        DataValue value = DataValue.FromTime(time);
        Assert.Equal(time, value.AsTime());
    }

    [Fact]
    public void AsTime_WrongKindThrows()
    {
        DataValue scalar = DataValue.FromFloat32(42f);
        Assert.Throws<InvalidOperationException>(() => scalar.AsTime());
    }

    [Fact]
    public void AsTime_NullThrows()
    {
        DataValue nullTime = DataValue.Null(DataKind.Time);
        Assert.True(nullTime.IsNull);
        Assert.Throws<InvalidOperationException>(() => nullTime.AsTime());
    }

    // ───────────────── FromDuration / AsDuration ─────────────────

    [Fact]
    public void FromDuration_RoundTrips()
    {
        TimeSpan duration = new(1, 2, 3, 4);
        DataValue value = DataValue.FromDuration(duration);

        Assert.Equal(DataKind.Duration, value.Kind);
        Assert.False(value.IsNull);
        Assert.Equal(duration, value.AsDuration());
    }

    [Fact]
    public void FromDuration_ZeroRoundTrips()
    {
        DataValue value = DataValue.FromDuration(TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, value.AsDuration());
    }

    [Fact]
    public void FromDuration_NegativeRoundTrips()
    {
        TimeSpan negative = TimeSpan.FromHours(-5);
        DataValue value = DataValue.FromDuration(negative);
        Assert.Equal(negative, value.AsDuration());
    }

    [Fact]
    public void AsDuration_WrongKindThrows()
    {
        DataValue str = DataValue.FromString("hello");
        Assert.Throws<InvalidOperationException>(() => str.AsDuration());
    }

    [Fact]
    public void AsDuration_NullThrows()
    {
        DataValue nullDuration = DataValue.Null(DataKind.Duration);
        Assert.True(nullDuration.IsNull);
        Assert.Throws<InvalidOperationException>(() => nullDuration.AsDuration());
    }

    // ───────────────── Equality ─────────────────

    [Fact]
    public void Time_EqualValuesAreEqual()
    {
        DataValue a = DataValue.FromTime(new TimeOnly(8, 30, 0));
        DataValue b = DataValue.FromTime(new TimeOnly(8, 30, 0));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Time_DifferentValuesAreNotEqual()
    {
        DataValue a = DataValue.FromTime(new TimeOnly(8, 30, 0));
        DataValue b = DataValue.FromTime(new TimeOnly(9, 0, 0));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Duration_EqualValuesAreEqual()
    {
        DataValue a = DataValue.FromDuration(TimeSpan.FromMinutes(90));
        DataValue b = DataValue.FromDuration(TimeSpan.FromMinutes(90));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Duration_DifferentValuesAreNotEqual()
    {
        DataValue a = DataValue.FromDuration(TimeSpan.FromHours(1));
        DataValue b = DataValue.FromDuration(TimeSpan.FromHours(2));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Time_NullsAreEqual()
    {
        DataValue a = DataValue.Null(DataKind.Time);
        DataValue b = DataValue.Null(DataKind.Time);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Duration_NullsAreEqual()
    {
        DataValue a = DataValue.Null(DataKind.Duration);
        DataValue b = DataValue.Null(DataKind.Duration);
        Assert.Equal(a, b);
    }

    // ───────────────── GetHashCode ─────────────────

    [Fact]
    public void Time_EqualValuesHaveSameHashCode()
    {
        DataValue a = DataValue.FromTime(new TimeOnly(12, 0, 0));
        DataValue b = DataValue.FromTime(new TimeOnly(12, 0, 0));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Duration_EqualValuesHaveSameHashCode()
    {
        DataValue a = DataValue.FromDuration(TimeSpan.FromSeconds(3600));
        DataValue b = DataValue.FromDuration(TimeSpan.FromSeconds(3600));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ───────────────── ToString ─────────────────

    [Fact]
    public void Time_ToStringFormatsCorrectly()
    {
        DataValue value = DataValue.FromTime(new TimeOnly(14, 30, 0));
        Assert.Equal("14:30:00", value.ToString());
    }

    [Fact]
    public void Time_ToStringWithMilliseconds()
    {
        DataValue value = DataValue.FromTime(new TimeOnly(10, 20, 30, 456));
        Assert.Contains("10:20:30.456", value.ToString());
    }

    [Fact]
    public void Duration_ToStringUsesConstantFormat()
    {
        DataValue value = DataValue.FromDuration(new TimeSpan(1, 2, 3, 4));
        string result = value.ToString();
        Assert.Equal("1.02:03:04", result);
    }

    [Fact]
    public void Duration_NegativeToString()
    {
        DataValue value = DataValue.FromDuration(TimeSpan.FromHours(-5));
        string result = value.ToString();
        Assert.StartsWith("-", result);
    }

    [Fact]
    public void Time_NullToString()
    {
        DataValue value = DataValue.Null(DataKind.Time);
        Assert.Equal("NULL(Time)", value.ToString());
    }

    [Fact]
    public void Duration_NullToString()
    {
        DataValue value = DataValue.Null(DataKind.Duration);
        Assert.Equal("NULL(Duration)", value.ToString());
    }
}
