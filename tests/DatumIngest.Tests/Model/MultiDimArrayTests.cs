using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Round-trip and validation tests for multi-dimensional typed-array values on
/// <see cref="DataValue"/>. Covers PR1: arena / inline factories, <c>GetShape</c>
/// accessor, element accessors skipping the shape prefix, validation rejections.
/// </summary>
public sealed class MultiDimArrayTests : ServiceTestBase
{
    // ───────────────────── Arena round-trip ─────────────────────

    [Fact]
    public void Arena_Float32_2x3_RoundTripsShapeAndElements()
    {
        float[] data = [1f, 2f, 3f, 4f, 5f, 6f];
        int[] shape = [2, 3];
        Arena arena = CreateArena();

        DataValue value = DataValue.FromArenaMultiDimArray<float>(data, shape, DataKind.Float32, arena);

        Assert.True(value.IsArray);
        Assert.True(value.IsMultiDim);
        Assert.False(value.IsInlineArray);
        Assert.Equal(DataKind.Float32, value.Kind);
        Assert.Equal(2, value.Ndim);

        ReadOnlySpan<int> recoveredShape = value.GetShape(arena);
        Assert.Equal(shape, recoveredShape.ToArray());

        ReadOnlySpan<float> recovered = value.AsArraySpan<float>(arena);
        Assert.Equal(data, recovered.ToArray());

        Assert.Equal(6, value.ElementCount);
    }

    [Fact]
    public void Arena_Int32_2x2x2_ThreeDimensional()
    {
        int[] data = [0, 1, 2, 3, 4, 5, 6, 7];
        int[] shape = [2, 2, 2];
        Arena arena = CreateArena();

        DataValue value = DataValue.FromArenaMultiDimArray<int>(data, shape, DataKind.Int32, arena);

        Assert.Equal(3, value.Ndim);
        Assert.Equal(shape, value.GetShape(arena).ToArray());
        Assert.Equal(data, value.AsArraySpan<int>(arena).ToArray());
        Assert.Equal(8, value.ElementCount);
    }

    [Fact]
    public void Arena_Float64_LargeMatrix_RoundTrips()
    {
        // 32×32 = 1024 elements × 8 bytes = 8 KiB — well past inline cap.
        double[] data = new double[1024];
        for (int i = 0; i < data.Length; i++) data[i] = i * 0.25;
        int[] shape = [32, 32];
        Arena arena = CreateArena();

        DataValue value = DataValue.FromArenaMultiDimArray<double>(data, shape, DataKind.Float64, arena);

        Assert.Equal(shape, value.GetShape(arena).ToArray());
        Assert.Equal(data, value.AsArraySpan<double>(arena).ToArray());
        Assert.Equal(1024, value.ElementCount);
    }

    // ───────────────────── Inline round-trip ─────────────────────

    [Fact]
    public void Inline_Float32_1x2_FitsInPayload()
    {
        // 2 dims × 4 = 8 shape bytes + 2 × 4 = 8 element bytes = 16 (exact fit).
        float[] data = [3.5f, -1.25f];
        int[] shape = [1, 2];

        DataValue value = DataValue.FromInlineMultiDimArray<float>(data, shape, DataKind.Float32);

        Assert.True(value.IsArray);
        Assert.True(value.IsInlineArray);
        Assert.True(value.IsMultiDim);
        Assert.Equal(2, value.Ndim);

        Assert.Equal(shape, value.GetShape().ToArray());

        ReadOnlySpan<float> recovered = value.AsInlineArraySpan<float>();
        Assert.Equal(data, recovered.ToArray());

        // AsArraySpan auto-router also works without a store.
        ReadOnlySpan<float> viaAuto = value.AsArraySpan<float>();
        Assert.Equal(data, viaAuto.ToArray());
    }

    [Fact]
    public void Inline_Int16_2x2_RoundTrips()
    {
        // 2 dims × 4 = 8 shape bytes + 4 × 2 = 8 element bytes = 16 (exact fit).
        short[] data = [10, 20, 30, 40];
        int[] shape = [2, 2];

        DataValue value = DataValue.FromInlineMultiDimArray<short>(data, shape, DataKind.Int16);

        Assert.Equal(shape, value.GetShape().ToArray());
        Assert.Equal(data, value.AsInlineArraySpan<short>().ToArray());
        Assert.Equal(4, value.ElementCount);
    }

    [Fact]
    public void Inline_InlineArrayBytes_ReturnsElementBytesOnly()
    {
        // Shape prefix MUST be excluded from InlineArrayBytes.
        short[] data = [10, 20, 30, 40];
        int[] shape = [2, 2];

        DataValue value = DataValue.FromInlineMultiDimArray<short>(data, shape, DataKind.Int16);

        ReadOnlySpan<byte> bytes = value.InlineArrayBytes;
        Assert.Equal(8, bytes.Length);   // 4 elements × 2 bytes, not 8 + 8.
    }

    [Fact]
    public void Inline_OverflowingPayload_Throws()
    {
        // shape [2,2] = 8 bytes prefix + 4 × Float32 = 16 element bytes = 24 total → too big.
        float[] data = [1f, 2f, 3f, 4f];
        int[] shape = [2, 2];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => DataValue.FromInlineMultiDimArray<float>(data, shape, DataKind.Float32));
    }

    // ───────────────────── Non-multi-dim values ─────────────────────

    [Fact]
    public void NonMultiDim_Value_HasZeroNdim_AndEmptyShape()
    {
        // Regular 1-D arena array — no flag, no prefix.
        Arena arena = CreateArena();
        DataValue value = DataValue.FromArenaArray<float>([1f, 2f, 3f], DataKind.Float32, arena);

        Assert.False(value.IsMultiDim);
        Assert.Equal(0, value.Ndim);
        Assert.True(value.GetShape(arena).IsEmpty);
        Assert.Equal(3, value.ElementCount);
    }

    [Fact]
    public void NonMultiDim_InlineArray_StillReadsElementsCorrectly()
    {
        // Sanity: the prefix-skipping logic is a no-op when !IsMultiDim.
        DataValue value = DataValue.FromInlineArray<float>([1f, 2f, 3f], DataKind.Float32);

        Assert.False(value.IsMultiDim);
        Assert.Equal([1f, 2f, 3f], value.AsInlineArraySpan<float>().ToArray());
        Assert.Equal(12, value.InlineArrayBytes.Length);
    }

    // ───────────────────── Validation ─────────────────────

    [Fact]
    public void Validate_RejectsNdimLessThanTwo()
    {
        Arena arena = CreateArena();
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimArray<float>([1f, 2f, 3f], [3], DataKind.Float32, arena));
    }

    [Fact]
    public void Validate_RejectsNonPositiveDim()
    {
        Arena arena = CreateArena();
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimArray<float>([], [2, 0], DataKind.Float32, arena));
    }

    [Fact]
    public void Validate_RejectsShapeProductMismatch()
    {
        Arena arena = CreateArena();
        // shape [2,3] = 6 elements expected, but only 5 supplied.
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimArray<float>([1f, 2f, 3f, 4f, 5f], [2, 3], DataKind.Float32, arena));
    }

    [Fact]
    public void Validate_RejectsByteArrayKind()
    {
        Arena arena = CreateArena();
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimArray<byte>(new byte[4], [2, 2], DataKind.UInt8, arena));
    }

    [Fact]
    public void Validate_RejectsReferenceElementKinds()
    {
        Arena arena = CreateArena();
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimArray<byte>(new byte[4], [2, 2], DataKind.String, arena));
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimArray<byte>(new byte[4], [2, 2], DataKind.Image, arena));
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimArray<byte>(new byte[4], [2, 2], DataKind.Struct, arena));
    }

    // ───────────────────── Sidecar factory (offset/length only — bytes assumed present) ─────────────────────

    [Fact]
    public void Sidecar_Factory_PacksFlagsAndNdim()
    {
        // Just the value-side construction — sidecar bytes I/O is exercised in PR3.
        DataValue value = DataValue.FromMultiDimArrayInSidecar(
            DataKind.Float32, offset: 1024, length: 8 + 24, ndim: 2, storeId: 7);

        Assert.True(value.IsArray);
        Assert.True(value.IsMultiDim);
        Assert.True(value.IsInSidecar);
        Assert.Equal(DataKind.Float32, value.Kind);
        Assert.Equal(2, value.Ndim);
    }

    [Fact]
    public void Sidecar_Factory_RejectsNdimOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DataValue.FromMultiDimArrayInSidecar(DataKind.Float32, offset: 0, length: 16, ndim: 1));
    }

    [Fact]
    public void Sidecar_Factory_RejectsByteArrayKind()
    {
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromMultiDimArrayInSidecar(DataKind.UInt8, offset: 0, length: 16, ndim: 2));
    }
}
