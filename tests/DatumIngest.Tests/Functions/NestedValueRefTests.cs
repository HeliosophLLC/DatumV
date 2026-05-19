using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Functions;

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

        // Typed-array carrier: Kind=elementKind, IsArray flag set.
        Assert.Equal(DataKind.String, arrayRef.Kind);
        Assert.True(arrayRef.IsArray);
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
            Assert.Equal(DataKind.String, dv.Kind);
            Assert.True(dv.IsArray);
            Assert.Equal(DataKind.String, dv.ArrayElementKind);
            string[] dvElements = dv.AsStringArray(arena);
            Assert.Equal(3, dvElements.Length);
            Assert.Equal("eggs", dvElements[0]);
            Assert.Equal("plate", dvElements[1]);
            Assert.Equal("tablecloth", dvElements[2]);
        }
        finally { ReturnArena(arena); }
    }

    // ─── Fixed-width primitive arrays (Int32, Float32, Boolean, …) ────────

    [Fact]
    public void FromArray_Int32_InlineSize_RoundTripsViaAsArraySpan()
    {
        // 3 × 4 bytes = 12 bytes — fits in the 16-byte inline-array payload.
        ValueRef arrayRef = ValueRef.FromArray(DataKind.Int32,
        [
            ValueRef.FromInt32(1),
            ValueRef.FromInt32(2),
            ValueRef.FromInt32(3),
        ]);

        Assert.Equal(DataKind.Int32, arrayRef.Kind);
        Assert.True(arrayRef.IsArray);
        Assert.Equal(DataKind.Int32, arrayRef.ArrayElementKind);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            Assert.Equal(DataKind.Int32, dv.Kind);
            Assert.True(dv.IsArray);
            Assert.True(dv.IsInlineArray);
            ReadOnlySpan<int> ints = dv.AsArraySpan<int>(arena);
            Assert.Equal(3, ints.Length);
            Assert.Equal(1, ints[0]);
            Assert.Equal(2, ints[1]);
            Assert.Equal(3, ints[2]);
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromArray_Int32_ArenaSize_RoundTripsViaAsArraySpan()
    {
        // 10 × 4 bytes = 40 bytes — exceeds the 16-byte inline cap, lands in arena.
        ValueRef[] elements = new ValueRef[10];
        for (int i = 0; i < 10; i++) elements[i] = ValueRef.FromInt32(i * 100);
        ValueRef arrayRef = ValueRef.FromArray(DataKind.Int32, elements);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            Assert.Equal(DataKind.Int32, dv.Kind);
            Assert.True(dv.IsArray);
            Assert.False(dv.IsInlineArray);
            ReadOnlySpan<int> ints = dv.AsArraySpan<int>(arena);
            Assert.Equal(10, ints.Length);
            for (int i = 0; i < 10; i++) Assert.Equal(i * 100, ints[i]);
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromArray_Float32_RoundTripsViaAsArraySpan()
    {
        ValueRef arrayRef = ValueRef.FromArray(DataKind.Float32,
        [
            ValueRef.FromFloat32(0.5f),
            ValueRef.FromFloat32(-1.25f),
            ValueRef.FromFloat32(float.NaN),
            ValueRef.FromFloat32(3.14f),
            ValueRef.FromFloat32(99.99f),
        ]);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            ReadOnlySpan<float> floats = dv.AsArraySpan<float>(arena);
            Assert.Equal(5, floats.Length);
            Assert.Equal(0.5f, floats[0]);
            Assert.Equal(-1.25f, floats[1]);
            Assert.True(float.IsNaN(floats[2]));
            Assert.Equal(3.14f, floats[3]);
            Assert.Equal(99.99f, floats[4]);
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromArray_Boolean_RoundTripsViaAsArraySpan()
    {
        // Bool is 1 byte per element — 4 elements pack inline.
        ValueRef arrayRef = ValueRef.FromArray(DataKind.Boolean,
        [
            ValueRef.FromBoolean(true),
            ValueRef.FromBoolean(false),
            ValueRef.FromBoolean(true),
            ValueRef.FromBoolean(true),
        ]);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            ReadOnlySpan<byte> bools = dv.AsArraySpan<byte>(arena);
            Assert.Equal(4, bools.Length);
            Assert.NotEqual(0, bools[0]);
            Assert.Equal(0, bools[1]);
            Assert.NotEqual(0, bools[2]);
            Assert.NotEqual(0, bools[3]);
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromArray_Empty_Int32_RoundTrips()
    {
        ValueRef arrayRef = ValueRef.FromArray(DataKind.Int32, []);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            Assert.Equal(DataKind.Int32, dv.Kind);
            Assert.True(dv.IsArray);
            ReadOnlySpan<int> ints = dv.AsArraySpan<int>(arena);
            Assert.Equal(0, ints.Length);
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromArray_NestedArrayElement_ThrowsAtConstruction()
    {
        // Array<Array<X>> is not supported; the typed-array carrier encodes
        // only the leaf element kind, not nesting. Reject at FromArray time
        // to localise the failure rather than blow up obscurely on materialise.
        ValueRef innerArray = ValueRef.FromArray(DataKind.String,
        [
            ValueRef.FromString("eggs"),
            ValueRef.FromString("plate"),
        ]);

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => ValueRef.FromArray(DataKind.String, [innerArray]));
        Assert.Contains("Nested arrays", ex.Message);
        Assert.Contains("Wrap the inner array in a Struct", ex.Message);
    }

    [Fact]
    public void FromArray_NullStringElement_ThrowsOnMaterialise()
    {
        // Per-element nulls inside an array aren't supported yet; the typed-array
        // factories don't have a null-bitmap, so silently writing nulls would
        // corrupt the slot block. Surface the gap explicitly.
        ValueRef arrayRef = ValueRef.FromArray(DataKind.String,
        [
            ValueRef.FromString("a"),
            ValueRef.Null(DataKind.String),
            ValueRef.FromString("c"),
        ]);

        Arena arena = RentArena();
        try
        {
            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => arrayRef.ToDataValue(arena));
            Assert.Contains("element [1] is null", ex.Message);
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromArray_NullInt32Element_ThrowsOnMaterialise()
    {
        ValueRef arrayRef = ValueRef.FromArray(DataKind.Int32,
        [
            ValueRef.FromInt32(1),
            ValueRef.Null(DataKind.Int32),
            ValueRef.FromInt32(3),
        ]);

        Arena arena = RentArena();
        try
        {
            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => arrayRef.ToDataValue(arena));
            Assert.Contains("element [1] is null", ex.Message);
        }
        finally { ReturnArena(arena); }
    }

    // ─── Bulk primitive-array constructor (FromPrimitiveArray<T>) ──────────

    [Fact]
    public void FromPrimitiveArray_Float32_Large_RoundTripsViaAsArraySpan()
    {
        // Embedder-sized: 1536 floats (CLIP / sentence-transformer territory).
        // Construction must not wrap each float in a ValueRef — the payload is
        // the float[] directly, and bytes flow into the arena in one StoreBytes
        // call at materialise.
        float[] embedding = new float[1536];
        for (int i = 0; i < embedding.Length; i++)
        {
            embedding[i] = MathF.Sin(i * 0.001f);
        }

        ValueRef arrayRef = ValueRef.FromPrimitiveArray(embedding, DataKind.Float32);

        Assert.Equal(DataKind.Float32, arrayRef.Kind);
        Assert.True(arrayRef.IsArray);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            Assert.Equal(DataKind.Float32, dv.Kind);
            Assert.True(dv.IsArray);
            Assert.False(dv.IsInlineArray); // 1536 × 4 = 6144 bytes ≫ 16-byte inline cap
            ReadOnlySpan<float> readback = dv.AsArraySpan<float>(arena);
            Assert.Equal(embedding.Length, readback.Length);
            for (int i = 0; i < embedding.Length; i++)
            {
                Assert.Equal(embedding[i], readback[i]);
            }
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromPrimitiveArray_Int32_RoundTrips()
    {
        int[] values = [-100, 0, 100, 200, 300, 400, 500, 600];
        ValueRef arrayRef = ValueRef.FromPrimitiveArray(values, DataKind.Int32);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            ReadOnlySpan<int> readback = dv.AsArraySpan<int>(arena);
            Assert.Equal(values.Length, readback.Length);
            for (int i = 0; i < values.Length; i++)
            {
                Assert.Equal(values[i], readback[i]);
            }
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromPrimitiveArray_Empty_RoundTrips()
    {
        ValueRef arrayRef = ValueRef.FromPrimitiveArray(Array.Empty<float>(), DataKind.Float32);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            Assert.Equal(DataKind.Float32, dv.Kind);
            Assert.True(dv.IsArray);
            ReadOnlySpan<float> readback = dv.AsArraySpan<float>(arena);
            Assert.Equal(0, readback.Length);
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromPrimitiveArray_SizeMismatch_ThrowsAtConstruction()
    {
        // double is 8 bytes; Float32 is 4 bytes. Caller error.
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            ValueRef.FromPrimitiveArray<double>([1.0, 2.0, 3.0], DataKind.Float32));
        Assert.Contains("Double", ex.Message);
        Assert.Contains("Float32", ex.Message);
    }

    [Fact]
    public void FromPrimitiveArray_TimestampTz_RoundTrips()
    {
        // Under the PG-faithful Timestamp/TimestampTz split, TimestampTz is
        // 8-byte UTC ticks with no per-row offset — a trivial Int64 array.
        // The previous Array<DateTime> rejection is gone; see
        // DataValueTimestampTests for the full slice-1 contract.
        long[] ticks = [0L, 1L, 2L];
        ValueRef arrayRef = ValueRef.FromPrimitiveArray(ticks, DataKind.TimestampTz);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            Assert.Equal(DataKind.TimestampTz, dv.Kind);
            Assert.True(dv.IsArray);
            ReadOnlySpan<long> readback = dv.AsArraySpan<long>(arena);
            Assert.Equal(ticks.Length, readback.Length);
            for (int i = 0; i < ticks.Length; i++)
            {
                Assert.Equal(ticks[i], readback[i]);
            }
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromPrimitiveArray_Float64_RoundTrips()
    {
        double[] values = [Math.PI, Math.E, double.MinValue, double.MaxValue, 0.0, -1.5];
        ValueRef arrayRef = ValueRef.FromPrimitiveArray(values, DataKind.Float64);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            ReadOnlySpan<double> readback = dv.AsArraySpan<double>(arena);
            Assert.Equal(values.Length, readback.Length);
            for (int i = 0; i < values.Length; i++)
            {
                Assert.Equal(values[i], readback[i]);
            }
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromPrimitiveArray_HoldsCallerArrayByReference()
    {
        // No defensive copy at construction — the caller's T[] is the payload.
        // This is the whole point of the bulk constructor (vs. wrapping each
        // element in a ValueRef struct). Verify by checking the carrier kind
        // and that materialisation reads back what we put in (the alternative
        // implementation that pre-extracts bytes would also pass this test;
        // it's a smoke test that the bulk path is wired up at all).
        float[] caller = [1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f];
        ValueRef arrayRef = ValueRef.FromPrimitiveArray(caller, DataKind.Float32);

        Assert.Same(caller, arrayRef.Materialized);
    }

    [Fact]
    public void FromStruct_OfFixedWidthArrays_RoundTrips()
    {
        // A struct holding two fixed-width primitive arrays — the kind of
        // shape SQL expressions can build directly (no model required).
        ValueRef record = ValueRef.FromStruct(
        [
            ValueRef.FromString("series_a"),
            ValueRef.FromArray(DataKind.Int32,
            [
                ValueRef.FromInt32(10),
                ValueRef.FromInt32(20),
                ValueRef.FromInt32(30),
                ValueRef.FromInt32(40),
                ValueRef.FromInt32(50),
            ]),
            ValueRef.FromArray(DataKind.Float32,
            [
                ValueRef.FromFloat32(1.1f),
                ValueRef.FromFloat32(2.2f),
                ValueRef.FromFloat32(3.3f),
            ]),
        ]);

        Arena arena = RentArena();
        try
        {
            DataValue dv = record.ToDataValue(arena);
            DataValue[] fields = dv.AsStruct(arena);
            Assert.Equal("series_a", fields[0].AsString(arena));

            Assert.Equal(DataKind.Int32, fields[1].Kind);
            Assert.True(fields[1].IsArray);
            ReadOnlySpan<int> ints = fields[1].AsArraySpan<int>(arena);
            Assert.Equal(new[] { 10, 20, 30, 40, 50 }, ints.ToArray());

            Assert.Equal(DataKind.Float32, fields[2].Kind);
            Assert.True(fields[2].IsArray);
            ReadOnlySpan<float> floats = fields[2].AsArraySpan<float>(arena);
            Assert.Equal(3, floats.Length);
            Assert.Equal(1.1f, floats[0]);
            Assert.Equal(2.2f, floats[1]);
            Assert.Equal(3.3f, floats[2]);
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
            // Top-level value: typed Array<Struct>.
            Assert.Equal(DataKind.Struct, dv.Kind);
            Assert.True(dv.IsArray);
            DataValue[] structs = dv.AsStructArray(arena);
            Assert.Equal(2, structs.Length);

            // Each element is now a self-describing Struct DataValue. Pull its
            // fields via AsStruct.
            DataValue[] r0 = structs[0].AsStruct(arena);
            Assert.Equal("region_1", r0[0].AsString(arena));
            // Inner Array<String> field: typed-string-array.
            Assert.Equal(DataKind.String, r0[1].Kind);
            Assert.True(r0[1].IsArray);
            string[] r0Labels = r0[1].AsStringArray(arena);
            Assert.Equal(2, r0Labels.Length);
            Assert.Equal("eggs", r0Labels[0]);
            Assert.Equal("plate", r0Labels[1]);

            DataValue[] r1 = structs[1].AsStruct(arena);
            Assert.Equal("region_2", r1[0].AsString(arena));
            string[] r1Labels = r1[1].AsStringArray(arena);
            Assert.Single(r1Labels);
            Assert.Equal("tablecloth", r1Labels[0]);
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
        // Typed-null carrier: Kind=elementKind, IsArray + IsNull flags set.
        Assert.Equal(DataKind.String, nullArr.Kind);
        Assert.True(nullArr.IsArray);
        Assert.Equal(DataKind.String, nullArr.ArrayElementKind);

        Arena arena = RentArena();
        try
        {
            DataValue dv = nullArr.ToDataValue(arena);
            Assert.True(dv.IsNull);
            Assert.Equal(DataKind.String, dv.Kind);
            Assert.True(dv.IsArray);
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
