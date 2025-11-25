using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Round-trip tests for reference-type array values (Phase 1, additive — see
/// <c>project_reference_type_arrays.md</c>). Covers String arrays today; expands
/// to Image / Struct / nested arrays as those factories land.
/// </summary>
public sealed class ReferenceArrayTests : ServiceTestBase
{
    // ───────────────────── Array<String> ─────────────────────

    [Fact]
    public void StringArray_Empty_RoundTrips()
    {
        Arena arena = new();
        DataValue value = DataValue.FromStringArray([], arena);

        Assert.Equal(DataKind.String, value.Kind);
        Assert.True(value.IsArray);
        Assert.True(value.IsInline);
        Assert.Equal([], value.AsStringArray(arena));
    }

    [Fact]
    public void StringArray_Single_FitsInline()
    {
        Arena arena = new();
        DataValue value = DataValue.FromStringArray(["hello"], arena);

        Assert.Equal(DataKind.String, value.Kind);
        Assert.True(value.IsArray);
        // N=1 fits inline: the DataValue holds the slot, no slot block needed.
        Assert.True(value.IsInline);

        string[] result = value.AsStringArray(arena);
        Assert.Single(result);
        Assert.Equal("hello", result[0]);
    }

    [Fact]
    public void StringArray_Multiple_UsesArenaSlotBlock()
    {
        Arena arena = new();
        DataValue value = DataValue.FromStringArray(["alpha", "beta", "gamma"], arena);

        Assert.Equal(DataKind.String, value.Kind);
        Assert.True(value.IsArray);
        // N≥2 lives in the arena (slot block + element bytes both there).
        Assert.False(value.IsInline);

        string[] result = value.AsStringArray(arena);
        Assert.Equal(["alpha", "beta", "gamma"], result);
    }

    [Fact]
    public void StringArray_LongStringElements_RoundTrip()
    {
        Arena arena = new();
        // Each element exceeds the 16-byte inline cap; ensures slot offsets
        // resolve correctly into the arena's variable-length region.
        string[] elements = [
            "this is a string longer than sixteen bytes",
            "another long string that needs the heap",
            "third entry, also clearly longer than the inline limit",
        ];
        DataValue value = DataValue.FromStringArray(elements, arena);

        string[] result = value.AsStringArray(arena);
        Assert.Equal(elements, result);
    }

    [Fact]
    public void StringArray_LargeArray_RoundTrip()
    {
        Arena arena = new();
        string[] elements = new string[256];
        for (int i = 0; i < elements.Length; i++)
        {
            elements[i] = $"element-{i:D4}";
        }
        DataValue value = DataValue.FromStringArray(elements, arena);

        string[] result = value.AsStringArray(arena);
        Assert.Equal(elements, result);
    }

    [Fact]
    public void StringArray_KindIsArray_DistinguishedFromScalar()
    {
        Arena arena = new();
        DataValue scalar = DataValue.FromString("hello", arena);
        DataValue oneElementArray = DataValue.FromStringArray(["hello"], arena);

        // Same Kind, different IsArray — the flag is the only discriminator.
        Assert.Equal(scalar.Kind, oneElementArray.Kind);
        Assert.False(scalar.IsArray);
        Assert.True(oneElementArray.IsArray);
    }

    [Fact]
    public void AsStringArray_OnNonArray_Throws()
    {
        Arena arena = new();
        DataValue scalar = DataValue.FromString("hello", arena);

        Assert.Throws<InvalidOperationException>(() => scalar.AsStringArray(arena));
    }

    [Fact]
    public void AsStringArray_OnNullArray_Throws()
    {
        Arena arena = new();
        DataValue typedNull = DataValue.Null(DataKind.String);

        Assert.Throws<InvalidOperationException>(() => typedNull.AsStringArray(arena));
    }

    [Fact]
    public void AsStringArray_OnWrongKindArray_Throws()
    {
        // A byte array (UInt8 + IsArray) is not a String array.
        Arena arena = new();
        DataValue byteArray = DataValue.FromByteArray([1, 2, 3], arena);

        Assert.Throws<InvalidOperationException>(() => byteArray.AsStringArray(arena));
    }

    // ───────────────────── Array<Image> ─────────────────────

    [Fact]
    public void ImageArray_Empty_RoundTrips()
    {
        Arena arena = new();
        DataValue value = DataValue.FromImageArray(ReadOnlySpan<byte[]>.Empty, arena);

        Assert.Equal(DataKind.Image, value.Kind);
        Assert.True(value.IsArray);
        Assert.True(value.IsInline);
        Assert.Empty(value.AsImageArray(arena));
    }

    [Fact]
    public void ImageArray_Single_FitsInline()
    {
        Arena arena = new();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        DataValue value = DataValue.FromImageArray([png], arena);

        Assert.Equal(DataKind.Image, value.Kind);
        Assert.True(value.IsArray);
        Assert.True(value.IsInline);

        byte[][] result = value.AsImageArray(arena);
        Assert.Single(result);
        Assert.Equal(png, result[0]);
    }

    [Fact]
    public void ImageArray_Multiple_UsesArenaSlotBlock()
    {
        Arena arena = new();
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        byte[] jpg = [0xFF, 0xD8, 0xFF, 0xE0];
        byte[] gif = [0x47, 0x49, 0x46, 0x38];
        DataValue value = DataValue.FromImageArray([png, jpg, gif], arena);

        Assert.True(value.IsArray);
        Assert.False(value.IsInline);

        byte[][] result = value.AsImageArray(arena);
        Assert.Equal(3, result.Length);
        Assert.Equal(png, result[0]);
        Assert.Equal(jpg, result[1]);
        Assert.Equal(gif, result[2]);
    }

    [Fact]
    public void ImageArray_KindIsArray_DistinguishedFromScalarImage()
    {
        Arena arena = new();
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47];
        DataValue scalar = DataValue.FromImage(bytes, arena);
        DataValue oneElementArray = DataValue.FromImageArray([bytes], arena);

        Assert.Equal(DataKind.Image, scalar.Kind);
        Assert.Equal(DataKind.Image, oneElementArray.Kind);
        Assert.False(scalar.IsArray);
        Assert.True(oneElementArray.IsArray);
    }

    [Fact]
    public void AsImageArray_OnNonArray_Throws()
    {
        Arena arena = new();
        DataValue scalar = DataValue.FromImage([0xFF, 0xD8], arena);

        Assert.Throws<InvalidOperationException>(() => scalar.AsImageArray(arena));
    }

    // ───────────────────── Array<Struct> ─────────────────────

    [Fact]
    public void StructArray_Empty_RoundTrips()
    {
        Arena arena = new();
        DataValue value = DataValue.FromStructArray(ReadOnlySpan<DataValue[]>.Empty, arena);

        Assert.Equal(DataKind.Struct, value.Kind);
        Assert.True(value.IsArray);
        Assert.True(value.IsInline);
        Assert.Empty(value.AsStructArray(arena));
    }

    [Fact]
    public void StructArray_Single_FitsInline()
    {
        Arena arena = new();
        DataValue[] fields = [DataValue.FromString("alice", arena), DataValue.FromInt32(42)];
        DataValue value = DataValue.FromStructArray([fields], arena);

        Assert.Equal(DataKind.Struct, value.Kind);
        Assert.True(value.IsArray);
        Assert.True(value.IsInline);

        DataValue[][] result = value.AsStructArray(arena);
        Assert.Single(result);
        Assert.Equal(2, result[0].Length);
        Assert.Equal("alice", result[0][0].AsString(arena));
        Assert.Equal(42, result[0][1].AsInt32());
    }

    [Fact]
    public void StructArray_Multiple_UsesArenaSlotBlock()
    {
        Arena arena = new();
        DataValue[] alice = [DataValue.FromString("alice", arena), DataValue.FromInt32(30)];
        DataValue[] bob = [DataValue.FromString("bob", arena), DataValue.FromInt32(25)];
        DataValue[] carol = [DataValue.FromString("carol", arena), DataValue.FromInt32(40)];
        DataValue value = DataValue.FromStructArray([alice, bob, carol], arena);

        Assert.True(value.IsArray);
        Assert.False(value.IsInline);

        DataValue[][] result = value.AsStructArray(arena);
        Assert.Equal(3, result.Length);
        Assert.Equal("alice", result[0][0].AsString(arena));
        Assert.Equal(30, result[0][1].AsInt32());
        Assert.Equal("bob", result[1][0].AsString(arena));
        Assert.Equal(25, result[1][1].AsInt32());
        Assert.Equal("carol", result[2][0].AsString(arena));
        Assert.Equal(40, result[2][1].AsInt32());
    }

    [Fact]
    public void StructArray_KindIsArray_DistinguishedFromScalarStruct()
    {
        Arena arena = new();
        DataValue[] fields = [DataValue.FromString("alice", arena), DataValue.FromInt32(30)];
        DataValue scalar = DataValue.FromStruct((short)fields.Length, fields, arena);
        DataValue oneElementArray = DataValue.FromStructArray([fields], arena);

        Assert.Equal(DataKind.Struct, scalar.Kind);
        Assert.Equal(DataKind.Struct, oneElementArray.Kind);
        Assert.False(scalar.IsArray);
        Assert.True(oneElementArray.IsArray);
    }

    [Fact]
    public void AsStructArray_OnNonArray_Throws()
    {
        Arena arena = new();
        DataValue[] fields = [DataValue.FromInt32(1)];
        DataValue scalar = DataValue.FromStruct((short)fields.Length, fields, arena);

        Assert.Throws<InvalidOperationException>(() => scalar.AsStructArray(arena));
    }

    // ───────────────────── Stabilize (deep-copy across stores) ─────────────────────

    [Fact]
    public void Stabilize_StringArray_DeepCopiesAcrossStores()
    {
        Arena source = new();
        Arena retention = new();

        string[] elements = [
            "this is a long string that does not fit inline",
            "another long string for the same array",
        ];
        DataValue original = DataValue.FromStringArray(elements, source);

        DataValue stable = DataValueRetention.Stabilize(original, source, retention);

        // Disposing the source must not affect the stabilised array.
        source.Dispose();

        string[] result = stable.AsStringArray(retention);
        Assert.Equal(elements, result);
    }

    [Fact]
    public void Stabilize_StringArray_InlineN1_DeepCopies()
    {
        Arena source = new();
        Arena retention = new();

        // Even an inline N=1 array points its slot at sourceStore — needs deep copy.
        DataValue original = DataValue.FromStringArray(["a single value, longer than 16 bytes"], source);
        Assert.True(original.IsInline);

        DataValue stable = DataValueRetention.Stabilize(original, source, retention);

        source.Dispose();

        string[] result = stable.AsStringArray(retention);
        Assert.Equal(["a single value, longer than 16 bytes"], result);
    }

    [Fact]
    public void Stabilize_ImageArray_DeepCopiesAcrossStores()
    {
        Arena source = new();
        Arena retention = new();

        byte[][] elements = [
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10],
        ];
        DataValue original = DataValue.FromImageArray(elements, source);

        DataValue stable = DataValueRetention.Stabilize(original, source, retention);
        source.Dispose();

        byte[][] result = stable.AsImageArray(retention);
        Assert.Equal(2, result.Length);
        Assert.Equal(elements[0], result[0]);
        Assert.Equal(elements[1], result[1]);
    }

    [Fact]
    public void Stabilize_StructArray_DeepCopiesIncludingFieldStrings()
    {
        Arena source = new();
        Arena retention = new();

        // Each struct contains a long-string field — proves the field-level
        // recursion in StabilizeStructArray actually copies field payloads,
        // not just the slot block.
        DataValue[] alice = [
            DataValue.FromString("alice in wonderland with a long surname", source),
            DataValue.FromInt32(30),
        ];
        DataValue[] bob = [
            DataValue.FromString("robert with a long string for testing", source),
            DataValue.FromInt32(25),
        ];
        DataValue original = DataValue.FromStructArray([alice, bob], source);

        DataValue stable = DataValueRetention.Stabilize(original, source, retention);
        source.Dispose();

        DataValue[][] result = stable.AsStructArray(retention);
        Assert.Equal(2, result.Length);
        Assert.Equal("alice in wonderland with a long surname", result[0][0].AsString(retention));
        Assert.Equal(30, result[0][1].AsInt32());
        Assert.Equal("robert with a long string for testing", result[1][0].AsString(retention));
        Assert.Equal(25, result[1][1].AsInt32());
    }

    [Fact]
    public void Stabilize_ReferenceArray_SameStore_PassesThrough()
    {
        // Same-store fast path — no copy, slot offsets stay valid.
        Arena arena = new();
        DataValue original = DataValue.FromStringArray(["alpha", "beta"], arena);

        DataValue stable = DataValueRetention.Stabilize(original, arena, arena);

        // Should be the same value (no copy taken).
        Assert.Equal(original.Kind, stable.Kind);
        Assert.Equal(original.IsArray, stable.IsArray);
        Assert.Equal(["alpha", "beta"], stable.AsStringArray(arena));
    }

    [Fact]
    public void Stabilize_EmptyReferenceArray_PassesThrough()
    {
        Arena source = new();
        Arena retention = new();

        DataValue empty = DataValue.FromStringArray([], source);
        DataValue stable = DataValueRetention.Stabilize(empty, source, retention);

        // N=0 has no per-element data; result is just an empty array.
        Assert.True(stable.IsInline);
        Assert.Empty(stable.AsStringArray(retention));
    }
}
