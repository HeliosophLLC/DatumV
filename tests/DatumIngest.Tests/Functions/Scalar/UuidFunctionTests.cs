using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for all UUID-related scalar functions:
/// <see cref="Uuid4Function"/>, <see cref="Uuid7Function"/>, <see cref="IsUuidFunction"/>,
/// <see cref="UuidStrFunction"/>, <see cref="UuidBytesFunction"/>,
/// <see cref="UuidVersionFunction"/>, and <see cref="UuidTimestampFunction"/>.
/// </summary>
public class UuidFunctionTests
{
    // ───────────────── Uuid4Function ─────────────────

    [Fact]
    public void Uuid4Function_ReturnsUuid()
    {
        Uuid4Function function = new();
        DataValue result = function.Execute([]);
        Assert.Equal(DataKind.Uuid, result.Kind);
        Assert.False(result.IsNull);
    }

    [Fact]
    public void Uuid4Function_GeneratesDistinctValues()
    {
        Uuid4Function function = new();
        DataValue first = function.Execute([]);
        DataValue second = function.Execute([]);
        Assert.NotEqual(first.AsUuid(), second.AsUuid());
    }

    [Fact]
    public void Uuid4Function_ValidateArguments_ReturnsUuidKind()
    {
        Uuid4Function function = new();
        DataKind result = function.ValidateArguments([]);
        Assert.Equal(DataKind.Uuid, result);
    }

    [Fact]
    public void Uuid4Function_ValidateArguments_ExtraArgs_Throws()
    {
        Uuid4Function function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }

    // ───────────────── Uuid7Function ─────────────────

    [Fact]
    public void Uuid7Function_ReturnsUuid()
    {
        Uuid7Function function = new();
        DataValue result = function.Execute([]);
        Assert.Equal(DataKind.Uuid, result.Kind);
        Assert.False(result.IsNull);
    }

    [Fact]
    public void Uuid7Function_GeneratesDistinctValues()
    {
        Uuid7Function function = new();
        DataValue first = function.Execute([]);
        DataValue second = function.Execute([]);
        Assert.NotEqual(first.AsUuid(), second.AsUuid());
    }

    [Fact]
    public void Uuid7Function_ValidateArguments_ExtraArgs_Throws()
    {
        Uuid7Function function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar]));
    }

    // ───────────────── IsUuidFunction ─────────────────

    [Fact]
    public void IsUuidFunction_ValidUuid_ReturnsTrue()
    {
        IsUuidFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("550e8400-e29b-41d4-a716-446655440000")]);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void IsUuidFunction_InvalidString_ReturnsFalse()
    {
        IsUuidFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("not-a-uuid")]);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void IsUuidFunction_NullInput_ReturnsNull()
    {
        IsUuidFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void IsUuidFunction_ValidateArguments_WrongArgCount_Throws()
    {
        IsUuidFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([]));
    }

    [Fact]
    public void IsUuidFunction_ValidateArguments_WrongType_Throws()
    {
        IsUuidFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar]));
    }

    // ───────────────── UuidStrFunction ─────────────────

    [Fact]
    public void UuidStrFunction_FormatsAsLowercaseHyphenated()
    {
        Guid knownGuid = new("550E8400-E29B-41D4-A716-446655440000");
        UuidStrFunction function = new();
        DataValue result = function.Execute([DataValue.FromUuid(knownGuid)]);
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", result.AsString());
    }

    [Fact]
    public void UuidStrFunction_NullInput_ReturnsNull()
    {
        UuidStrFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Uuid)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void UuidStrFunction_ValidateArguments_WrongType_Throws()
    {
        UuidStrFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }

    // ───────────────── UuidBytesFunction ─────────────────

    [Fact]
    public void UuidBytesFunction_Returns16Bytes()
    {
        Uuid4Function generator = new();
        DataValue uuid = generator.Execute([]);

        UuidBytesFunction function = new();
        DataValue result = function.Execute([uuid]);
        Assert.Equal(16, result.AsUInt8Array().ToArray().Length);
    }

    [Fact]
    public void UuidBytesFunction_NullInput_ReturnsNull()
    {
        UuidBytesFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Uuid)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void UuidBytesFunction_ValidateArguments_WrongType_Throws()
    {
        UuidBytesFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }

    // ───────────────── UuidVersionFunction ─────────────────

    [Fact]
    public void UuidVersionFunction_V4_Returns4()
    {
        Uuid4Function generator = new();
        DataValue uuid = generator.Execute([]);

        UuidVersionFunction function = new();
        DataValue result = function.Execute([uuid]);
        Assert.Equal(4f, result.AsScalar());
    }

    [Fact]
    public void UuidVersionFunction_NullInput_ReturnsNull()
    {
        UuidVersionFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Uuid)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void UuidVersionFunction_ValidateArguments_WrongArgCount_Throws()
    {
        UuidVersionFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([]));
    }

    // ───────────────── UuidTimestampFunction ─────────────────

    [Fact]
    public void UuidTimestampFunction_V7_ReturnsDateTime()
    {
        Uuid7Function generator = new();
        DataValue uuid = generator.Execute([]);

        UuidTimestampFunction function = new();
        DataValue result = function.Execute([uuid]);
        Assert.Equal(DataKind.DateTime, result.Kind);
        Assert.False(result.IsNull);
    }

    [Fact]
    public void UuidTimestampFunction_V4_ReturnsNull()
    {
        Uuid4Function generator = new();
        DataValue uuid = generator.Execute([]);

        UuidTimestampFunction function = new();
        DataValue result = function.Execute([uuid]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void UuidTimestampFunction_NullInput_ReturnsNull()
    {
        UuidTimestampFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Uuid)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void UuidTimestampFunction_ValidateArguments_WrongType_Throws()
    {
        UuidTimestampFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }
}
