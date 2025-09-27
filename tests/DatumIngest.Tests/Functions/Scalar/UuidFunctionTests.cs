using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for all UUID-related scalar functions:
/// <see cref="Uuidv4Function"/>, <see cref="Uuidv7Function"/>, <see cref="IsUuidFunction"/>,
/// <see cref="UuidStrFunction"/>, <see cref="UuidBytesFunction"/>,
/// <see cref="UuidExtractVersionFunction"/>, and <see cref="UuidExtractTimestampFunction"/>.
/// </summary>
public class UuidFunctionTests
{
    // ───────────────── Uuidv4Function ─────────────────

    [Fact]
    public void Uuidv4Function_ReturnsUuid()
    {
        Uuidv4Function function = new();
        DataValue result = function.Execute([]);
        Assert.Equal(DataKind.Uuid, result.Kind);
        Assert.False(result.IsNull);
    }

    [Fact]
    public void Uuidv4Function_GeneratesDistinctValues()
    {
        Uuidv4Function function = new();
        DataValue first = function.Execute([]);
        DataValue second = function.Execute([]);
        Assert.NotEqual(first.AsUuid(), second.AsUuid());
    }

    [Fact]
    public void Uuidv4Function_ValidateArguments_ReturnsUuidKind()
    {
        Uuidv4Function function = new();
        DataKind result = function.ValidateArguments([]);
        Assert.Equal(DataKind.Uuid, result);
    }

    [Fact]
    public void Uuidv4Function_ValidateArguments_ExtraArgs_Throws()
    {
        Uuidv4Function function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }

    // ───────────────── Uuidv7Function ─────────────────

    [Fact]
    public void Uuidv7Function_NoArgs_ReturnsUuid()
    {
        Uuidv7Function function = new();
        DataValue result = function.Execute([]);
        Assert.Equal(DataKind.Uuid, result.Kind);
        Assert.False(result.IsNull);
    }

    [Fact]
    public void Uuidv7Function_GeneratesDistinctValues()
    {
        Uuidv7Function function = new();
        DataValue first = function.Execute([]);
        DataValue second = function.Execute([]);
        Assert.NotEqual(first.AsUuid(), second.AsUuid());
    }

    [Fact]
    public void Uuidv7Function_ValidateArguments_NoArgs_ReturnsUuidKind()
    {
        Uuidv7Function function = new();
        DataKind result = function.ValidateArguments([]);
        Assert.Equal(DataKind.Uuid, result);
    }

    [Fact]
    public void Uuidv7Function_ValidateArguments_DurationArg_ReturnsUuidKind()
    {
        Uuidv7Function function = new();
        DataKind result = function.ValidateArguments([DataKind.Duration]);
        Assert.Equal(DataKind.Uuid, result);
    }

    [Fact]
    public void Uuidv7Function_ValidateArguments_WrongType_Throws()
    {
        Uuidv7Function function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void Uuidv7Function_ValidateArguments_TooManyArgs_Throws()
    {
        Uuidv7Function function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Duration, DataKind.Duration]));
    }

    [Fact]
    public void Uuidv7Function_WithShift_ReturnsUuid()
    {
        Uuidv7Function function = new();
        DataValue shift = DataValue.FromDuration(TimeSpan.FromHours(1));
        DataValue result = function.Execute([shift]);
        Assert.Equal(DataKind.Uuid, result.Kind);
        Assert.False(result.IsNull);
    }

    [Fact]
    public void Uuidv7Function_WithShift_TimestampReflectsOffset()
    {
        Uuidv7Function generator = new();
        UuidExtractTimestampFunction extractor = new();

        DataValue noShift = generator.Execute([]);
        DataValue withShift = generator.Execute([DataValue.FromDuration(TimeSpan.FromHours(1))]);

        DateTimeOffset baseTime = extractor.Execute([noShift]).AsDateTime();
        DateTimeOffset shiftedTime = extractor.Execute([withShift]).AsDateTime();

        // The shifted UUID timestamp should be roughly 1 hour ahead.
        TimeSpan diff = shiftedTime - baseTime;
        Assert.InRange(diff.TotalMinutes, 55, 65);
    }

    [Fact]
    public void Uuidv7Function_WithNullShift_ReturnsUuid()
    {
        Uuidv7Function function = new();
        DataValue nullShift = DataValue.Null(DataKind.Duration);
        DataValue result = function.Execute([nullShift]);
        Assert.Equal(DataKind.Uuid, result.Kind);
        Assert.False(result.IsNull);
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
            function.ValidateArguments([DataKind.Float32]));
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
        Uuidv4Function generator = new();
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

    // ───────────────── UuidExtractVersionFunction ─────────────────

    [Fact]
    public void UuidExtractVersionFunction_V4_Returns4()
    {
        Uuidv4Function generator = new();
        DataValue uuid = generator.Execute([]);

        UuidExtractVersionFunction function = new();
        DataValue result = function.Execute([uuid]);
        Assert.Equal((short)4, result.AsInt16());
    }

    [Fact]
    public void UuidExtractVersionFunction_V7_Returns7()
    {
        Uuidv7Function generator = new();
        DataValue uuid = generator.Execute([]);

        UuidExtractVersionFunction function = new();
        DataValue result = function.Execute([uuid]);
        Assert.Equal((short)7, result.AsInt16());
    }

    [Fact]
    public void UuidExtractVersionFunction_NullInput_ReturnsNull()
    {
        UuidExtractVersionFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Uuid)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void UuidExtractVersionFunction_ValidateArguments_ReturnsInt16()
    {
        UuidExtractVersionFunction function = new();
        DataKind result = function.ValidateArguments([DataKind.Uuid]);
        Assert.Equal(DataKind.Int16, result);
    }

    [Fact]
    public void UuidExtractVersionFunction_ValidateArguments_WrongArgCount_Throws()
    {
        UuidExtractVersionFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([]));
    }

    // ───────────────── UuidExtractTimestampFunction ─────────────────

    [Fact]
    public void UuidExtractTimestampFunction_V7_ReturnsDateTime()
    {
        Uuidv7Function generator = new();
        DataValue uuid = generator.Execute([]);

        UuidExtractTimestampFunction function = new();
        DataValue result = function.Execute([uuid]);
        Assert.Equal(DataKind.DateTime, result.Kind);
        Assert.False(result.IsNull);
    }

    [Fact]
    public void UuidExtractTimestampFunction_V4_ReturnsNull()
    {
        Uuidv4Function generator = new();
        DataValue uuid = generator.Execute([]);

        UuidExtractTimestampFunction function = new();
        DataValue result = function.Execute([uuid]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void UuidExtractTimestampFunction_NullInput_ReturnsNull()
    {
        UuidExtractTimestampFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Uuid)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void UuidExtractTimestampFunction_ValidateArguments_WrongType_Throws()
    {
        UuidExtractTimestampFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void UuidExtractTimestampFunction_V7_TimestampIsRecent()
    {
        Uuidv7Function generator = new();
        DataValue uuid = generator.Execute([]);

        UuidExtractTimestampFunction function = new();
        DateTimeOffset extracted = function.Execute([uuid]).AsDateTime();
        TimeSpan age = DateTimeOffset.UtcNow - extracted;
        Assert.InRange(age.TotalSeconds, -1, 5);
    }
}
