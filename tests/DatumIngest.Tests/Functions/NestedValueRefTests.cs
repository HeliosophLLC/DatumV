using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for the recursive <see cref="ValueRef"/> shape (struct / array
/// payloads carrying nested <see cref="ValueRef"/>[]). The point: nested
/// non-inline values (long strings, byte arrays, nested arrays/structs)
/// stay in managed memory until the outermost <see cref="ValueRef.ToDataValue"/>
/// recurses through and writes everything to the target arena in one pass.
/// </summary>
public sealed class NestedValueRefTests : ServiceTestBase
{
    private Arena RentArena() => GetService<Pool>().Backing.RentArena();
    private void ReturnArena(Arena arena) => GetService<Pool>().Backing.TryReturn(arena);

    // ─── FromStruct + GetStructFields ──────────────────────────────────────

    [Fact]
    public void FromStruct_SimpleInlineFields_RoundTripsViaToDataValue()
    {
        ValueRef structRef = ValueRef.FromStruct(
        [
            ValueRef.FromInt32(42),
            ValueRef.FromFloat32(3.14f),
            ValueRef.FromBoolean(true),
        ]);

        Assert.Equal(DataKind.Struct, structRef.Kind);
        Assert.False(structRef.IsNull);

        ReadOnlySpan<ValueRef> fields = structRef.GetStructFields();
        Assert.Equal(3, fields.Length);
        Assert.Equal(42, fields[0].AsInt32());
        Assert.Equal(3.14f, fields[1].AsFloat32());
        Assert.True(fields[2].AsBoolean());

        Arena arena = RentArena();
        try
        {
            DataValue dv = structRef.ToDataValue(arena);
            Assert.Equal(DataKind.Struct, dv.Kind);
            Assert.False(dv.IsNull);
            DataValue[] dvFields = dv.AsStruct(arena);
            Assert.Equal(3, dvFields.Length);
            Assert.Equal(42, dvFields[0].AsInt32());
            Assert.Equal(3.14f, dvFields[1].AsFloat32());
            Assert.True(dvFields[2].AsBoolean());
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromStruct_LongStringField_DefersUntilToDataValue()
    {
        // A description string that exceeds the 16-byte inline DataValue budget.
        // The whole point: this never hits an arena while the ValueRef is built.
        string longDescription = "A pile of scrambled eggs on a white plate on top of a checkered tablecloth";

        ValueRef structRef = ValueRef.FromStruct(
        [
            ValueRef.FromString("Plate"),
            ValueRef.FromString(longDescription),
            ValueRef.FromFloat32(120.0f),
        ]);

        // Construction did not throw and field accessor reads the long string
        // straight back from managed memory.
        Assert.Equal(longDescription, structRef.GetStructFields()[1].AsString());

        Arena arena = RentArena();
        try
        {
            DataValue dv = structRef.ToDataValue(arena);
            DataValue[] fields = dv.AsStruct(arena);
            Assert.Equal("Plate", fields[0].AsString(arena));
            Assert.Equal(longDescription, fields[1].AsString(arena));
            Assert.Equal(120.0f, fields[2].AsFloat32());
        }
        finally { ReturnArena(arena); }
    }

    // ─── FromArray + GetArrayElements ──────────────────────────────────────

    [Fact]
    public void FromArray_StringElements_NoArenaUntilMaterialise()
    {
        ValueRef arrayRef = ValueRef.FromArray(DataKind.String,
        [
            ValueRef.FromString("eggs"),
            ValueRef.FromString("plate"),
            ValueRef.FromString("tablecloth"),
        ]);

        Assert.Equal(DataKind.Array, arrayRef.Kind);
        Assert.Equal(DataKind.String, arrayRef.ArrayElementKind);

        ReadOnlySpan<ValueRef> elements = arrayRef.GetArrayElements();
        Assert.Equal(3, elements.Length);
        Assert.Equal("eggs", elements[0].AsString());
        Assert.Equal("plate", elements[1].AsString());
        Assert.Equal("tablecloth", elements[2].AsString());

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            Assert.Equal(DataKind.Array, dv.Kind);
            Assert.Equal(DataKind.String, dv.ArrayElementKind);
            DataValue[] dvElements = dv.AsArray(arena);
            Assert.Equal(3, dvElements.Length);
            Assert.Equal("eggs", dvElements[0].AsString(arena));
            Assert.Equal("plate", dvElements[1].AsString(arena));
            Assert.Equal("tablecloth", dvElements[2].AsString(arena));
        }
        finally { ReturnArena(arena); }
    }

    // ─── Deep nesting: Array<Struct{name, labels: Array<String>}> ──────────

    [Fact]
    public void DeepNesting_ArrayOfStructWithNestedArray_RoundTrips()
    {
        ValueRef arrayOfStructs = ValueRef.FromArray(DataKind.Struct,
        [
            ValueRef.FromStruct(
            [
                ValueRef.FromString("region_1"),
                ValueRef.FromArray(DataKind.String,
                [
                    ValueRef.FromString("eggs"),
                    ValueRef.FromString("plate"),
                ]),
            ]),
            ValueRef.FromStruct(
            [
                ValueRef.FromString("region_2"),
                ValueRef.FromArray(DataKind.String,
                [
                    ValueRef.FromString("tablecloth"),
                ]),
            ]),
        ]);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayOfStructs.ToDataValue(arena);
            DataValue[] structs = dv.AsArray(arena);
            Assert.Equal(2, structs.Length);

            DataValue[] r0 = structs[0].AsStruct(arena);
            Assert.Equal("region_1", r0[0].AsString(arena));
            DataValue[] r0Labels = r0[1].AsArray(arena);
            Assert.Equal(2, r0Labels.Length);
            Assert.Equal("eggs", r0Labels[0].AsString(arena));
            Assert.Equal("plate", r0Labels[1].AsString(arena));

            DataValue[] r1 = structs[1].AsStruct(arena);
            Assert.Equal("region_2", r1[0].AsString(arena));
            DataValue[] r1Labels = r1[1].AsArray(arena);
            Assert.Single(r1Labels);
            Assert.Equal("tablecloth", r1Labels[0].AsString(arena));
        }
        finally { ReturnArena(arena); }
    }

    // ─── Image-in-struct: bytes payload survives the recursion ─────────────

    [Fact]
    public void StructWithImageField_BytesSurviveMaterialisation()
    {
        byte[] fakeImage = new byte[100];
        for (int i = 0; i < fakeImage.Length; i++) fakeImage[i] = (byte)i;

        ValueRef structRef = ValueRef.FromStruct(
        [
            ValueRef.FromString("photo"),
            ValueRef.FromBytes(DataKind.Image, fakeImage),
        ]);

        Arena arena = RentArena();
        try
        {
            DataValue dv = structRef.ToDataValue(arena);
            DataValue[] fields = dv.AsStruct(arena);
            Assert.Equal("photo", fields[0].AsString(arena));
            Assert.Equal(DataKind.Image, fields[1].Kind);
            ReadOnlySpan<byte> readback = fields[1].AsByteSpan(arena, registry: null);
            Assert.Equal(fakeImage.Length, readback.Length);
            for (int i = 0; i < fakeImage.Length; i++)
            {
                Assert.Equal(fakeImage[i], readback[i]);
            }
        }
        finally { ReturnArena(arena); }
    }

    // ─── Null cases ────────────────────────────────────────────────────────

    [Fact]
    public void NullStruct_PassesThrough()
    {
        ValueRef nullStruct = ValueRef.NullStruct(3);
        Assert.True(nullStruct.IsNull);
        Assert.Equal(DataKind.Struct, nullStruct.Kind);

        Arena arena = RentArena();
        try
        {
            DataValue dv = nullStruct.ToDataValue(arena);
            Assert.True(dv.IsNull);
            Assert.Equal(DataKind.Struct, dv.Kind);
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void NullArray_PassesThroughWithElementKind()
    {
        ValueRef nullArr = ValueRef.NullArray(DataKind.String);
        Assert.True(nullArr.IsNull);
        Assert.Equal(DataKind.Array, nullArr.Kind);
        Assert.Equal(DataKind.String, nullArr.ArrayElementKind);

        Arena arena = RentArena();
        try
        {
            DataValue dv = nullArr.ToDataValue(arena);
            Assert.True(dv.IsNull);
            Assert.Equal(DataKind.Array, dv.Kind);
            Assert.Equal(DataKind.String, dv.ArrayElementKind);
        }
        finally { ReturnArena(arena); }
    }

    // ─── Terminal-sink path: traverse without materialising ────────────────

    [Fact]
    public void TerminalSink_TraverseNestedStructure_NoArenaUsed()
    {
        // The value of the recursive design: a consumer that only needs to
        // read fields can recurse into the managed tree without touching
        // any IValueStore. Demonstrate by constructing a deeply nested
        // value, recursing into it, and asserting we never call ToDataValue.
        ValueRef detection = ValueRef.FromStruct(
        [
            ValueRef.FromString("Plate"),
            ValueRef.FromString("description with words that overflow inline"),
            ValueRef.FromFloat32(0.95f),
            ValueRef.FromArray(DataKind.String,
            [
                ValueRef.FromString("eggs"),
                ValueRef.FromString("plate"),
            ]),
        ]);

        // No arena ever used.
        ReadOnlySpan<ValueRef> fields = detection.GetStructFields();
        Assert.Equal("Plate", fields[0].AsString());
        Assert.Equal("description with words that overflow inline", fields[1].AsString());
        Assert.Equal(0.95f, fields[2].AsFloat32());

        ReadOnlySpan<ValueRef> labels = fields[3].GetArrayElements();
        Assert.Equal(2, labels.Length);
        Assert.Equal("eggs", labels[0].AsString());
        Assert.Equal("plate", labels[1].AsString());
    }
}
