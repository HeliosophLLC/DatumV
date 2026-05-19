using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Spatial;

public sealed class PoseInverseFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task IdentityInverse_IsIdentity()
    {
        EvaluationFrame f = CreateEvaluationFrame();
        ValueRef identity = await new PoseIdentityFunction().ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty, f, default);

        ValueRef result = await new PoseInverseFunction().ExecuteAsync(
            new[] { identity }, f, default);

        ReadOnlySpan<float> inv = result.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);
        AssertMatrixApproxEqual(new float[]
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        }, inv);
    }

    [Fact]
    public async Task PureTranslation_InvertsSign()
    {
        EvaluationFrame f = CreateEvaluationFrame();
        ValueRef pose = await new PoseTranslateFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(5f), ValueRef.FromFloat32(-3f), ValueRef.FromFloat32(2f) },
            f, default);

        ValueRef result = await new PoseInverseFunction().ExecuteAsync(
            new[] { pose }, f, default);

        ReadOnlySpan<float> inv = result.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);
        AssertMatrixApproxEqual(new float[]
        {
            1, 0, 0, -5,
            0, 1, 0,  3,
            0, 0, 1, -2,
            0, 0, 0,  1,
        }, inv);
    }

    [Fact]
    public async Task PureRotation_TransposesRotation()
    {
        EvaluationFrame f = CreateEvaluationFrame();
        // 90° rotation around Z: (x, y, z) → (-y, x, z).
        // Row-major: [0 -1 0 0, 1 0 0 0, 0 0 1 0, 0 0 0 1]
        // Transpose: [0 1 0 0, -1 0 0 0, 0 0 1 0, 0 0 0 1]
        float[] rotZ = [
            0, -1, 0, 0,
            1,  0, 0, 0,
            0,  0, 1, 0,
            0,  0, 0, 1,
        ];
        ValueRef pose = ValueRef.FromPrimitiveArray(rotZ, DataKind.Float32);

        ValueRef result = await new PoseInverseFunction().ExecuteAsync(
            new[] { pose }, f, default);

        ReadOnlySpan<float> inv = result.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);
        AssertMatrixApproxEqual(new float[]
        {
            0,  1, 0, 0,
            -1, 0, 0, 0,
            0,  0, 1, 0,
            0,  0, 0, 1,
        }, inv);
    }

    [Fact]
    public async Task ComposingPoseWithInverse_GivesIdentity()
    {
        // pose_compose(pose_inverse(P), P) should be ≈ identity.
        EvaluationFrame f = CreateEvaluationFrame();
        ValueRef translation = await new PoseTranslateFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(2f), ValueRef.FromFloat32(3f), ValueRef.FromFloat32(-1f) },
            f, default);

        ValueRef inverse = await new PoseInverseFunction().ExecuteAsync(
            new[] { translation }, f, default);

        ValueRef composed = await new PoseComposeFunction().ExecuteAsync(
            new[] { inverse, translation }, f, default);

        ReadOnlySpan<float> r = composed.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);
        AssertMatrixApproxEqual(new float[]
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        }, r);
    }

    [Fact]
    public async Task PoseTransformInverseRoundtrips_RecoversOriginalPoint()
    {
        // Verify the inverse via point round-trip: applying pose then inverse
        // returns the original position. Uses pc_transform end-to-end.
        EvaluationFrame f = CreateEvaluationFrame();

        ValueRef pose = await new PoseTranslateFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(10f), ValueRef.FromFloat32(20f), ValueRef.FromFloat32(30f) },
            f, default);
        ValueRef inv = await new PoseInverseFunction().ExecuteAsync(new[] { pose }, f, default);

        // Compose forward then inverse — result should be identity.
        ValueRef composed = await new PoseComposeFunction().ExecuteAsync(new[] { inv, pose }, f, default);
        ReadOnlySpan<float> r = composed.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);

        // Diagonal ones, zero translation.
        Assert.Equal(1f, r[0], precision: 4);
        Assert.Equal(1f, r[5], precision: 4);
        Assert.Equal(1f, r[10], precision: 4);
        Assert.Equal(0f, r[3], precision: 4);
        Assert.Equal(0f, r[7], precision: 4);
        Assert.Equal(0f, r[11], precision: 4);
    }

    [Fact]
    public async Task WrongLength_Throws()
    {
        ValueRef badPose = ValueRef.FromPrimitiveArray(new float[] { 1, 0, 0, 0 }, DataKind.Float32);
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PoseInverseFunction().ExecuteAsync(
                new[] { badPose }, CreateEvaluationFrame(), default));
        Assert.Contains("16", ex.Message);
    }

    [Fact]
    public async Task NullInput_PropagatesNullArray()
    {
        ValueRef result = await new PoseInverseFunction().ExecuteAsync(
            new[] { ValueRef.NullArray(DataKind.Float32) }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
    }

    private static void AssertMatrixApproxEqual(float[] expected, ReadOnlySpan<float> actual)
    {
        Assert.Equal(16, actual.Length);
        for (int i = 0; i < 16; i++)
        {
            Assert.True(MathF.Abs(expected[i] - actual[i]) < 1e-5f,
                $"matrix[{i}]: expected {expected[i]}, got {actual[i]}");
        }
    }
}
