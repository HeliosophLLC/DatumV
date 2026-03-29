using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Tests for <see cref="DataValueRetention.Stabilize"/> — the helper that copies
/// reference-type <see cref="DataValue"/> payloads into a retention store so the
/// resulting value remains valid beyond the original source's lifetime.
/// </summary>
public sealed class DataValueRetentionTests : ServiceTestBase
{
    // ───────────────────── Self-contained values pass through unchanged ─────────────────────

    [Fact]
    public void Stabilize_Null_PassesThrough()
    {
        Arena source = new();
        Arena retention = new();

        DataValue value = DataValue.Null(DataKind.Int32);
        DataValue stable = DataValueRetention.Stabilize(value, source, retention);

        Assert.True(stable.IsNull);
        Assert.Equal(DataKind.Int32, stable.Kind);
    }

    [Fact]
    public void Stabilize_InlineString_PassesThrough()
    {
        Arena source = new();
        Arena retention = new();

        DataValue value = DataValue.FromString("short", source);
        Assert.True(value.IsInline);

        DataValue stable = DataValueRetention.Stabilize(value, source, retention);

        Assert.True(stable.IsInline);
        Assert.Equal("short", stable.AsString(retention));
    }

    [Theory]
    [InlineData(DataKind.Int32)]
    [InlineData(DataKind.Int64)]
    [InlineData(DataKind.Float32)]
    [InlineData(DataKind.Float64)]
    [InlineData(DataKind.Boolean)]
    [InlineData(DataKind.Date)]
    [InlineData(DataKind.TimestampTz)]
    [InlineData(DataKind.Timestamp)]
    [InlineData(DataKind.Uuid)]
    public void Stabilize_FixedSizeScalar_PassesThrough(DataKind kind)
    {
        Arena source = new();
        Arena retention = new();

        DataValue value = kind switch
        {
            DataKind.Int32 => DataValue.FromInt32(42),
            DataKind.Int64 => DataValue.FromInt64(9_999_999_999L),
            DataKind.Float32 => DataValue.FromFloat32(3.14f),
            DataKind.Float64 => DataValue.FromFloat64(2.71828),
            DataKind.Boolean => DataValue.FromBoolean(true),
            DataKind.Date => DataValue.FromDate(new DateOnly(2025, 1, 31)),
            DataKind.TimestampTz => DataValue.FromTimestampTz(
                new DateTimeOffset(2025, 1, 31, 12, 0, 0, TimeSpan.Zero)),
            DataKind.Timestamp => DataValue.FromTimestamp(
                new DateTime(2025, 1, 31, 12, 0, 0, DateTimeKind.Unspecified)),
            DataKind.Uuid => DataValue.FromUuid(Guid.Parse("12345678-1234-1234-1234-123456789abc")),
            _ => throw new ArgumentException($"Unsupported: {kind}"),
        };

        DataValue stable = DataValueRetention.Stabilize(value, source, retention);

        Assert.Equal(value.Kind, stable.Kind);
        Assert.Equal(value, stable);
    }

    // ───────────────────── Reference-type payloads copy into retention store ─────────────────────

    [Fact]
    public void Stabilize_NonInlineString_CopiesToRetentionStore()
    {
        Arena source = new();
        Arena retention = new();

        // 30-byte string → forces reference-store (>16 byte inline threshold).
        string value = "this string is longer than sixteen bytes";
        DataValue original = DataValue.FromString(value, source);
        Assert.False(original.IsInline);

        DataValue stable = DataValueRetention.Stabilize(original, source, retention);

        // Stabilised value must read back correctly from the retention store.
        Assert.Equal(value, stable.AsString(retention));
    }

    [Fact]
    public void Stabilize_NonInlineString_SurvivesSourceDisposal()
    {
        Arena source = new();
        Arena retention = new();

        string value = "this string is longer than sixteen bytes";
        DataValue original = DataValue.FromString(value, source);
        DataValue stable = DataValueRetention.Stabilize(original, source, retention);

        // Dispose the source arena — reads of the stabilised value must still work.
        source.Dispose();

        Assert.Equal(value, stable.AsString(retention));
    }

    [Fact]
    public void Stabilize_UInt8Array_CopiesToRetentionStore()
    {
        Arena source = new();
        Arena retention = new();

        byte[] bytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        DataValue original = DataValue.FromByteArray(bytes, source);

        DataValue stable = DataValueRetention.Stabilize(original, source, retention);

        source.Dispose();

        byte[] read = stable.AsUInt8Array(retention);
        Assert.Equal(bytes, read);
    }

    [Fact]
    public void Stabilize_Image_CopiesToRetentionStore()
    {
        Arena source = new();
        Arena retention = new();

        byte[] bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };   // PNG magic
        DataValue original = DataValue.FromImage(bytes, source);

        DataValue stable = DataValueRetention.Stabilize(original, source, retention);

        source.Dispose();

        byte[] read = stable.AsImage(retention);
        Assert.Equal(bytes, read);
    }

    // ───────────────────── Equivalence of stabilised and original ─────────────────────

    [Fact]
    public void Stabilize_StringContent_HashesEqualToOriginal()
    {
        Arena source = new();
        Arena retention = new();

        string value = "some content to hash that is longer than sixteen bytes";
        DataValue original = DataValue.FromString(value, source);
        DataValue stable = DataValueRetention.Stabilize(original, source, retention);

        // Same content → same XxHash64 → equal RawContentHash across stores.
        Assert.Equal(original.RawContentHash, stable.RawContentHash);
    }

    [Fact]
    public void Stabilize_StringContent_EqualsOriginal()
    {
        Arena source = new();
        Arena retention = new();

        string value = "a string that is definitely longer than sixteen bytes";
        DataValue original = DataValue.FromString(value, source);
        DataValue stable = DataValueRetention.Stabilize(original, source, retention);

        // Both are reference-store values with the same cached hash — CompareStrings
        // takes the both-reference-store path and compares by cached hash.
        Assert.Equal(original, stable);
    }

    // ───────────────────── Unsupported kinds throw with a pointer to the helper ─────────────────────

    [Fact]
    public void Stabilize_ArenaFloat32Array_CopiesToRetentionStore()
    {
        Arena source = new();
        Arena retention = new();

        // 100 floats = 400 bytes — exceeds the inline cap, so this lands arena-backed.
        float[] values = Enumerable.Range(0, 100).Select(i => i * 0.5f).ToArray();
        DataValue original = DataValue.FromArenaArray<float>(values, DataKind.Float32, source);
        Assert.False(original.IsInline);

        DataValue stable = DataValueRetention.Stabilize(original, source, retention);

        // Source disposal must not corrupt the stabilised value.
        source.Dispose();

        ReadOnlySpan<float> recovered = stable.AsArraySpan<float>(retention);
        Assert.Equal(values, recovered.ToArray());
        Assert.Equal(DataKind.Float32, stable.Kind);
        Assert.True(stable.IsArray);
    }

    // ───────────────────── PointCloud / Mesh byte-blob retention ─────────────────────

    [Fact]
    public void Stabilize_PointCloud_CopiesBytesToRetentionStore()
    {
        Arena source = new();
        Arena retention = new();

        byte[] blob = new byte[64];
        for (int i = 0; i < blob.Length; i++) blob[i] = (byte)(i + 1);
        DataValue original = DataValue.FromPointCloud(blob, source);

        DataValue stable = DataValueRetention.Stabilize(original, source, retention);

        source.Dispose();   // retention copy must survive source disposal

        ReadOnlySpan<byte> recovered = stable.AsByteSpan(retention);
        Assert.Equal(blob, recovered.ToArray());
        Assert.Equal(DataKind.PointCloud, stable.Kind);
    }

    [Fact]
    public void Stabilize_Mesh_CopiesBytesToRetentionStore()
    {
        Arena source = new();
        Arena retention = new();

        byte[] blob = new byte[96];
        for (int i = 0; i < blob.Length; i++) blob[i] = (byte)(i * 3 + 7);
        DataValue original = DataValue.FromMesh(blob, source);

        DataValue stable = DataValueRetention.Stabilize(original, source, retention);

        source.Dispose();

        ReadOnlySpan<byte> recovered = stable.AsByteSpan(retention);
        Assert.Equal(blob, recovered.ToArray());
        Assert.Equal(DataKind.Mesh, stable.Kind);
    }
}
