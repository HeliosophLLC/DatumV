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

    // ─── Phase A4: AsArraySpan auto-router ───

    [Fact]
    public void AsArraySpan_DispatchesToInline_ForInlineArray()
    {
        // No store / registry needed — auto-router recognises the inline flag.
        DataValue value = DataValue.FromInlineArray<int>([10, 20, 30, 40], DataKind.Int32);

        ReadOnlySpan<int> elements = value.AsArraySpan<int>();
        Assert.Equal(new[] { 10, 20, 30, 40 }, elements.ToArray());
    }

    [Fact]
    public void AsArraySpan_TypedFloat32_RoundTrips()
    {
        DataValue value = DataValue.FromInlineArray<float>([0.5f, 1.5f, -2.5f], DataKind.Float32);

        ReadOnlySpan<float> elements = value.AsArraySpan<float>();
        Assert.Equal(new[] { 0.5f, 1.5f, -2.5f }, elements.ToArray());
    }

    [Fact]
    public void AsArraySpan_OnScalar_Throws()
    {
        DataValue scalar = DataValue.FromInt32(42);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => scalar.AsArraySpan<int>());
        Assert.Contains("non-array", ex.Message);
    }

    // ─── New numeric kinds (Float16, Decimal, Int128, UInt128) ───

    [Fact]
    public void RoundTrip_Float16x8_FillsPayload()
    {
        // 8 × Half = 16 bytes — exactly fills the inline payload region.
        Half[] source =
        [
            (Half)0.0f, (Half)1.0f, (Half)(-1.5f), (Half)2.5f,
            (Half)(-3.25f), Half.NaN, Half.PositiveInfinity, Half.NegativeInfinity,
        ];
        DataValue value = DataValue.FromInlineArray<Half>(source, DataKind.Float16);

        Assert.True(value.IsArray);
        Assert.True(value.IsInlineArray);
        Assert.Equal(DataKind.Float16, value.Kind);

        ReadOnlySpan<Half> recovered = value.AsArraySpan<Half>();
        Assert.Equal(8, recovered.Length);
        for (int i = 0; i < 6; i++) Assert.Equal(source[i], recovered[i]);
        Assert.True(Half.IsNaN(recovered[5]));
        Assert.True(Half.IsPositiveInfinity(recovered[6]));
        Assert.True(Half.IsNegativeInfinity(recovered[7]));
    }

    [Fact]
    public void RoundTrip_Decimalx1_FillsPayload()
    {
        // 1 × decimal = 16 bytes — exactly fills the inline payload region.
        decimal[] source = [123456789.0123456789m];
        DataValue value = DataValue.FromInlineArray<decimal>(source, DataKind.Decimal);

        Assert.True(value.IsArray);
        Assert.True(value.IsInlineArray);
        Assert.Equal(DataKind.Decimal, value.Kind);

        ReadOnlySpan<decimal> recovered = value.AsArraySpan<decimal>();
        Assert.Equal(1, recovered.Length);
        Assert.Equal(123456789.0123456789m, recovered[0]);
    }

    [Fact]
    public void RoundTrip_Int128x1_FillsPayload()
    {
        Int128[] source = [Int128.MaxValue];
        DataValue value = DataValue.FromInlineArray<Int128>(source, DataKind.Int128);

        Assert.True(value.IsArray);
        Assert.Equal(DataKind.Int128, value.Kind);
        ReadOnlySpan<Int128> recovered = value.AsArraySpan<Int128>();
        Assert.Equal(Int128.MaxValue, recovered[0]);
    }

    [Fact]
    public void RoundTrip_UInt128x1_FillsPayload()
    {
        UInt128[] source = [UInt128.MaxValue];
        DataValue value = DataValue.FromInlineArray<UInt128>(source, DataKind.UInt128);

        Assert.True(value.IsArray);
        Assert.Equal(DataKind.UInt128, value.Kind);
        ReadOnlySpan<UInt128> recovered = value.AsArraySpan<UInt128>();
        Assert.Equal(UInt128.MaxValue, recovered[0]);
    }

    // ─── Arena-backed round-trips (large arrays via FromArenaArray) ───

    [Fact]
    public void RoundTrip_ArenaInt128_LongerThanInline()
    {
        // 4 × Int128 = 64 bytes — far past the 16-byte inline cap, must arena-back.
        Int128[] source = [1, -2, Int128.MaxValue, Int128.MinValue];
        Arena arena = new();

        DataValue value = DataValue.FromArenaArray<Int128>(source, DataKind.Int128, arena);

        Assert.True(value.IsArray);
        Assert.False(value.IsInlineArray);
        Assert.Equal(DataKind.Int128, value.Kind);

        ReadOnlySpan<Int128> recovered = value.AsArraySpan<Int128>(arena);
        Assert.Equal(source, recovered.ToArray());
    }

    [Fact]
    public void RoundTrip_ArenaFloat16_LargeBatch()
    {
        // 100 Half elements = 200 bytes — exercises the arena path.
        Half[] source = new Half[100];
        for (int i = 0; i < source.Length; i++) source[i] = (Half)(i * 0.5f);

        Arena arena = new();
        DataValue value = DataValue.FromArenaArray<Half>(source, DataKind.Float16, arena);

        Assert.False(value.IsInlineArray);
        ReadOnlySpan<Half> recovered = value.AsArraySpan<Half>(arena);
        Assert.Equal(source, recovered.ToArray());
    }

    [Fact]
    public void FromInlineArrayBytes_RejectsNonMultipleOfElementSize()
    {
        // 5 bytes is not a multiple of 4 (Float32 element size).
        byte[] payload = [1, 2, 3, 4, 5];
        Assert.Throws<ArgumentException>(
            () => DataValue.FromInlineArrayBytes(payload, DataKind.Float32));
    }

    [Fact]
    public void FromInlineArrayBytes_DerivesElementCount_FromKindStride()
    {
        // 12 bytes, Int32 stride = 4 → 3 elements.
        byte[] payload = new byte[12];
        BitConverter.GetBytes(7).CopyTo(payload, 0);
        BitConverter.GetBytes(11).CopyTo(payload, 4);
        BitConverter.GetBytes(13).CopyTo(payload, 8);

        DataValue value = DataValue.FromInlineArrayBytes(payload, DataKind.Int32);
        ReadOnlySpan<int> recovered = value.AsArraySpan<int>();
        Assert.Equal(new[] { 7, 11, 13 }, recovered.ToArray());
    }
}
