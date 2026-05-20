using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Uuid;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar;

/// <summary>
/// Tests for UUID scalar functions: <see cref="UuidExtractTimestampFunction"/>,
/// <see cref="UuidExtractVersionFunction"/>, and the <c>gen_random_uuid</c> alias for <c>uuidv4</c>.
/// </summary>
public sealed class UuidFunctionTests
{
    private static readonly EvaluationFrame Frame = default;

    // ─── gen_random_uuid alias ─────────────────────────────────────────────

    [Fact]
    public void GenRandomUuid_RegisteredAsAliasForUuidV4()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? aliased = registry.TryGetScalar("gen_random_uuid");
        Assert.IsType<UuidV4Function>(aliased);
    }

    // ─── uuid_extract_version ──────────────────────────────────────────────

    [Fact]
    public async Task ExtractVersion_V4_Returns4()
    {
        ValueRef result = await new UuidExtractVersionFunction()
            .ExecuteAsync(new[] { ValueRef.FromUuid(Guid.NewGuid()) }, Frame, default);
        Assert.False(result.IsNull);
        Assert.Equal((short)4, result.AsInt16());
    }

    [Fact]
    public async Task ExtractVersion_V7_Returns7()
    {
        ValueRef result = await new UuidExtractVersionFunction()
            .ExecuteAsync(new[] { ValueRef.FromUuid(Guid.CreateVersion7()) }, Frame, default);
        Assert.False(result.IsNull);
        Assert.Equal((short)7, result.AsInt16());
    }

    [Fact]
    public async Task ExtractVersion_NilUuid_ReturnsNull()
    {
        ValueRef result = await new UuidExtractVersionFunction()
            .ExecuteAsync(new[] { ValueRef.FromUuid(Guid.Empty) }, Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int16, result.Kind);
    }

    [Fact]
    public async Task ExtractVersion_NullInput_ReturnsNull()
    {
        ValueRef result = await new UuidExtractVersionFunction()
            .ExecuteAsync(new[] { ValueRef.Null(DataKind.Uuid) }, Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int16, result.Kind);
    }

    [Fact]
    public void ExtractVersion_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<UuidExtractVersionFunction>(registry.TryGetScalar("uuid_extract_version"));
    }

    // ─── uuid_extract_timestamp ────────────────────────────────────────────

    [Fact]
    public async Task ExtractTimestamp_V7_RoundTripsEmbeddedTime()
    {
        DateTimeOffset stamp = new(2026, 5, 11, 12, 34, 56, TimeSpan.Zero);
        Guid uuid = Guid.CreateVersion7(stamp);

        ValueRef result = await new UuidExtractTimestampFunction()
            .ExecuteAsync(new[] { ValueRef.FromUuid(uuid) }, Frame, default);

        Assert.False(result.IsNull);
        // v7 stores millisecond precision — compare at that resolution.
        Assert.Equal(stamp.ToUnixTimeMilliseconds(), result.AsTimestampTz().ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ExtractTimestamp_V4_ReturnsNull()
    {
        ValueRef result = await new UuidExtractTimestampFunction()
            .ExecuteAsync(new[] { ValueRef.FromUuid(Guid.NewGuid()) }, Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.TimestampTz, result.Kind);
    }

    [Fact]
    public async Task ExtractTimestamp_V1_KnownVector()
    {
        // RFC 9562 Appendix A.1 — a v1 UUID with timestamp 2022-02-22 19:22:22 UTC.
        // Source: https://www.rfc-editor.org/rfc/rfc9562.html#name-example-of-a-uuidv1-value
        Guid uuid = Guid.Parse("c232ab00-9414-11ec-b3c8-9f6bdeced846");

        ValueRef result = await new UuidExtractTimestampFunction()
            .ExecuteAsync(new[] { ValueRef.FromUuid(uuid) }, Frame, default);

        Assert.False(result.IsNull);
        DateTimeOffset expected = new(2022, 2, 22, 19, 22, 22, TimeSpan.Zero);
        // v1 stores 100ns precision — compare to the nearest second.
        Assert.Equal(expected.ToUnixTimeSeconds(), result.AsTimestampTz().ToUnixTimeSeconds());
    }

    [Fact]
    public async Task ExtractTimestamp_NilUuid_ReturnsNull()
    {
        ValueRef result = await new UuidExtractTimestampFunction()
            .ExecuteAsync(new[] { ValueRef.FromUuid(Guid.Empty) }, Frame, default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task ExtractTimestamp_NullInput_ReturnsNull()
    {
        ValueRef result = await new UuidExtractTimestampFunction()
            .ExecuteAsync(new[] { ValueRef.Null(DataKind.Uuid) }, Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.TimestampTz, result.Kind);
    }

    [Fact]
    public void ExtractTimestamp_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<UuidExtractTimestampFunction>(registry.TryGetScalar("uuid_extract_timestamp"));
    }
}
