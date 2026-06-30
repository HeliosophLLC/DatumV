using System.Runtime.InteropServices;

using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Functions;

/// <summary>
/// Slice-A carrier tests for the body-local <see cref="DataKind.ListBuilder"/>
/// kind: the <see cref="ListBuilderValue"/> byte-buffer accumulator, the
/// <see cref="ValueRef"/> wrapper surface, freezing to a flat <c>Array&lt;T&gt;</c>,
/// and the persistence-refusal guard. Higher slices (parser, evaluator wiring)
/// exercise the SQL surface; this file pins the runtime carrier contract.
/// </summary>
public sealed class ListBuilderValueTests : ServiceTestBase
{
    private Arena RentArena() => GetService<Pool>().Backing.RentArena();
    private void ReturnArena(Arena arena) => GetService<Pool>().Backing.TryReturn(arena);

    private static ReadOnlySpan<byte> FloatBytes(params float[] values) =>
        MemoryMarshal.AsBytes<float>(values).ToArray();

    private static ReadOnlySpan<byte> Int32Bytes(params int[] values) =>
        MemoryMarshal.AsBytes<int>(values).ToArray();

    // ─── ListBuilderValue construction + element width ─────────────────────

    [Fact]
    public void Construct_PrimitiveKind_FixesStrideAndStartsEmpty()
    {
        ListBuilderValue list = new(DataKind.Float32);
        Assert.Equal(DataKind.Float32, list.ElementKind);
        Assert.Equal(4, list.Stride);
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void Construct_ReferenceKind_Throws()
    {
        // String has no fixed element width — ScalarByteSize rejects it, which is
        // exactly the "primitive element kinds only" guard for v0.
        Assert.Throws<InvalidOperationException>(() => new ListBuilderValue(DataKind.String));
    }

    // ─── APPEND semantics (scalar + array concat) ──────────────────────────

    [Fact]
    public void AppendScalars_AccumulatesInOrder()
    {
        ListBuilderValue list = new(DataKind.Float32);
        list.AppendBytes(FloatBytes(1.5f));
        list.AppendBytes(FloatBytes(2.5f));
        list.AppendBytes(FloatBytes(3.5f));

        Assert.Equal(3, list.Count);
        Assert.True(MemoryMarshal.Cast<byte, float>(list.Bytes).SequenceEqual([1.5f, 2.5f, 3.5f]));
    }

    [Fact]
    public void AppendArray_ConcatenatesAllElements()
    {
        // A single multi-element append mirrors `array_concat(acc, peer_array)`.
        ListBuilderValue list = new(DataKind.Float32);
        list.AppendBytes(FloatBytes(1f, 2f));
        list.AppendBytes(FloatBytes(3f, 4f, 5f));

        Assert.Equal(5, list.Count);
        Assert.True(MemoryMarshal.Cast<byte, float>(list.Bytes).SequenceEqual([1f, 2f, 3f, 4f, 5f]));
    }

    [Fact]
    public void AppendBytes_NonStrideMultiple_Throws()
    {
        ListBuilderValue list = new(DataKind.Float32);
        Assert.Throws<ArgumentException>(() => list.AppendBytes(new byte[3]));
    }

    [Fact]
    public void GrowthAcrossManyAppends_PreservesAllElements()
    {
        // Drive the doubling-growth path well past the initial floor.
        ListBuilderValue list = new(DataKind.Int32);
        for (int i = 0; i < 1000; i++)
        {
            list.AppendBytes(Int32Bytes(i));
        }

        Assert.Equal(1000, list.Count);
        ReadOnlySpan<int> elements = MemoryMarshal.Cast<byte, int>(list.Bytes);
        Assert.Equal(0, elements[0]);
        Assert.Equal(500, elements[500]);
        Assert.Equal(999, elements[999]);
    }

    [Fact]
    public void Reserve_DoesNotChangeCount_AndAppendStillWorks()
    {
        ListBuilderValue list = new(DataKind.Float32, reserveElements: 16384);
        Assert.Equal(0, list.Count);

        list.AppendBytes(FloatBytes(42f));
        Assert.Equal(1, list.Count);
        Assert.Equal(42f, MemoryMarshal.Cast<byte, float>(list.Bytes)[0]);
    }

    // ─── ValueRef wrapper surface ──────────────────────────────────────────

    [Fact]
    public void FromListBuilder_ExposesKindAndElementKind()
    {
        ValueRef listRef = ValueRef.FromListBuilder(new ListBuilderValue(DataKind.Float32));

        Assert.Equal(DataKind.ListBuilder, listRef.Kind);
        Assert.True(listRef.IsListBuilder);
        Assert.False(listRef.IsNull);
        Assert.Equal(DataKind.Float32, listRef.ListElementKind);
    }

    [Fact]
    public void FromListBuilder_NullPayload_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ValueRef.FromListBuilder(null!));
    }

    [Fact]
    public void IsListBuilder_FalseForOrdinaryArray()
    {
        ValueRef arrayRef = ValueRef.FromPrimitiveArray<float>([1f, 2f, 3f], DataKind.Float32);
        Assert.False(arrayRef.IsListBuilder);
    }

    // ─── Freeze to Array<T> ────────────────────────────────────────────────

    [Fact]
    public void FreezeToArray_Float32_ProducesMatchingFlatArray()
    {
        ListBuilderValue list = new(DataKind.Float32);
        list.AppendBytes(FloatBytes(10f, 20f, 30f));

        ValueRef frozen = ValueRef.FromListBuilder(list).FreezeToArray();

        Assert.True(frozen.IsArray);
        Assert.False(frozen.IsListBuilder);
        Assert.Equal(DataKind.Float32, frozen.Kind);
        // The frozen payload is the semantic typed array downstream readers expect.
        Assert.Equal([10f, 20f, 30f], Assert.IsType<float[]>(frozen.Materialized));

        // And it materialises into an arena like any ordinary array.
        Arena arena = RentArena();
        try
        {
            DataValue dv = frozen.ToDataValue(arena);
            Assert.Equal(DataKind.Float32, dv.Kind);
            Assert.True(dv.IsArray);
            Assert.True(dv.AsArraySpan<float>(arena).SequenceEqual([10f, 20f, 30f]));
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FreezeToArray_Int32_IsKindAgnostic()
    {
        ListBuilderValue list = new(DataKind.Int32);
        list.AppendBytes(Int32Bytes(7, 8, 9));

        ValueRef frozen = ValueRef.FromListBuilder(list).FreezeToArray();

        Assert.Equal(DataKind.Int32, frozen.Kind);
        Assert.Equal([7, 8, 9], Assert.IsType<int[]>(frozen.Materialized));
    }

    [Fact]
    public void FreezeToArray_Empty_ProducesEmptyArray()
    {
        ValueRef frozen = ValueRef.FromListBuilder(new ListBuilderValue(DataKind.Float32)).FreezeToArray();
        Assert.Equal(DataKind.Float32, frozen.Kind);
        Assert.Empty(Assert.IsType<float[]>(frozen.Materialized));
    }

    // ─── Auto-freeze at the materialisation boundary ───────────────────────

    [Fact]
    public void ToDataValue_OnListBuilder_AutoFreezesToArray()
    {
        // A list reaching a DataValue boundary auto-freezes to its array (the
        // SQL-natural promotion) rather than throwing — the array is the list's
        // correct storable form.
        ListBuilderValue list = new(DataKind.Float32);
        list.AppendBytes(FloatBytes(1.5f, 2.5f));
        ValueRef listRef = ValueRef.FromListBuilder(list);

        Arena arena = RentArena();
        try
        {
            DataValue dv = listRef.ToDataValue(arena);
            Assert.Equal(DataKind.Float32, dv.Kind);
            Assert.True(dv.IsArray);
            Assert.True(dv.AsArraySpan<float>(arena).SequenceEqual([1.5f, 2.5f]));
        }
        finally { ReturnArena(arena); }
    }
}
