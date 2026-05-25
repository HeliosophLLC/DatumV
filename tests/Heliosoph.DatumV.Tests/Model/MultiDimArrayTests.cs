using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Model;

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
    public void Validate_AcceptsByteArrayKind()
    {
        // Slice B: multi-dim UInt8 is supported. The byte-count ElementCount path
        // subtracts the shape prefix; accessors do the same.
        Arena arena = CreateArena();
        DataValue value = DataValue.FromArenaMultiDimArray<byte>(
            [1, 2, 3, 4], [2, 2], DataKind.UInt8, arena);
        Assert.True(value.IsArray);
        Assert.True(value.IsMultiDim);
        Assert.Equal(DataKind.UInt8, value.Kind);
        Assert.Equal(4, value.ElementCount);
    }

    [Fact]
    public void Validate_RejectsReferenceElementKinds()
    {
        // String + Image have their own multi-dim factories
        // (FromArenaMultiDimStringArray / FromArenaMultiDimImageArray) and are
        // no longer rejected by the fixed-width factory. Struct / Mesh / Audio /
        // Video / Json / PointCloud still reject until their per-kind factories land.
        Arena arena = CreateArena();
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimArray<byte>(new byte[4], [2, 2], DataKind.Struct, arena));
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimArray<byte>(new byte[4], [2, 2], DataKind.Mesh, arena));
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimArray<byte>(new byte[4], [2, 2], DataKind.Json, arena));
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
    public void Sidecar_Factory_AcceptsByteArrayKind()
    {
        // Slice B: UInt8 sidecar multi-dim is supported. AsUInt8Array / AsByteSpan
        // skip the shape prefix when IsMultiDim is set.
        DataValue value = DataValue.FromMultiDimArrayInSidecar(
            DataKind.UInt8, offset: 0, length: 8 + 4, ndim: 2, storeId: 2);

        Assert.True(value.IsArray);
        Assert.True(value.IsMultiDim);
        Assert.True(value.IsInSidecar);
        Assert.Equal(DataKind.UInt8, value.Kind);
        Assert.Equal(2, value.Ndim);
    }

    [Fact]
    public void Sidecar_Factory_AcceptsString()
    {
        // String got its own multi-dim factory in Slice A; the sidecar variant
        // accepts String too (the slot block lives in the sidecar; AsStringArray
        // resolves elements via the registry).
        DataValue value = DataValue.FromMultiDimArrayInSidecar(
            DataKind.String, offset: 0, length: 8 + 4 * ArraySlot.SizeBytes, ndim: 2, storeId: 3);

        Assert.True(value.IsArray);
        Assert.True(value.IsMultiDim);
        Assert.True(value.IsInSidecar);
        Assert.Equal(DataKind.String, value.Kind);
        Assert.Equal(2, value.Ndim);
    }

    // ───────────────────── Multi-dim String[] (Slice A) ─────────────────────

    [Fact]
    public void Arena_String_2x2_RoundTripsShapeAndElements()
    {
        string[] data = ["alpha", "beta", "gamma", "delta"];
        int[] shape = [2, 2];
        Arena arena = CreateArena();

        DataValue value = DataValue.FromArenaMultiDimStringArray(data, shape, arena);

        Assert.True(value.IsArray);
        Assert.True(value.IsMultiDim);
        Assert.False(value.IsInlineArray);
        Assert.True(value.IsArenaBacked);
        Assert.Equal(DataKind.String, value.Kind);
        Assert.Equal(2, value.Ndim);

        Assert.Equal(shape, value.GetShape(arena).ToArray());
        Assert.Equal(4, value.ElementCount);

        string[] recovered = value.AsStringArray(arena);
        Assert.Equal(data, recovered);
    }

    [Fact]
    public void Arena_String_2x3_LargerStrings_RoundTrip()
    {
        // Longer strings exercise the per-element store.StoreString path and
        // make sure the slot block decodes each element's (offset, length)
        // correctly after the shape prefix.
        string[] data = [
            "the quick brown fox jumps over the lazy dog",
            "lorem ipsum dolor sit amet",
            "",   // empty string mid-array
            "α β γ δ ε ζ η",   // multi-byte UTF-8
            "single",
            "🐉🌊",   // 4-byte UTF-8 each
        ];
        int[] shape = [2, 3];
        Arena arena = CreateArena();

        DataValue value = DataValue.FromArenaMultiDimStringArray(data, shape, arena);

        Assert.Equal(6, value.ElementCount);
        Assert.Equal(shape, value.GetShape(arena).ToArray());
        Assert.Equal(data, value.AsStringArray(arena));
    }

    [Fact]
    public void Arena_String_3D_2x2x2()
    {
        string[] data = ["a", "b", "c", "d", "e", "f", "g", "h"];
        int[] shape = [2, 2, 2];
        Arena arena = CreateArena();

        DataValue value = DataValue.FromArenaMultiDimStringArray(data, shape, arena);

        Assert.Equal(3, value.Ndim);
        Assert.Equal(shape, value.GetShape(arena).ToArray());
        Assert.Equal(data, value.AsStringArray(arena));
    }

    [Fact]
    public void Arena_String_RejectsNdimLessThanTwo()
    {
        Arena arena = CreateArena();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DataValue.FromArenaMultiDimStringArray(["a", "b", "c"], [3], arena));
    }

    [Fact]
    public void Arena_String_RejectsShapeProductMismatch()
    {
        Arena arena = CreateArena();
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimStringArray(["a", "b", "c"], [2, 2], arena));
    }

    [Fact]
    public void Arena_String_RejectsNonPositiveDim()
    {
        Arena arena = CreateArena();
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimStringArray([], [2, 0], arena));
    }

    [Fact]
    public void Arena_String_RejectsNullElement()
    {
        Arena arena = CreateArena();
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimStringArray(["a", null!, "c", "d"], [2, 2], arena));
    }

    [Fact]
    public void Arena_String_SameShapeSameElements_AreEqual()
    {
        // Same allocation → byte-identical → equal. Cross-arena equality for
        // Array<String> is currently conservative-false (no content hash stamped),
        // matching 1-D Array<String> behavior — covered in MultiDimEqualityTests.
        Arena arena = CreateArena();
        DataValue a = DataValue.FromArenaMultiDimStringArray(["x", "y", "z", "w"], [2, 2], arena);
        Assert.Equal(a, a);
        Assert.Equal(a.GetHashCode(), a.GetHashCode());
    }

    [Fact]
    public void Arena_String_DifferentShape_NotEqual()
    {
        // [2,3] and [3,2] of the same elements live at different arena offsets
        // so the offset-based equality fast path already separates them; ndim
        // also differs in _charCount's high byte.
        Arena arena = CreateArena();
        string[] data = ["a", "b", "c", "d", "e", "f"];
        DataValue v23 = DataValue.FromArenaMultiDimStringArray(data, [2, 3], arena);
        DataValue v32 = DataValue.FromArenaMultiDimStringArray(data, [3, 2], arena);

        Assert.NotEqual(v23, v32);
        Assert.NotEqual(2, v32.Ndim - v23.Ndim);   // both rank 2
        Assert.Equal([2, 3], v23.GetShape(arena).ToArray());
        Assert.Equal([3, 2], v32.GetShape(arena).ToArray());
    }

    [Fact]
    public void Arena_String_FlatVsMultiDim_NotEqual()
    {
        // Same element strings, one flat, one multi-dim — IsMultiDim flag differs
        // → _flags differs → not equal. (Mirrors the primitive multi-dim invariant.)
        Arena arena = CreateArena();
        string[] data = ["a", "b", "c", "d"];

        DataValue flat = DataValue.FromStringArray(data, arena);
        DataValue multi = DataValue.FromArenaMultiDimStringArray(data, [2, 2], arena);

        Assert.NotEqual(flat, multi);
        Assert.False(flat.IsMultiDim);
        Assert.True(multi.IsMultiDim);
    }

    // ───────────────────── Multi-dim UInt8[] (Slice B) ─────────────────────

    [Fact]
    public void Arena_UInt8_2x3_RoundTripsShapeAndElements()
    {
        byte[] data = [10, 20, 30, 40, 50, 60];
        int[] shape = [2, 3];
        Arena arena = CreateArena();

        DataValue value = DataValue.FromArenaMultiDimArray<byte>(data, shape, DataKind.UInt8, arena);

        Assert.True(value.IsArray);
        Assert.True(value.IsMultiDim);
        Assert.True(value.IsByteArrayKind);
        Assert.Equal(DataKind.UInt8, value.Kind);
        Assert.Equal(2, value.Ndim);

        Assert.Equal(shape, value.GetShape(arena).ToArray());
        Assert.Equal(6, value.ElementCount);
        Assert.Equal(data, value.AsArraySpan<byte>(arena).ToArray());
        Assert.Equal(data, value.AsUInt8Array(arena));
        Assert.Equal(data, value.AsByteSpan(arena).ToArray());
    }

    [Fact]
    public void Arena_UInt8_3D_2x2x2()
    {
        byte[] data = [0, 1, 2, 3, 4, 5, 6, 7];
        int[] shape = [2, 2, 2];
        Arena arena = CreateArena();

        DataValue value = DataValue.FromArenaMultiDimArray<byte>(data, shape, DataKind.UInt8, arena);

        Assert.Equal(3, value.Ndim);
        Assert.Equal(8, value.ElementCount);
        Assert.Equal(shape, value.GetShape(arena).ToArray());
        Assert.Equal(data, value.AsUInt8Array(arena));
    }

    [Fact]
    public void Sidecar_Factory_AcceptsImage()
    {
        // Image sidecar multi-dim is supported. AsImageArray skips the
        // shape prefix when IsMultiDim is set.
        DataValue value = DataValue.FromMultiDimArrayInSidecar(
            DataKind.Image, offset: 0, length: 8 + 4 * ArraySlot.SizeBytes, ndim: 2, storeId: 5);

        Assert.True(value.IsArray);
        Assert.True(value.IsMultiDim);
        Assert.True(value.IsInSidecar);
        Assert.Equal(DataKind.Image, value.Kind);
        Assert.Equal(2, value.Ndim);
    }

    // ───────────────────── Multi-dim Image[] (Slice C) ─────────────────────

    /// <summary>Synthesizes a tiny 1×1 PNG byte payload distinguishable by a tag byte.</summary>
    private static byte[] FakeImageBytes(byte tag) =>
        [0x89, 0x50, 0x4E, 0x47, tag, 0x00, 0x01, 0x02];

    [Fact]
    public void Arena_Image_2x2_RoundTripsShapeAndElements()
    {
        byte[][] data = [
            FakeImageBytes(1), FakeImageBytes(2),
            FakeImageBytes(3), FakeImageBytes(4),
        ];
        int[] shape = [2, 2];
        Arena arena = CreateArena();

        DataValue value = DataValue.FromArenaMultiDimImageArray(data, shape, arena);

        Assert.True(value.IsArray);
        Assert.True(value.IsMultiDim);
        Assert.True(value.IsArenaBacked);
        Assert.Equal(DataKind.Image, value.Kind);
        Assert.Equal(2, value.Ndim);

        Assert.Equal(shape, value.GetShape(arena).ToArray());
        Assert.Equal(4, value.ElementCount);

        byte[][] recovered = value.AsImageArray(arena);
        Assert.Equal(data.Length, recovered.Length);
        for (int i = 0; i < data.Length; i++)
        {
            Assert.Equal(data[i], recovered[i]);
        }
    }

    [Fact]
    public void Arena_Image_2x3_VariousSizes()
    {
        // Mixed-length payloads exercise per-element offset/length math after
        // the shape prefix.
        byte[][] data = [
            [0xFF],                                     // 1 byte
            [0xFF, 0xD8, 0xFF],                         // 3 bytes (JPEG SOI-ish)
            FakeImageBytes(0xA),
            new byte[1024],                             // 1 KiB
            [0x47, 0x49, 0x46, 0x38, 0x39, 0x61],       // "GIF89a"
            [0x00],
        ];
        int[] shape = [2, 3];
        Arena arena = CreateArena();

        DataValue value = DataValue.FromArenaMultiDimImageArray(data, shape, arena);

        Assert.Equal(6, value.ElementCount);
        Assert.Equal(shape, value.GetShape(arena).ToArray());

        byte[][] recovered = value.AsImageArray(arena);
        for (int i = 0; i < data.Length; i++) Assert.Equal(data[i], recovered[i]);
    }

    [Fact]
    public void Arena_Image_3D_2x2x2()
    {
        byte[][] data = new byte[8][];
        for (int i = 0; i < 8; i++) data[i] = FakeImageBytes((byte)i);
        int[] shape = [2, 2, 2];
        Arena arena = CreateArena();

        DataValue value = DataValue.FromArenaMultiDimImageArray(data, shape, arena);

        Assert.Equal(3, value.Ndim);
        Assert.Equal(shape, value.GetShape(arena).ToArray());
        Assert.Equal(8, value.ElementCount);
    }

    [Fact]
    public void Arena_Image_RejectsNdimLessThanTwo()
    {
        Arena arena = CreateArena();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DataValue.FromArenaMultiDimImageArray(
                [FakeImageBytes(1), FakeImageBytes(2), FakeImageBytes(3)], [3], arena));
    }

    [Fact]
    public void Arena_Image_RejectsShapeProductMismatch()
    {
        Arena arena = CreateArena();
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimImageArray(
                [FakeImageBytes(1), FakeImageBytes(2), FakeImageBytes(3)], [2, 2], arena));
    }

    [Fact]
    public void Arena_Image_RejectsNullElement()
    {
        Arena arena = CreateArena();
        Assert.Throws<ArgumentException>(() =>
            DataValue.FromArenaMultiDimImageArray(
                [FakeImageBytes(1), null!, FakeImageBytes(3), FakeImageBytes(4)],
                [2, 2], arena));
    }

    [Fact]
    public void Arena_Image_FlatVsMultiDim_NotEqual()
    {
        Arena arena = CreateArena();
        byte[][] data = [FakeImageBytes(1), FakeImageBytes(2), FakeImageBytes(3), FakeImageBytes(4)];

        DataValue flat = DataValue.FromImageArray(data, arena);
        DataValue multi = DataValue.FromArenaMultiDimImageArray(data, [2, 2], arena);

        Assert.NotEqual(flat, multi);
        Assert.False(flat.IsMultiDim);
        Assert.True(multi.IsMultiDim);
    }

    [Fact]
    public void Arena_UInt8_FlatVsMultiDim_NotEqual()
    {
        // Same byte content, one flat (no shape prefix), one multi-dim (with
        // prefix). IsMultiDim flag → _flags differ → not equal.
        Arena arena = CreateArena();
        byte[] data = [1, 2, 3, 4];

        DataValue flat = DataValue.FromByteArray(data, arena);
        DataValue multi = DataValue.FromArenaMultiDimArray<byte>(data, [2, 2], DataKind.UInt8, arena);

        Assert.NotEqual(flat, multi);
        Assert.False(flat.IsMultiDim);
        Assert.True(multi.IsMultiDim);
        Assert.Equal(4, flat.ElementCount);
        Assert.Equal(4, multi.ElementCount);   // both report 4 elements (prefix excluded)
    }
}
