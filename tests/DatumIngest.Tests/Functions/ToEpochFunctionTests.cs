using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="ToEpochFunction"/>.
/// </summary>
public class ToEpochFunctionTests
{
    private readonly ToEpochFunction _function = new();

    [Fact]
    public void Name_IsToEpoch()
    {
        Assert.Equal("to_epoch", _function.Name);
    }

    [Fact]
    public void ToEpoch_DateReturnsEpochDays()
    {
        // 2000-01-01 is 10957 days after 1970-01-01.
        DataValue result = _function.Execute([DataValue.FromDate(new DateOnly(2000, 1, 1))]);
        Assert.Equal(10957f, result.AsScalar());
    }

    [Fact]
    public void ToEpoch_UnixEpochDateReturnsZero()
    {
        DataValue result = _function.Execute([DataValue.FromDate(new DateOnly(1970, 1, 1))]);
        Assert.Equal(0f, result.AsScalar());
    }

    [Fact]
    public void ToEpoch_DateBeforeEpochReturnsNegative()
    {
        DataValue result = _function.Execute([DataValue.FromDate(new DateOnly(1969, 12, 31))]);
        Assert.Equal(-1f, result.AsScalar());
    }

    [Fact]
    public void ToEpoch_DateTimeReturnsEpochSeconds()
    {
        // 2000-01-01T00:00:00 is 946684800 seconds after Unix epoch.
        DataValue result = _function.Execute([DataValue.FromDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero))]);
        Assert.Equal(946684800f, result.AsScalar());
    }

    [Fact]
    public void ToEpoch_DateTimeWithTimeComponent()
    {
        // 1970-01-01T01:00:00 is 3600 seconds after Unix epoch.
        DataValue result = _function.Execute([DataValue.FromDateTime(new DateTimeOffset(1970, 1, 1, 1, 0, 0, TimeSpan.Zero))]);
        Assert.Equal(3600f, result.AsScalar());
    }

    [Fact]
    public void ToEpoch_NullDateReturnsTypedNull()
    {
        DataValue result = _function.Execute([DataValue.Null(DataKind.Date)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Scalar, result.Kind);
    }

    [Fact]
    public void ToEpoch_NullDateTimeReturnsTypedNull()
    {
        DataValue result = _function.Execute([DataValue.Null(DataKind.DateTime)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Scalar, result.Kind);
    }

    [Fact]
    public void ValidateArguments_RejectsNonTemporal()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void ValidateArguments_RejectsWrongArgumentCount()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Date, DataKind.Date]));
    }

    [Fact]
    public void ValidateArguments_AcceptsDate()
    {
        DataKind result = _function.ValidateArguments([DataKind.Date]);
        Assert.Equal(DataKind.Scalar, result);
    }

    [Fact]
    public void ValidateArguments_AcceptsDateTime()
    {
        DataKind result = _function.ValidateArguments([DataKind.DateTime]);
        Assert.Equal(DataKind.Scalar, result);
    }
}
