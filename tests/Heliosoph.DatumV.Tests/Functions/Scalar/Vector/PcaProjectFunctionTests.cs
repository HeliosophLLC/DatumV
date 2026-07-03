using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Vector;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Vector;

/// <summary>
/// Tests for <see cref="PcaProjectFunction"/> — <c>pca_project(model, vec)</c>.
/// Verifies centered projection against hand-computed values, field-name (not
/// positional) model resolution, null propagation, and the dimension /
/// missing-field error paths.
/// </summary>
public sealed class PcaProjectFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_Exposes()
    {
        Assert.Equal("pca_project", PcaProjectFunction.Name);
        Assert.Equal(FunctionCategory.Vector, PcaProjectFunction.Category);
    }

    /// <summary>
    /// Builds a PCA model struct ValueRef with fields interned in
    /// <paramref name="context"/>'s registry, in the given field order —
    /// letting tests verify name-based (not positional) resolution.
    /// </summary>
    private static ValueRef BuildModel(
        Heliosoph.DatumV.Execution.ExecutionContext context,
        float[] mean,
        float[] componentsFlat,
        int k,
        bool reversedFieldOrder = false)
    {
        int d = mean.Length;
        float[] ratio = new float[k];

        int f32Arr = context.Types.InternArrayType(DataKind.Float32);
        ValueRef meanRef = ValueRef.FromPrimitiveArray(mean, DataKind.Float32);
        ValueRef compRef = ValueRef.FromPrimitiveMultiDimArray(componentsFlat, [k, d], DataKind.Float32);
        ValueRef ratioRef = ValueRef.FromPrimitiveArray(ratio, DataKind.Float32);

        StructFieldDescriptor[] descriptors;
        ValueRef[] fields;
        if (reversedFieldOrder)
        {
            descriptors = [new("variance_ratio", f32Arr), new("components", f32Arr), new("mean", f32Arr)];
            fields = [ratioRef, compRef, meanRef];
        }
        else
        {
            descriptors = [new("mean", f32Arr), new("components", f32Arr), new("variance_ratio", f32Arr)];
            fields = [meanRef, compRef, ratioRef];
        }

        ushort typeId = (ushort)context.Types.InternStructType(descriptors);
        return ValueRef.FromStruct(fields, typeId);
    }

    private async Task<ValueRef> Project(
        Heliosoph.DatumV.Execution.ExecutionContext context, ValueRef model, ValueRef vec)
    {
        PcaProjectFunction fn = new();
        return await fn.ExecuteAsync(
            new ValueRef[] { model, vec },
            CreateEvaluationFrame(context),
            default);
    }

    private static float[] ReadFloats(ValueRef result, EvaluationFrame frame)
    {
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.True(result.IsArray);
        return result.ToDataValue(frame.Source).AsArraySpan<float>(frame.Source, frame.SidecarRegistry).ToArray();
    }

    [Fact]
    public async Task IdentityBasis_SubtractsMean()
    {
        var context = CreateExecutionContext();
        ValueRef model = BuildModel(context, mean: [1f, 2f], componentsFlat: [1f, 0f, 0f, 1f], k: 2);

        ValueRef result = await Project(context, model,
            ValueRef.FromPrimitiveArray(new float[] { 3f, 5f }, DataKind.Float32));

        float[] projected = ReadFloats(result, CreateEvaluationFrame(context));
        Assert.Equal(2f, projected[0], 4);
        Assert.Equal(3f, projected[1], 4);
    }

    [Fact]
    public async Task RotatedBasis_ProjectsOntoComponents()
    {
        var context = CreateExecutionContext();
        float invSqrt2 = 1f / MathF.Sqrt(2f);
        // PC1 along (1,1)/√2, PC2 along (-1,1)/√2, zero mean.
        ValueRef model = BuildModel(context,
            mean: [0f, 0f],
            componentsFlat: [invSqrt2, invSqrt2, -invSqrt2, invSqrt2],
            k: 2);

        ValueRef result = await Project(context, model,
            ValueRef.FromPrimitiveArray(new float[] { 2f, 2f }, DataKind.Float32));

        float[] projected = ReadFloats(result, CreateEvaluationFrame(context));
        Assert.Equal(2f * MathF.Sqrt(2f), projected[0], 3);
        Assert.Equal(0f, projected[1], 3);
    }

    [Fact]
    public async Task KLessThanD_ReturnsKCoordinates()
    {
        var context = CreateExecutionContext();
        ValueRef model = BuildModel(context, mean: [0f, 0f, 0f], componentsFlat: [0f, 1f, 0f], k: 1);

        ValueRef result = await Project(context, model,
            ValueRef.FromPrimitiveArray(new float[] { 7f, 4f, 9f }, DataKind.Float32));

        float[] projected = ReadFloats(result, CreateEvaluationFrame(context));
        Assert.Single(projected);
        Assert.Equal(4f, projected[0], 4);
    }

    [Fact]
    public async Task FieldOrder_ResolvedByName_NotPosition()
    {
        var context = CreateExecutionContext();
        ValueRef model = BuildModel(context, mean: [1f, 2f], componentsFlat: [1f, 0f, 0f, 1f], k: 2,
            reversedFieldOrder: true);

        ValueRef result = await Project(context, model,
            ValueRef.FromPrimitiveArray(new float[] { 3f, 5f }, DataKind.Float32));

        float[] projected = ReadFloats(result, CreateEvaluationFrame(context));
        Assert.Equal(2f, projected[0], 4);
        Assert.Equal(3f, projected[1], 4);
    }

    [Fact]
    public async Task NullModel_ReturnsNullArray()
    {
        var context = CreateExecutionContext();
        ValueRef result = await Project(context, ValueRef.NullStruct(0),
            ValueRef.FromPrimitiveArray(new float[] { 1f, 2f }, DataKind.Float32));

        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public async Task NullVector_ReturnsNullArray()
    {
        var context = CreateExecutionContext();
        ValueRef model = BuildModel(context, mean: [0f, 0f], componentsFlat: [1f, 0f, 0f, 1f], k: 2);

        ValueRef result = await Project(context, model, ValueRef.NullArray(DataKind.Float32));

        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public async Task DimensionMismatch_Throws()
    {
        var context = CreateExecutionContext();
        ValueRef model = BuildModel(context, mean: [0f, 0f], componentsFlat: [1f, 0f, 0f, 1f], k: 2);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await Project(context, model,
                ValueRef.FromPrimitiveArray(new float[] { 1f, 2f, 3f }, DataKind.Float32)));
        Assert.Contains("2", ex.Message);
        Assert.Contains("3", ex.Message);
    }

    [Fact]
    public async Task MissingField_Throws()
    {
        var context = CreateExecutionContext();
        int f32Arr = context.Types.InternArrayType(DataKind.Float32);
        ushort typeId = (ushort)context.Types.InternStructType(
            [new StructFieldDescriptor("centroids", f32Arr)]);
        ValueRef notAModel = ValueRef.FromStruct(
            [ValueRef.FromPrimitiveArray(new float[] { 1f, 2f }, DataKind.Float32)], typeId);

        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await Project(context, notAModel,
                ValueRef.FromPrimitiveArray(new float[] { 1f, 2f }, DataKind.Float32)));
    }

    [Fact]
    public async Task RoundTrip_FitThenProject_RecoversAxisCoordinates()
    {
        var context = CreateExecutionContext();
        // Model equivalent to fitting AxisAlignedPoints: identity basis, zero mean.
        ValueRef model = BuildModel(context, mean: [0f, 0f], componentsFlat: [1f, 0f, 0f, 1f], k: 2);

        float[][] points = [[3f, 0f], [-3f, 0f], [0f, 1f], [0f, -1f]];
        foreach (float[] p in points)
        {
            ValueRef result = await Project(context, model,
                ValueRef.FromPrimitiveArray(p, DataKind.Float32));
            float[] projected = ReadFloats(result, CreateEvaluationFrame(context));
            Assert.Equal(p[0], projected[0], 4);
            Assert.Equal(p[1], projected[1], 4);
        }
    }
}
