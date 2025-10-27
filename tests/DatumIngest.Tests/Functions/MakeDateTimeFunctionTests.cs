using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="MakeDateFunction"/> and <see cref="MakeTimestampFunction"/>.
/// </summary>
public class MakeDateTimeFunctionTests : ServiceTestBase
{
    // ───────────────────── make_date() ─────────────────────

    [Fact]
    public void MakeDate_Name()
    {
        MakeDateFunction function = new();
        Assert.Equal("make_date", function.Name);
    }

    [Fact]
    public void MakeDate_ConstructsDate()
    {
        MakeDateFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(2024),
            DataValue.FromFloat32(6),
            DataValue.FromFloat32(15)
        ]);
        Assert.Equal(DataKind.Date, result.Kind);
        Assert.Equal(new DateOnly(2024, 6, 15), result.AsDate());
    }

    [Fact]
    public void MakeDate_LeapYear()
    {
        MakeDateFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(2024),
            DataValue.FromFloat32(2),
            DataValue.FromFloat32(29)
        ]);
        Assert.Equal(new DateOnly(2024, 2, 29), result.AsDate());
    }

    [Fact]
    public void MakeDate_NullYear_ReturnsNull()
    {
        MakeDateFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Float32),
            DataValue.FromFloat32(6),
            DataValue.FromFloat32(15)
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Date, result.Kind);
    }

    [Fact]
    public void MakeDate_NullMonth_ReturnsNull()
    {
        MakeDateFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(2024),
            DataValue.Null(DataKind.Float32),
            DataValue.FromFloat32(15)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void MakeDate_NullDay_ReturnsNull()
    {
        MakeDateFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(2024),
            DataValue.FromFloat32(6),
            DataValue.Null(DataKind.Float32)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void MakeDate_ValidateArguments_RejectsWrongCount()
    {
        MakeDateFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void MakeDate_ValidateArguments_RejectsNonInteger()
    {
        MakeDateFunction function = new();
        Assert.Throws<FunctionArgumentException>(() => function.ValidateArguments([DataKind.String, DataKind.Int32, DataKind.Int32]));
    }

    [Fact]
    public void MakeDate_ValidateArguments_ReturnsDateKind()
    {
        MakeDateFunction function = new();
        DataKind result = function.ValidateArguments([DataKind.Int32, DataKind.Int32, DataKind.Int32]);
        Assert.Equal(DataKind.Date, result);
    }

    // ───────────────────── make_timestamp() ─────────────────────

    [Fact]
    public void MakeTimestamp_Name()
    {
        MakeTimestampFunction function = new();
        Assert.Equal("make_timestamp", function.Name);
    }

    [Fact]
    public void MakeTimestamp_ConstructsDateTime()
    {
        MakeTimestampFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(2024),
            DataValue.FromFloat32(6),
            DataValue.FromFloat32(15),
            DataValue.FromFloat32(10),
            DataValue.FromFloat32(30),
            DataValue.FromFloat32(45)
        ]);
        Assert.Equal(DataKind.DateTime, result.Kind);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 10, 30, 45, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void MakeTimestamp_Midnight()
    {
        MakeTimestampFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(2024),
            DataValue.FromFloat32(1),
            DataValue.FromFloat32(1),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(0)
        ]);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void MakeTimestamp_NullArgument_ReturnsNull()
    {
        MakeTimestampFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(2024),
            DataValue.FromFloat32(6),
            DataValue.FromFloat32(15),
            DataValue.Null(DataKind.Float32),
            DataValue.FromFloat32(30),
            DataValue.FromFloat32(0)
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.DateTime, result.Kind);
    }

    [Fact]
    public void MakeTimestamp_ValidateArguments_RejectsWrongCount()
    {
        MakeTimestampFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([
            DataKind.Float32, DataKind.Float32, DataKind.Float32, DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void MakeTimestamp_ValidateArguments_RejectsNonInteger()
    {
        MakeTimestampFunction function = new();
        // minute (arg 5) is String — should reject
        Assert.Throws<FunctionArgumentException>(() => function.ValidateArguments([
            DataKind.Int32, DataKind.Int32, DataKind.Int32,
            DataKind.Int32, DataKind.String, DataKind.Float64]));
    }

    [Fact]
    public void MakeTimestamp_ValidateArguments_ReturnsDateTimeKind()
    {
        MakeTimestampFunction function = new();
        // First 5 args are integer, last (second) is numeric (Float64 for fractional seconds)
        DataKind result = function.ValidateArguments([
            DataKind.Int32, DataKind.Int32, DataKind.Int32,
            DataKind.Int32, DataKind.Int32, DataKind.Float64]);
        Assert.Equal(DataKind.DateTime, result);
    }
}
