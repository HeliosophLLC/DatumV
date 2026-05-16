using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Locks in the typed-vs-untyped API split for struct factories. The point of
/// the split is reviewability: any production code that constructs a struct
/// without a TypeId is grep-able as <c>FromUntyped*</c>; the regular factories
/// require a TypeId argument, so a missing-plumbing site fails to compile rather
/// than silently producing <c>f0..fN</c> output.
/// </summary>
public sealed class StructApiContractTests : ServiceTestBase
{
    [Fact]
    public void FromStruct_Typed_StampsTypeIdOnDataValue()
    {
        Arena arena = CreateArena();
        DataValue v = DataValue.FromStruct([DataValue.FromInt32(1)], arena, typeId: 42);
        Assert.Equal((ushort)42, v.TypeId);
        Assert.Equal(DataKind.Struct, v.Kind);
        Assert.False(v.IsNull);
    }

    [Fact]
    public void FromUntypedStruct_HasZeroTypeId()
    {
        Arena arena = CreateArena();
        DataValue v = DataValue.FromUntypedStruct([DataValue.FromInt32(1)], arena);
        Assert.Equal((ushort)0, v.TypeId);
        Assert.Equal(DataKind.Struct, v.Kind);
        Assert.False(v.IsNull);
    }

    [Fact]
    public void FromStructArray_Typed_StampsTypeIdOnEachElement()
    {
        // Per-element TypeId layout: the typeId argument is now stamped into
        // each slot's reserved bytes, not on the array container. Read elements
        // back and verify each carries the TypeId.
        Arena arena = CreateArena();
        DataValue[] r0 = [DataValue.FromInt32(1)];
        DataValue[] r1 = [DataValue.FromInt32(2)];
        DataValue v = DataValue.FromStructArray([r0, r1], arena, typeId: 7);
        Assert.True(v.IsArray);
        Assert.Equal((ushort)0, v.TypeId);  // container is intentionally unused

        DataValue[] elements = v.AsStructArray(arena);
        Assert.Equal(2, elements.Length);
        Assert.Equal((ushort)7, elements[0].TypeId);
        Assert.Equal((ushort)7, elements[1].TypeId);
    }

    [Fact]
    public void FromUntypedStructArray_ElementsHaveZeroTypeId()
    {
        Arena arena = CreateArena();
        DataValue[] r0 = [DataValue.FromInt32(1)];
        DataValue[] r1 = [DataValue.FromInt32(2)];
        DataValue v = DataValue.FromUntypedStructArray([r0, r1], arena);
        Assert.Equal((ushort)0, v.TypeId);
        Assert.True(v.IsArray);

        DataValue[] elements = v.AsStructArray(arena);
        Assert.All(elements, e => Assert.Equal((ushort)0, e.TypeId));
    }

    [Fact]
    public void NullStruct_Typed_PropagatesTypeId()
    {
        DataValue v = DataValue.NullStruct(typeId: 11);
        Assert.True(v.IsNull);
        Assert.Equal((ushort)11, v.TypeId);
    }

    [Fact]
    public void NullUntypedStruct_HasZeroTypeId()
    {
        DataValue v = DataValue.NullUntypedStruct();
        Assert.True(v.IsNull);
        Assert.Equal((ushort)0, v.TypeId);
    }

    [Fact]
    public void FromUntypedStruct_RoundTripsFields()
    {
        // Behavioural sameness: the only difference between typed and untyped
        // factories is the TypeId stamp. Field payload round-trip is identical,
        // which is why FromUntypedStruct is a safe escape hatch for tests.
        Arena arena = CreateArena();
        DataValue[] fields = [DataValue.FromInt32(7), DataValue.FromBoolean(true)];
        DataValue v = DataValue.FromUntypedStruct(fields, arena);

        DataValue[] roundTripped = v.AsStruct(arena);
        Assert.Equal(2, roundTripped.Length);
        Assert.Equal(7, roundTripped[0].AsInt32());
        Assert.True(roundTripped[1].AsBoolean());
    }
}
