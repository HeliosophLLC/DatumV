using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests that <see cref="ExpressionEvaluator"/>'s index-access path can read
/// elements out of typed arrays (<c>Kind=elementKind + IsArray</c>) — the
/// shape <c>ARRAY_AGG</c>, <c>ValueRef.FromArray</c>, and the YOLO output
/// path produce.
/// </summary>
public class TypedArrayIndexAccessTests : ServiceTestBase
{
    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(new ColumnLookup(names), values);
    }

    private static async Task<DataValue> EvalAsync(IndexAccessExpression access, Row row, Arena arena)
    {
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault());
        EvaluationFrame frame = new(row, arena, arena, evaluator.Accountant);
        return await evaluator.EvaluateAsync(access, frame);
    }

    private static async Task<DataValue> EvalWithRegistryAsync(
        IndexAccessExpression access, Row row, Arena arena, TypeRegistry registry)
    {
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault(), typeRegistry: registry);
        EvaluationFrame frame = new(row, arena, arena, evaluator.Accountant);
        return await evaluator.EvaluateAsync(access, frame);
    }

    [Fact]
    public async Task IndexAccess_ArrayOfStruct_StampsElementTypeIdOnResult()
    {
        // After the per-element TypeId layout, FromStructArray writes the
        // element struct's TypeId directly into each slot's reserved bytes.
        // Index access just returns the per-element DataValue, which already
        // carries its TypeId — no array-container hop, works for N=1 too.
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            TypeRegistry registry = new();
            int structTypeId = registry.InternStructType(
            [
                new StructFieldDescriptor("label", registry.InternScalarType(DataKind.String)),
                new StructFieldDescriptor("score", registry.InternScalarType(DataKind.Float32)),
            ]);

            DataValue[] s0 = [DataValue.FromString("cat", arena), DataValue.FromFloat32(0.9f)];
            DataValue[] s1 = [DataValue.FromString("dog", arena), DataValue.FromFloat32(0.7f)];
            DataValue structArray = DataValue.FromStructArray(
                [s0, s1], arena, (ushort)structTypeId);

            IndexAccessExpression access = new(
                new ColumnReference("detections"),
                [new LiteralExpression(1)]);

            DataValue result = await EvalWithRegistryAsync(
                access, MakeRow(("detections", structArray)), arena, registry);

            Assert.Equal(DataKind.Struct, result.Kind);
            Assert.False(result.IsArray);
            Assert.Equal((ushort)structTypeId, result.TypeId);
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public async Task IndexAccess_ArrayOfStruct_N1Inline_StampsElementTypeId()
    {
        // The case that motivated the layout change — single-element struct
        // arrays previously stripped TypeId because the inline layout used
        // _charCount as the element count. With per-element TypeId in the
        // slot's reserved bytes, N=1 round-trips just like N≥2.
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            TypeRegistry registry = new();
            int structTypeId = registry.InternStructType(
            [
                new StructFieldDescriptor("score", registry.InternScalarType(DataKind.Float32)),
            ]);

            DataValue[] s0 = [DataValue.FromFloat32(0.9f)];
            DataValue structArray = DataValue.FromStructArray(
                [s0], arena, (ushort)structTypeId);

            IndexAccessExpression access = new(
                new ColumnReference("detections"),
                [new LiteralExpression(1)]);

            DataValue result = await EvalWithRegistryAsync(
                access, MakeRow(("detections", structArray)), arena, registry);

            Assert.Equal(DataKind.Struct, result.Kind);
            Assert.False(result.IsArray);
            Assert.Equal((ushort)structTypeId, result.TypeId);
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public async Task IndexAccess_ArrayOfStruct_OutOfRange_NullStructStillCarriesElementTypeId()
    {
        // Out-of-range index returns NullStruct that borrows the TypeId from
        // any existing element so the null still names the shape that *would*
        // have been there. Empty arrays naturally fall back to TypeId=0.
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            TypeRegistry registry = new();
            int structTypeId = registry.InternStructType(
            [
                new StructFieldDescriptor("x", registry.InternScalarType(DataKind.Int32)),
            ]);

            DataValue[] s0 = [DataValue.FromInt32(1)];
            DataValue[] s1 = [DataValue.FromInt32(2)];
            DataValue structArray = DataValue.FromStructArray(
                [s0, s1], arena, (ushort)structTypeId);

            IndexAccessExpression access = new(
                new ColumnReference("arr"),
                [new LiteralExpression(99)]);

            DataValue result = await EvalWithRegistryAsync(
                access, MakeRow(("arr", structArray)), arena, registry);

            Assert.True(result.IsNull);
            Assert.Equal(DataKind.Struct, result.Kind);
            Assert.Equal((ushort)structTypeId, result.TypeId);
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public async Task IndexAccess_ArrayOfStruct_NoRegistry_LegacyZeroTypeIdBehaviour()
    {
        // Without a registry, the evaluator can't resolve ElementTypeId and
        // the result has TypeId=0. Verifies the fix is non-breaking for tests
        // that don't construct a registry.
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            DataValue[] s0 = [DataValue.FromInt32(1)];
            DataValue structArray = DataValue.FromUntypedStructArray([s0], arena);

            IndexAccessExpression access = new(
                new ColumnReference("arr"),
                [new LiteralExpression(1)]);

            DataValue result = await EvalAsync(access, MakeRow(("arr", structArray)), arena);

            Assert.Equal(DataKind.Struct, result.Kind);
            Assert.Equal((ushort)0, result.TypeId);
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public async Task IndexAccess_StringArray_ReturnsElementAtPosition()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            DataValue stringArray = DataValue.FromStringArray(
                ["alpha", "beta", "gamma"], arena);

            IndexAccessExpression access = new(
                new ColumnReference("arr"),
                [new LiteralExpression(2)]);

            DataValue result = await EvalAsync(access, MakeRow(("arr", stringArray)), arena);

            Assert.Equal(DataKind.String, result.Kind);
            Assert.False(result.IsArray);
            Assert.Equal("beta", result.AsString(arena));
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public async Task IndexAccess_Float32Array_ReturnsElementAtPosition()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            DataValue floatArray = DataValue.FromArenaArray<float>(
                [1.5f, 2.5f, 3.5f, 4.5f], DataKind.Float32, arena);

            IndexAccessExpression access = new(
                new ColumnReference("arr"),
                [new LiteralExpression(3)]);

            DataValue result = await EvalAsync(access, MakeRow(("arr", floatArray)), arena);

            Assert.Equal(DataKind.Float32, result.Kind);
            Assert.Equal(3.5f, result.AsFloat32());
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public async Task IndexAccess_Int32Array_OutOfRange_ReturnsTypedNull()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            DataValue intArray = DataValue.FromArenaArray<int>(
                [10, 20, 30], DataKind.Int32, arena);

            IndexAccessExpression access = new(
                new ColumnReference("arr"),
                [new LiteralExpression(99)]);

            DataValue result = await EvalAsync(access, MakeRow(("arr", intArray)), arena);

            Assert.True(result.IsNull);
            Assert.Equal(DataKind.Int32, result.Kind);
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public async Task IndexAccess_StructArray_ReturnsStructElement()
    {
        // YOLO output shape: Array<Struct{label, score}>. Indexing returns a Struct.
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            DataValue[] s0 = [DataValue.FromString("cat", arena), DataValue.FromFloat32(0.9f)];
            DataValue[] s1 = [DataValue.FromString("dog", arena), DataValue.FromFloat32(0.7f)];
            DataValue structArray = DataValue.FromUntypedStructArray([s0, s1], arena);

            IndexAccessExpression access = new(
                new ColumnReference("detections"),
                [new LiteralExpression(2)]);

            DataValue result = await EvalAsync(access, MakeRow(("detections", structArray)), arena);

            Assert.Equal(DataKind.Struct, result.Kind);
            Assert.False(result.IsArray);
            DataValue[] fields = result.AsStruct(arena);
            Assert.Equal("dog", fields[0].AsString(arena));
            Assert.Equal(0.7f, fields[1].AsFloat32());
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public async Task IndexAccess_StringArray_NamedFieldAccess_Throws()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            DataValue stringArray = DataValue.FromStringArray(["x", "y"], arena);

            IndexAccessExpression access = new(
                new ColumnReference("arr"),
                [new LiteralExpression("foo")]);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await EvalAsync(access, MakeRow(("arr", stringArray)), arena));
            Assert.Contains("Named field access", ex.Message);
            Assert.Contains("Array<String>", ex.Message);
        }
        finally { pool.Backing.TryReturn(arena); }
    }
}
