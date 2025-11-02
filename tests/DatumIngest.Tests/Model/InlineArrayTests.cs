using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Round-trip and edge-case tests for the Phase A3 inline-array feature on
/// <see cref="DataValue"/>: arrays of any unmanaged element kind that pack into the
/// 16-byte payload region, no arena allocation needed.
/// </summary>
public sealed class InlineArrayTests : ServiceTestBase
{
    [Fact]
    public void RoundTrip_Float32x4_QuaternionShape()
    {
        Span<float> source = [1.0f, 2.5f, -3.25f, float.NaN];
        DataValue value = DataValue.FromInlineArray<float>(source, DataKind.Float32);

        Assert.True(value.IsArray);
        Assert.True(value.IsInlineArray);
        Assert.Equal(DataKind.Float32, value.Kind);

        ReadOnlySpan<float> recovered = value.AsInlineArraySpan<float>();
        Assert.Equal(4, recovered.Length);
        Assert.Equal(1.0f, recovered[0]);
        Assert.Equal(2.5f, recovered[1]);
        Assert.Equal(-3.25f, recovered[2]);
        Assert.True(float.IsNaN(recovered[3]));
    }

    [Fact]
    public void RoundTrip_Int32x4_PointShape()
    {
        Span<int> source = [int.MinValue, -1, 0, int.MaxValue];
        DataValue value = DataValue.FromInlineArray<int>(source, DataKind.Int32);

        ReadOnlySpan<int> recovered = value.AsInlineArraySpan<int>();
        Assert.Equal(source.ToArray(), recovered.ToArray());
    }

    [Fact]
    public void RoundTrip_Float64x2_LatLon()
    {
        Span<double> source = [42.3601, -71.0589];
        DataValue value = DataValue.FromInlineArray<double>(source, DataKind.Float64);

        ReadOnlySpan<double> recovered = value.AsInlineArraySpan<double>();
        Assert.Equal(source.ToArray(), recovered.ToArray());
    }

    [Fact]
    public void RoundTrip_UInt8x16_FullPayload()
    {
        Span<byte> source = stackalloc byte[16];
        for (int i = 0; i < 16; i++) source[i] = (byte)(i * 17);

        DataValue value = DataValue.FromInlineArray<byte>(source, DataKind.UInt8);

        ReadOnlySpan<byte> recovered = value.AsInlineArraySpan<byte>();
        Assert.Equal(16, recovered.Length);
        for (int i = 0; i < 16; i++) Assert.Equal((byte)(i * 17), recovered[i]);
    }

    [Fact]
    public void RoundTrip_EmptyArray()
    {
        DataValue value = DataValue.FromInlineArray<int>([], DataKind.Int32);

        Assert.True(value.IsArray);
        Assert.True(value.IsInlineArray);
        Assert.Equal(0, value.AsInlineArraySpan<int>().Length);
    }

    [Fact]
    public void InlineArrayBytes_ReturnsActiveBytesOnly()
    {
        Span<float> source = [1.0f, 2.0f];   // 8 bytes; payload region is 16, trailing 8 unused
        DataValue value = DataValue.FromInlineArray<float>(source, DataKind.Float32);

        ReadOnlySpan<byte> bytes = value.InlineArrayBytes;
        Assert.Equal(8, bytes.Length);
    }

    [Fact]
    public void Construct_OverflowingPayload_Throws()
    {
        // 5 floats × 4 bytes = 20 bytes; exceeds 16-byte payload.
        // Use a heap-allocated array because Span<T> can't cross a lambda boundary.
        float[] tooBig = [1f, 2f, 3f, 4f, 5f];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => DataValue.FromInlineArray<float>(tooBig, DataKind.Float32));
    }

    [Fact]
    public void Construct_OverflowingByteCount_Throws()
    {
        // 17 bytes; exceeds 16-byte payload.
        byte[] tooBig = new byte[17];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DataValue.FromInlineArray<byte>(tooBig, DataKind.UInt8));
    }

    [Fact]
    public void AsInlineArraySpan_OnNonInlineValue_Throws()
    {
        DataValue scalar = DataValue.FromInt32(42);

        Assert.False(scalar.IsInlineArray);
        Assert.Throws<InvalidOperationException>(() => scalar.AsInlineArraySpan<int>());
    }

    [Fact]
    public void IsArray_Flag_Set_ForInlineArrays()
    {
        DataValue value = DataValue.FromInlineArray<int>([1, 2], DataKind.Int32);

        // The new IsArray flag is set, distinct from the legacy DataKind-based path.
        Assert.True(value.IsArray);
        Assert.True(value.IsInlineArray);
    }

    [Fact]
    public void Equals_TwoInlineArraysWithSamePayload_AreEqualByValue()
    {
        DataValue a = DataValue.FromInlineArray<int>([10, 20, 30, 40], DataKind.Int32);
        DataValue b = DataValue.FromInlineArray<int>([10, 20, 30, 40], DataKind.Int32);

        // Direct payload byte equality via the typed span — useful for sort-key fast path.
        Assert.True(a.AsInlineArraySpan<int>().SequenceEqual(b.AsInlineArraySpan<int>()));
    }

    [Fact]
    public void Stabilize_InlineArray_PassesThrough()
    {
        // Sanity check that DataValueRetention treats inline arrays as self-contained
        // (no copy across arenas required), per the Phase A3 update.
        DataValue value = DataValue.FromInlineArray<float>([1f, 2f, 3f, 4f], DataKind.Float32);

        Arena source = new();
        Arena destination = new();
        DataValue stabilized = DataValueRetention.Stabilize(value, source, destination);

        Assert.True(stabilized.IsInlineArray);
        Assert.Equal(value.AsInlineArraySpan<float>().ToArray(),
                     stabilized.AsInlineArraySpan<float>().ToArray());
    }
}
