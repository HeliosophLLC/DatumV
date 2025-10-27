using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="NowFunction"/>.
/// </summary>
public class NowFunctionTests : ServiceTestBase
{
    private readonly NowFunction _function = new();

    [Fact]
    public void Name_IsNow()
    {
        Assert.Equal("now", _function.Name);
    }

    [Fact]
    public void Now_ReturnsDateTime()
    {
        DataValue result = _function.Execute([]);
        Assert.Equal(DataKind.DateTime, result.Kind);
        Assert.False(result.IsNull);
    }

    [Fact]
    public void Now_ReturnsApproximatelyCurrentTime()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        DataValue result = _function.Execute([]);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        DateTimeOffset value = result.AsDateTime();
        Assert.InRange(value, before, after);
    }

    [Fact]
    public void Now_ValidateArguments_RejectsArguments()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void Now_ValidateArguments_AcceptsNoArguments()
    {
        DataKind result = _function.ValidateArguments([]);
        Assert.Equal(DataKind.DateTime, result);
    }
}
