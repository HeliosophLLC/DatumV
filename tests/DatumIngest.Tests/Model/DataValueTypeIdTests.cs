using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

public sealed class DataValueTypeIdTests
{
    // ─────────────── FromStruct + TypeId ───────────────

    [Fact]
    public void FromUntypedStruct_TypeId_IsZero()
    {
        Arena arena = new();
        DataValue v = DataValue.FromUntypedStruct([], arena);
        Assert.Equal((ushort)0, v.TypeId);
    }

    [Fact]
    public void FromStruct_WithTypeId_RoundTrips()
    {
        Arena arena = new();
        DataValue v = DataValue.FromStruct([], arena, typeId: 42);
        Assert.Equal((ushort)42, v.TypeId);
    }

    [Fact]
    public void FromStruct_MaxTypeId_RoundTrips()
    {
        Arena arena = new();
        DataValue v = DataValue.FromStruct([], arena, typeId: ushort.MaxValue);
        Assert.Equal(ushort.MaxValue, v.TypeId);
    }

    [Fact]
    public void FromStruct_WithTypeId_PreservesFields()
    {
        Arena arena = new();
        DataValue[] fields = [DataValue.FromInt32(7), DataValue.FromBoolean(true)];
        DataValue v = DataValue.FromStruct(fields, arena, typeId: 5);

        Assert.Equal(DataKind.Struct, v.Kind);
        Assert.False(v.IsNull);
        DataValue[] roundTripped = v.AsStruct(arena);
        Assert.Equal(2, roundTripped.Length);
        Assert.Equal(7, roundTripped[0].AsInt32());
        Assert.True(roundTripped[1].AsBoolean());
    }

    // ─────────────── NullStruct + TypeId ───────────────

    [Fact]
    public void NullUntypedStruct_TypeId_IsZero()
    {
        DataValue v = DataValue.NullUntypedStruct();
        Assert.True(v.IsNull);
        Assert.Equal((ushort)0, v.TypeId);
    }

    [Fact]
    public void NullStruct_WithTypeId_RoundTrips()
    {
        DataValue v = DataValue.NullStruct(typeId: 7);
        Assert.True(v.IsNull);
        Assert.Equal((ushort)7, v.TypeId);
    }

    // ─────────────── TypeId on non-struct values ───────────────

    [Fact]
    public void NonStruct_TypeId_IsAlwaysZero()
    {
        Assert.Equal((ushort)0, DataValue.FromInt32(42).TypeId);
        Assert.Equal((ushort)0, DataValue.FromBoolean(true).TypeId);
        Assert.Equal((ushort)0, DataValue.FromString("hello").TypeId);
        Assert.Equal((ushort)0, DataValue.FromFloat64(3.14).TypeId);
    }

    [Fact]
    public void UntypedStructArray_TypeId_IsZero()
    {
        Arena arena = new();
        DataValue v = DataValue.FromUntypedStructArray([[DataValue.FromInt32(1)]], arena);
        Assert.Equal((ushort)0, v.TypeId);
    }

    // ─────────────── TypeId preserved through NullStruct propagation ───────────────

    [Fact]
    public void NullStruct_CarriesTypeId_FromSource()
    {
        Arena arena = new();
        DataValue source = DataValue.FromStruct([DataValue.FromInt32(1)], arena, typeId: 13);
        DataValue nulled = DataValue.NullStruct(source.TypeId);
        Assert.Equal((ushort)13, nulled.TypeId);
        Assert.True(nulled.IsNull);
    }

    // ─────────────── Re-wrap round-trip ───────────────

    [Fact]
    public void TypeId_RoundTripsThroughAsStruct_AndFromStruct()
    {
        Arena arena = new();
        DataValue original = DataValue.FromStruct(
            [DataValue.FromInt32(99)], arena, typeId: 8);

        DataValue[] fields = original.AsStruct(arena);
        DataValue rewrapped = DataValue.FromStruct(fields, arena, typeId: original.TypeId);

        Assert.Equal((ushort)8, rewrapped.TypeId);
        Assert.Equal(99, rewrapped.AsStruct(arena)[0].AsInt32());
    }

    // ─────────────── Array<Struct>: per-element TypeId ───────────────

    [Fact]
    public void StructArray_N1_Inline_PerElementTypeId_RoundTrips()
    {
        // The motivating case: a single-detection SCRFD result. The N=1 inline
        // layout used to lose TypeId because `_charCount` carried the element
        // count. After the slot-resident TypeId change, each element's TypeId
        // rides in its own slot's reserved bytes — so even N=1 round-trips.
        Arena arena = new();
        DataValue[] fields = [DataValue.FromInt32(7), DataValue.FromBoolean(true)];
        DataValue v = DataValue.FromStructArray([fields], arena, typeId: 99);

        Assert.True(v.IsArray);
        Assert.True(v.IsInline);
        DataValue[] elements = v.AsStructArray(arena);
        Assert.Single(elements);
        Assert.Equal(DataKind.Struct, elements[0].Kind);
        Assert.Equal((ushort)99, elements[0].TypeId);

        DataValue[] elemFields = elements[0].AsStruct(arena);
        Assert.Equal(7, elemFields[0].AsInt32());
        Assert.True(elemFields[1].AsBoolean());
    }

    [Fact]
    public void StructArray_N3_InArena_PerElementTypeId_RoundTrips()
    {
        // The N≥2 path went through the slot block. After the change every
        // slot carries its own TypeId; per-element reads recover it.
        Arena arena = new();
        DataValue[] r0 = [DataValue.FromInt32(1)];
        DataValue[] r1 = [DataValue.FromInt32(2)];
        DataValue[] r2 = [DataValue.FromInt32(3)];
        DataValue v = DataValue.FromStructArray([r0, r1, r2], arena, typeId: 42);

        Assert.True(v.IsArray);
        Assert.False(v.IsInline);
        DataValue[] elements = v.AsStructArray(arena);
        Assert.Equal(3, elements.Length);
        Assert.All(elements, e => Assert.Equal((ushort)42, e.TypeId));
        Assert.All(elements, e => Assert.Equal(DataKind.Struct, e.Kind));
    }

    [Fact]
    public void StructArray_PerElement_DistinctTypeIdsViaUnsafePath_NotYetSupported()
    {
        // Documents current behaviour: FromStructArray takes a *single* element
        // TypeId stamped on every slot. Heterogeneous Array<Struct> (different
        // element shapes per row) would need a per-element-TypeId overload —
        // not exposed today, but the slot layout supports it.
        Arena arena = new();
        DataValue[] r0 = [DataValue.FromInt32(1)];
        DataValue[] r1 = [DataValue.FromInt32(2)];
        DataValue v = DataValue.FromStructArray([r0, r1], arena, typeId: 5);

        DataValue[] elements = v.AsStructArray(arena);
        Assert.Equal((ushort)5, elements[0].TypeId);
        Assert.Equal((ushort)5, elements[1].TypeId);
    }

    [Fact]
    public void StructArray_Container_NoLongerCarriesArrayTypeId()
    {
        // Array containers used to stamp the array's TypeId on `_charCount`.
        // After the per-element layout, the container's TypeId is always 0 —
        // each element self-describes via its slot. This test pins the
        // contract so a regression toward the old container-TypeId model is
        // visible.
        Arena arena = new();
        DataValue[] r0 = [DataValue.FromInt32(1)];
        DataValue[] r1 = [DataValue.FromInt32(2)];
        DataValue v = DataValue.FromStructArray([r0, r1], arena, typeId: 7);

        Assert.True(v.IsArray);
        Assert.Equal((ushort)0, v.TypeId);
    }
}
