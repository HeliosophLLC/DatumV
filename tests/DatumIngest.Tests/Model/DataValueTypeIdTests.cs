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
}
