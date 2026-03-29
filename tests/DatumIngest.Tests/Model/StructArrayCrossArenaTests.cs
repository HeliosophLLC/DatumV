using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Cross-arena copy of <c>Array&lt;Struct&gt;</c> at the DataValue level —
/// driven by a per-field recursive rebind helper that walks each struct
/// element's fields and re-emits any arena-backed reference fields into
/// the target arena. Without the rebind, the existing
/// <c>AsStructArray</c>+<c>FromStructArray</c> round-trip preserves
/// reference-field offsets that point into the source arena, leaving
/// dangling references in the target value.
/// <para>
/// SQL-level struct DDL with inline field declarations doesn't parse, so
/// these tests exercise the path directly through DataValue factories +
/// the cross-arena rebind helper. The InsertExecutor dispatch arm for
/// <c>DataKind.Struct</c> routes through the same helper when the
/// programmatic path drives an <c>Array&lt;Struct&gt;</c> through the
/// executor.
/// </para>
/// </summary>
public sealed class StructArrayCrossArenaTests : ServiceTestBase
{
    [Fact]
    public void StructArray_InlineScalarFieldsOnly_CrossArenaRoundTrips()
    {
        // Baseline: struct elements with all-inline fields (no String, no
        // byte arrays) round-trip via plain AsStructArray + FromStructArray.
        // This works even without per-field rebind because no arena references
        // exist in the fields. Locks the baseline so the harder tests below
        // can isolate the rebind-specific failures.
        Arena source = new();
        DataValue[] row0 = [DataValue.FromInt32(1), DataValue.FromFloat32(1.5f)];
        DataValue[] row1 = [DataValue.FromInt32(2), DataValue.FromFloat32(2.5f)];
        DataValue arr = DataValue.FromStructArray(new[] { row0, row1 }, source, typeId: 0);

        DataValue[] readBack = arr.AsStructArray(source);

        Arena target = new();
        DataValue[][] perRowFields = new DataValue[readBack.Length][];
        for (int i = 0; i < readBack.Length; i++)
        {
            perRowFields[i] = readBack[i].AsStruct(source);
        }
        DataValue arrInTarget = DataValue.FromStructArray(perRowFields, target, typeId: 0);

        DataValue[] elements = arrInTarget.AsStructArray(target);
        Assert.Equal(2, elements.Length);

        DataValue[] elem0Fields = elements[0].AsStruct(target);
        Assert.Equal(1, elem0Fields[0].AsInt32());
        Assert.Equal(1.5f, elem0Fields[1].AsFloat32());

        DataValue[] elem1Fields = elements[1].AsStruct(target);
        Assert.Equal(2, elem1Fields[0].AsInt32());
        Assert.Equal(2.5f, elem1Fields[1].AsFloat32());
    }

    [Fact]
    public void StructArray_WithStringField_CrossArenaRoundTrips()
    {
        // Harder case: each struct element has a String field whose bytes
        // live in the SOURCE arena. The naive AsStructArray + FromStructArray
        // round-trip preserves the source-arena offsets in the field bytes,
        // dangling into the source arena. The CopyStructArrayToTargetArena
        // helper must recursively rebind the String field to the target arena
        // before writing the slot block.
        Arena source = new();
        Arena target = new();

        DataValue[] row0 = [
            DataValue.FromString("alice", source),  // arena-backed in source
            DataValue.FromInt32(30),
        ];
        DataValue[] row1 = [
            DataValue.FromString("bob", source),
            DataValue.FromInt32(25),
        ];
        DataValue arr = DataValue.FromStructArray(new[] { row0, row1 }, source, typeId: 0);

        DataValue arrInTarget = CopyStructArrayAcrossArenas(arr, source, target);

        // The strings must be readable through the TARGET arena — proof that
        // their bytes were re-emitted in target and the slot offsets were
        // rewritten to point at target memory.
        DataValue[] elements = arrInTarget.AsStructArray(target);
        Assert.Equal(2, elements.Length);

        DataValue[] elem0Fields = elements[0].AsStruct(target);
        Assert.Equal("alice", elem0Fields[0].AsString(target));
        Assert.Equal(30, elem0Fields[1].AsInt32());

        DataValue[] elem1Fields = elements[1].AsStruct(target);
        Assert.Equal("bob", elem1Fields[0].AsString(target));
        Assert.Equal(25, elem1Fields[1].AsInt32());
    }

    [Fact]
    public void StructArray_WithNestedStructField_CrossArenaRoundTrips()
    {
        // Nested struct field: each top-level struct contains a struct field
        // which itself contains a String field. Exercises the
        // RebindStructToTargetArena recursive descent — without it, the
        // nested struct's String field would still reference the source
        // arena after the outer rebind.
        Arena source = new();
        Arena target = new();

        // Inner struct: { city: String }
        DataValue[] inner0 = [DataValue.FromString("Boston", source)];
        DataValue[] inner1 = [DataValue.FromString("Seattle", source)];
        DataValue innerStruct0 = DataValue.FromStruct(inner0, source, typeId: 0);
        DataValue innerStruct1 = DataValue.FromStruct(inner1, source, typeId: 0);

        // Outer struct: { name: String, address: Struct{city: String} }
        DataValue[] outer0 = [DataValue.FromString("alice", source), innerStruct0];
        DataValue[] outer1 = [DataValue.FromString("bob", source), innerStruct1];
        DataValue arr = DataValue.FromStructArray(
            new[] { outer0, outer1 }, source, typeId: 0);

        DataValue arrInTarget = CopyStructArrayAcrossArenas(arr, source, target);

        DataValue[] elements = arrInTarget.AsStructArray(target);
        Assert.Equal(2, elements.Length);

        // Verify outer + nested fields all read correctly through target.
        DataValue[] elem0Fields = elements[0].AsStruct(target);
        Assert.Equal("alice", elem0Fields[0].AsString(target));
        DataValue[] elem0InnerFields = elem0Fields[1].AsStruct(target);
        Assert.Equal("Boston", elem0InnerFields[0].AsString(target));

        DataValue[] elem1Fields = elements[1].AsStruct(target);
        Assert.Equal("bob", elem1Fields[0].AsString(target));
        DataValue[] elem1InnerFields = elem1Fields[1].AsStruct(target);
        Assert.Equal("Seattle", elem1InnerFields[0].AsString(target));
    }

    /// <summary>
    /// Test-only wrapper that invokes the cross-arena rebind path on a
    /// non-internal helper. The actual implementation lives in
    /// <c>InsertExecutor.CopyStructArrayToTargetArena</c>; this wrapper
    /// exists so the unit test can exercise it without an InsertExecutor
    /// pipeline.
    /// </summary>
    private static DataValue CopyStructArrayAcrossArenas(
        DataValue source, Arena sourceArena, Arena targetArena)
    {
        return DatumIngest.Catalog.Executors.InsertExecutor
            .CopyStructArrayToTargetArenaForTests(source, sourceArena, registry: null, targetArena);
    }
}
