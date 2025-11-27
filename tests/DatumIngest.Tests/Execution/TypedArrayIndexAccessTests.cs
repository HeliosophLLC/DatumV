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

    private static DataValue Eval(IndexAccessExpression access, Row row, Arena arena)
    {
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault());
        EvaluationFrame frame = new(row, arena, arena);
        return evaluator.Evaluate(access, frame);
    }

    [Fact]
    public void IndexAccess_StringArray_ReturnsElementAtPosition()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            DataValue stringArray = DataValue.FromStringArray(
                ["alpha", "beta", "gamma"], arena);

            IndexAccessExpression access = new(
                new ColumnReference("arr"),
                new LiteralExpression(1));

            DataValue result = Eval(access, MakeRow(("arr", stringArray)), arena);

            Assert.Equal(DataKind.String, result.Kind);
            Assert.False(result.IsArray);
            Assert.Equal("beta", result.AsString(arena));
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public void IndexAccess_Float32Array_ReturnsElementAtPosition()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            DataValue floatArray = DataValue.FromArenaArray<float>(
                [1.5f, 2.5f, 3.5f, 4.5f], DataKind.Float32, arena);

            IndexAccessExpression access = new(
                new ColumnReference("arr"),
                new LiteralExpression(2));

            DataValue result = Eval(access, MakeRow(("arr", floatArray)), arena);

            Assert.Equal(DataKind.Float32, result.Kind);
            Assert.Equal(3.5f, result.AsFloat32());
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public void IndexAccess_Int32Array_OutOfRange_ReturnsTypedNull()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            DataValue intArray = DataValue.FromArenaArray<int>(
                [10, 20, 30], DataKind.Int32, arena);

            IndexAccessExpression access = new(
                new ColumnReference("arr"),
                new LiteralExpression(99));

            DataValue result = Eval(access, MakeRow(("arr", intArray)), arena);

            Assert.True(result.IsNull);
            Assert.Equal(DataKind.Int32, result.Kind);
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public void IndexAccess_StructArray_ReturnsStructElement()
    {
        // YOLO output shape: Array<Struct{label, score}>. Indexing returns a Struct.
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            DataValue[] s0 = [DataValue.FromString("cat", arena), DataValue.FromFloat32(0.9f)];
            DataValue[] s1 = [DataValue.FromString("dog", arena), DataValue.FromFloat32(0.7f)];
            DataValue structArray = DataValue.FromStructArray([s0, s1], arena);

            IndexAccessExpression access = new(
                new ColumnReference("detections"),
                new LiteralExpression(1));

            DataValue result = Eval(access, MakeRow(("detections", structArray)), arena);

            Assert.Equal(DataKind.Struct, result.Kind);
            Assert.False(result.IsArray);
            DataValue[] fields = result.AsStruct(arena);
            Assert.Equal("dog", fields[0].AsString(arena));
            Assert.Equal(0.7f, fields[1].AsFloat32());
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public void IndexAccess_StringArray_NamedFieldAccess_Throws()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            DataValue stringArray = DataValue.FromStringArray(["x", "y"], arena);

            IndexAccessExpression access = new(
                new ColumnReference("arr"),
                new LiteralExpression("foo"));

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => Eval(access, MakeRow(("arr", stringArray)), arena));
            Assert.Contains("Named field access", ex.Message);
            Assert.Contains("Array<String>", ex.Message);
        }
        finally { pool.Backing.TryReturn(arena); }
    }
}
