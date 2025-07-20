using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="MakeDateFunction"/> and <see cref="MakeTimestampFunction"/>.
/// </summary>
public class MakeDateTimeFunctionTests
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
            DataValue.FromScalar(2024),
            DataValue.FromScalar(6),
            DataValue.FromScalar(15)
        ]);
        Assert.Equal(DataKind.Date, result.Kind);
        Assert.Equal(new DateOnly(2024, 6, 15), result.AsDate());
    }

    [Fact]
    public void MakeDate_LeapYear()
    {
        MakeDateFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(2024),
            DataValue.FromScalar(2),
            DataValue.FromScalar(29)
        ]);
        Assert.Equal(new DateOnly(2024, 2, 29), result.AsDate());
    }

    [Fact]
    public void MakeDate_NullYear_ReturnsNull()
    {
        MakeDateFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Scalar),
            DataValue.FromScalar(6),
            DataValue.FromScalar(15)
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Date, result.Kind);
    }

    [Fact]
    public void MakeDate_NullMonth_ReturnsNull()
    {
        MakeDateFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(2024),
            DataValue.Null(DataKind.Scalar),
            DataValue.FromScalar(15)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void MakeDate_NullDay_ReturnsNull()
    {
        MakeDateFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(2024),
            DataValue.FromScalar(6),
            DataValue.Null(DataKind.Scalar)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void MakeDate_ValidateArguments_RejectsWrongCount()
    {
        MakeDateFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Scalar, DataKind.Scalar]));
    }

    [Fact]
    public void MakeDate_ValidateArguments_RejectsNonScalar()
    {
        MakeDateFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.String, DataKind.Scalar, DataKind.Scalar]));
    }

    [Fact]
    public void MakeDate_ValidateArguments_ReturnsDateKind()
    {
        MakeDateFunction function = new();
        DataKind result = function.ValidateArguments([DataKind.Scalar, DataKind.Scalar, DataKind.Scalar]);
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
            DataValue.FromScalar(2024),
            DataValue.FromScalar(6),
            DataValue.FromScalar(15),
            DataValue.FromScalar(10),
            DataValue.FromScalar(30),
            DataValue.FromScalar(45)
        ]);
        Assert.Equal(DataKind.DateTime, result.Kind);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 10, 30, 45, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void MakeTimestamp_Midnight()
    {
        MakeTimestampFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(2024),
            DataValue.FromScalar(1),
            DataValue.FromScalar(1),
            DataValue.FromScalar(0),
            DataValue.FromScalar(0),
            DataValue.FromScalar(0)
        ]);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void MakeTimestamp_NullArgument_ReturnsNull()
    {
        MakeTimestampFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(2024),
            DataValue.FromScalar(6),
            DataValue.FromScalar(15),
            DataValue.Null(DataKind.Scalar),
            DataValue.FromScalar(30),
            DataValue.FromScalar(0)
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.DateTime, result.Kind);
    }

    [Fact]
    public void MakeTimestamp_ValidateArguments_RejectsWrongCount()
    {
        MakeTimestampFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([
            DataKind.Scalar, DataKind.Scalar, DataKind.Scalar, DataKind.Scalar, DataKind.Scalar]));
    }

    [Fact]
    public void MakeTimestamp_ValidateArguments_RejectsNonScalar()
    {
        MakeTimestampFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([
            DataKind.Scalar, DataKind.Scalar, DataKind.Scalar,
            DataKind.Scalar, DataKind.String, DataKind.Scalar]));
    }

    [Fact]
    public void MakeTimestamp_ValidateArguments_ReturnsDateTimeKind()
    {
        MakeTimestampFunction function = new();
        DataKind result = function.ValidateArguments([
            DataKind.Scalar, DataKind.Scalar, DataKind.Scalar,
            DataKind.Scalar, DataKind.Scalar, DataKind.Scalar]);
        Assert.Equal(DataKind.DateTime, result);
    }
}
