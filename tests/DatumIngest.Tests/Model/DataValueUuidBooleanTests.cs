using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Model;

/// <summary>
/// Tests for <see cref="DataValue"/> factory methods and behavior
/// for <see cref="DataKind.Uuid"/> and <see cref="DataKind.Boolean"/> kinds.
/// </summary>
public class DataValueUuidBooleanTests : ServiceTestBase
{
    // ───────────────── FromUuid ─────────────────

    [Fact]
    public void FromUuid_CreatesUuidKind()
    {
        Guid guid = Guid.NewGuid();
        DataValue value = DataValue.FromUuid(guid);
        Assert.Equal(DataKind.Uuid, value.Kind);
    }

    [Fact]
    public void AsUuid_ReturnsCorrectGuid()
    {
        Guid guid = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        DataValue value = DataValue.FromUuid(guid);
        Assert.Equal(guid, value.AsUuid());
    }

    // ───────────────── FromBoolean ─────────────────

    [Fact]
    public void FromBoolean_True_CreatesBooleanKind()
    {
        DataValue value = DataValue.FromBoolean(true);
        Assert.Equal(DataKind.Boolean, value.Kind);
    }

    [Fact]
    public void FromBoolean_False_CreatesBooleanKind()
    {
        DataValue value = DataValue.FromBoolean(false);
        Assert.Equal(DataKind.Boolean, value.Kind);
    }

    [Fact]
    public void AsBoolean_True_ReturnsTrue()
    {
        DataValue value = DataValue.FromBoolean(true);
        Assert.True(value.AsBoolean());
    }

    [Fact]
    public void AsBoolean_False_ReturnsFalse()
    {
        DataValue value = DataValue.FromBoolean(false);
        Assert.False(value.AsBoolean());
    }

    // ───────────────── Caching ─────────────────

    [Fact]
    public void BooleanTrue_IsCached()
    {
        DataValue first = DataValue.FromBoolean(true);
        DataValue second = DataValue.FromBoolean(true);
        Assert.Equal(first, second);
    }

    [Fact]
    public void BooleanFalse_IsCached()
    {
        DataValue first = DataValue.FromBoolean(false);
        DataValue second = DataValue.FromBoolean(false);
        Assert.Equal(first, second);
    }

    // ───────────────── Equality ─────────────────

    [Fact]
    public void Uuid_Equals_SameGuid()
    {
        Guid guid = new("550e8400-e29b-41d4-a716-446655440000");
        DataValue first = DataValue.FromUuid(guid);
        DataValue second = DataValue.FromUuid(guid);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Uuid_NotEquals_DifferentGuid()
    {
        DataValue first = DataValue.FromUuid(Guid.NewGuid());
        DataValue second = DataValue.FromUuid(Guid.NewGuid());
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Boolean_Equals_SameValue()
    {
        DataValue first = DataValue.FromBoolean(true);
        DataValue second = DataValue.FromBoolean(true);
        Assert.Equal(first, second);
    }

    // ───────────────── ToString ─────────────────

    [Fact]
    public void Uuid_ToString_ContainsGuid()
    {
        Guid guid = new("550e8400-e29b-41d4-a716-446655440000");
        DataValue value = DataValue.FromUuid(guid);
        string representation = value.ToString();
        Assert.Contains("550e8400-e29b-41d4-a716-446655440000", representation);
    }

    [Fact]
    public void Boolean_ToString_ReturnsExpected()
    {
        DataValue trueValue = DataValue.FromBoolean(true);
        DataValue falseValue = DataValue.FromBoolean(false);
        Assert.Equal("true", trueValue.ToString());
        Assert.Equal("false", falseValue.ToString());
    }

    // ───────────────── Null ─────────────────

    [Fact]
    public void NullUuid_IsNull()
    {
        DataValue value = DataValue.Null(DataKind.Uuid);
        Assert.True(value.IsNull);
        Assert.Equal(DataKind.Uuid, value.Kind);
    }

    [Fact]
    public void NullBoolean_IsNull()
    {
        DataValue value = DataValue.Null(DataKind.Boolean);
        Assert.True(value.IsNull);
        Assert.Equal(DataKind.Boolean, value.Kind);
    }
}
