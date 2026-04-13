using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Round-trip tests for <see cref="ExpressionEvaluator.EvaluateAsValueRefAsync"/>
/// lifting non-inline Struct and <c>Array&lt;Struct&gt;</c> column values into
/// <see cref="ValueRef"/>s. Before this lift landed, a column carrying a model's
/// <c>Array&lt;Struct&gt;</c> output threw "Cannot convert non-inline Array&lt;Struct&gt;
/// into a ValueRef" the moment any function tried to consume it. Pinning the round
/// trip here so future struct-consuming functions (e.g. drawing detections, cropping
/// regions) get a safety net.
/// </summary>
public class StructValueRefLiftTests : ServiceTestBase
{
    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(new ColumnLookup(names), values);
    }

    [Fact]
    public async Task Lift_StructArrayColumn_ProducesArrayOfStructValueRefs()
    {
        // YOLO-shaped output: Array<Struct{label, score, x, y, w, h}>. The lift
        // must walk each element, read its fields out of the arena, and return
        // a ValueRef whose IsArray is true and whose elements are themselves
        // struct-shaped ValueRefs with their fields lifted.
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
            DataValue array = DataValue.FromStructArray([s0, s1], arena, (ushort)structTypeId);

            using DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(store: arena, typeRegistry: registry);
            ExpressionEvaluator evaluator = context.CreateEvaluator();
            EvaluationFrame frame = evaluator.CreateFrame(MakeRow(("detections", array)), arena);

            ValueRef lifted = await evaluator.EvaluateAsValueRefAsync(
                new ColumnReference("detections"), frame);

            Assert.True(lifted.IsArray);
            Assert.Equal(DataKind.Struct, lifted.Kind);

            ReadOnlySpan<ValueRef> elements = lifted.GetArrayElements();
            Assert.Equal(2, elements.Length);

            // Element 0: per-element TypeId is preserved through the lift.
            Assert.Equal((ushort)structTypeId, elements[0].TypeId);
            Assert.Equal(DataKind.Struct, elements[0].Kind);
            ReadOnlySpan<ValueRef> e0Fields = elements[0].GetStructFields();
            Assert.Equal(2, e0Fields.Length);
            Assert.Equal("cat", e0Fields[0].AsString());
            Assert.Equal(0.9f, e0Fields[1].AsFloat32());

            // Element 1: same shape, different data.
            Assert.Equal((ushort)structTypeId, elements[1].TypeId);
            ReadOnlySpan<ValueRef> e1Fields = elements[1].GetStructFields();
            Assert.Equal("dog", e1Fields[0].AsString());
            Assert.Equal(0.7f, e1Fields[1].AsFloat32());
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public async Task Lift_StructArrayColumn_NestedArrayInsideStruct_RecursesThroughBothPaths()
    {
        // SCRFD-shaped output: each element has a `landmarks` field that is itself
        // Array<Struct{x, y}>. The lift must recurse — the outer Array<Struct> arm
        // walks elements, the per-element struct lift walks fields, and a field
        // that is itself Array<Struct> re-enters ArrayDataValueToValueRef.
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            TypeRegistry registry = new();
            int landmarkTypeId = registry.InternStructType(
            [
                new StructFieldDescriptor("x", registry.InternScalarType(DataKind.Float32)),
                new StructFieldDescriptor("y", registry.InternScalarType(DataKind.Float32)),
            ]);
            int detectionTypeId = registry.InternStructType(
            [
                new StructFieldDescriptor("score",
                    registry.InternScalarType(DataKind.Float32)),
                new StructFieldDescriptor("landmarks",
                    registry.InternArrayType(DataKind.Struct, elementTypeId: landmarkTypeId)),
            ]);

            DataValue[] kp0 = [DataValue.FromFloat32(10f), DataValue.FromFloat32(20f)];
            DataValue[] kp1 = [DataValue.FromFloat32(30f), DataValue.FromFloat32(40f)];
            DataValue landmarks = DataValue.FromStructArray([kp0, kp1], arena, (ushort)landmarkTypeId);

            DataValue[] det = [DataValue.FromFloat32(0.95f), landmarks];
            DataValue array = DataValue.FromStructArray([det], arena, (ushort)detectionTypeId);

            using DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(store: arena, typeRegistry: registry);
            ExpressionEvaluator evaluator = context.CreateEvaluator();
            EvaluationFrame frame = evaluator.CreateFrame(MakeRow(("faces", array)), arena);

            ValueRef lifted = await evaluator.EvaluateAsValueRefAsync(
                new ColumnReference("faces"), frame);

            Assert.True(lifted.IsArray);
            ReadOnlySpan<ValueRef> elements = lifted.GetArrayElements();
            Assert.Equal(1, elements.Length);

            ReadOnlySpan<ValueRef> detFields = elements[0].GetStructFields();
            Assert.Equal(2, detFields.Length);
            Assert.Equal(0.95f, detFields[0].AsFloat32());

            // Nested array field — the recursion entered ArrayDataValueToValueRef
            // a second time with Struct as the element kind.
            ValueRef landmarkArr = detFields[1];
            Assert.True(landmarkArr.IsArray);
            ReadOnlySpan<ValueRef> kps = landmarkArr.GetArrayElements();
            Assert.Equal(2, kps.Length);

            ReadOnlySpan<ValueRef> kp0Fields = kps[0].GetStructFields();
            Assert.Equal(10f, kp0Fields[0].AsFloat32());
            Assert.Equal(20f, kp0Fields[1].AsFloat32());

            Assert.Equal((ushort)landmarkTypeId, kps[1].TypeId);
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public async Task Lift_NullStructArrayColumn_ProducesNullArrayValueRef()
    {
        // Null arrays must lift to ValueRef.NullArray so downstream IsArray
        // checks see the right shape. Verifies the null branch in ToValueRef
        // beats the Array<Struct> arm to the punch.
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            DataValue nullArray = DataValue.NullArrayOf(DataKind.Struct);
            using DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(store: arena);
            ExpressionEvaluator evaluator = context.CreateEvaluator();
            EvaluationFrame frame = evaluator.CreateFrame(MakeRow(("detections", nullArray)), arena);

            ValueRef lifted = await evaluator.EvaluateAsValueRefAsync(
                new ColumnReference("detections"), frame);

            Assert.True(lifted.IsNull);
            Assert.True(lifted.IsArray);
            Assert.Equal(DataKind.Struct, lifted.Kind);
        }
        finally { pool.Backing.TryReturn(arena); }
    }

    [Fact]
    public async Task Lift_SingleStructColumn_PreservesTypeIdAndFields()
    {
        // The single-struct (non-array) case has the same plumbing gap. Lifting
        // a struct column value reads its fields out of the arena and wraps
        // with FromStruct(fields, typeId).
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            TypeRegistry registry = new();
            int typeId = registry.InternStructType(
            [
                new StructFieldDescriptor("name", registry.InternScalarType(DataKind.String)),
                new StructFieldDescriptor("count", registry.InternScalarType(DataKind.Int32)),
            ]);

            DataValue[] fields = [DataValue.FromString("widget", arena), DataValue.FromInt32(42)];
            DataValue s = DataValue.FromStruct(fields, arena, (ushort)typeId);

            using DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(store: arena, typeRegistry: registry);
            ExpressionEvaluator evaluator = context.CreateEvaluator();
            EvaluationFrame frame = evaluator.CreateFrame(MakeRow(("s", s)), arena);

            ValueRef lifted = await evaluator.EvaluateAsValueRefAsync(
                new ColumnReference("s"), frame);

            Assert.False(lifted.IsArray);
            Assert.Equal(DataKind.Struct, lifted.Kind);
            Assert.Equal((ushort)typeId, lifted.TypeId);

            ReadOnlySpan<ValueRef> liftedFields = lifted.GetStructFields();
            Assert.Equal("widget", liftedFields[0].AsString());
            Assert.Equal(42, liftedFields[1].AsInt32());
        }
        finally { pool.Backing.TryReturn(arena); }
    }
}
