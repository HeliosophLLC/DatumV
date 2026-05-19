using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Spatial;

public sealed class PoseToEulerDegreesFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task Identity_ReturnsAllZeros()
    {
        EvaluationFrame f = CreateEvaluationFrame();
        ValueRef identity = await new PoseIdentityFunction().ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty, f, default);

        ValueRef result = await new PoseToEulerDegreesFunction().ExecuteAsync(
            new[] { identity }, f, default);

        ReadOnlySpan<float> angles = result.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);
        Assert.Equal(3, angles.Length);
        Assert.Equal(0f, angles[0], precision: 4);   // yaw
        Assert.Equal(0f, angles[1], precision: 4);   // pitch
        Assert.Equal(0f, angles[2], precision: 4);   // roll
    }

    [Fact]
    public async Task PureTranslation_ReturnsAllZeros()
    {
        EvaluationFrame f = CreateEvaluationFrame();
        ValueRef pose = await new PoseTranslateFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(5f), ValueRef.FromFloat32(-3f), ValueRef.FromFloat32(2f) },
            f, default);

        ValueRef result = await new PoseToEulerDegreesFunction().ExecuteAsync(
            new[] { pose }, f, default);

        ReadOnlySpan<float> angles = result.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);
        Assert.Equal(0f, angles[0], precision: 4);
        Assert.Equal(0f, angles[1], precision: 4);
        Assert.Equal(0f, angles[2], precision: 4);
    }

    [Fact]
    public async Task Yaw90Degrees_ExtractsYaw90()
    {
        EvaluationFrame f = CreateEvaluationFrame();
        // 90° yaw around Y axis (camera turns right). Row-major:
        //   r00 = cos(90) = 0,  r01 = 0,  r02 = sin(90) = 1
        //   r10 = 0,            r11 = 1,  r12 = 0
        //   r20 = -sin(90) = -1, r21 = 0, r22 = cos(90) = 0
        float[] rotY90 =
        [
            0, 0, 1, 0,
            0, 1, 0, 0,
            -1, 0, 0, 0,
            0, 0, 0, 1,
        ];
        ValueRef pose = ValueRef.FromPrimitiveArray(rotY90, DataKind.Float32);

        ValueRef result = await new PoseToEulerDegreesFunction().ExecuteAsync(
            new[] { pose }, f, default);

        ReadOnlySpan<float> angles = result.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);
        Assert.Equal(90f, angles[0], precision: 3);   // yaw = 90°
        Assert.Equal(0f, angles[1], precision: 3);
        Assert.Equal(0f, angles[2], precision: 3);
    }

    [Fact]
    public async Task Pitch45Degrees_ExtractsPitch45()
    {
        EvaluationFrame f = CreateEvaluationFrame();
        // 45° pitch around X axis. cos(45)=sin(45)=0.7071...
        const float c = 0.7071068f;
        const float s = 0.7071068f;
        float[] rotX45 =
        [
            1, 0,  0, 0,
            0, c, -s, 0,
            0, s,  c, 0,
            0, 0,  0, 1,
        ];
        ValueRef pose = ValueRef.FromPrimitiveArray(rotX45, DataKind.Float32);

        ValueRef result = await new PoseToEulerDegreesFunction().ExecuteAsync(
            new[] { pose }, f, default);

        ReadOnlySpan<float> angles = result.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);
        Assert.Equal(0f, angles[0], precision: 3);
        Assert.Equal(45f, angles[1], precision: 3);   // pitch = 45°
        Assert.Equal(0f, angles[2], precision: 3);
    }

    [Fact]
    public async Task Roll30Degrees_ExtractsRoll30()
    {
        EvaluationFrame f = CreateEvaluationFrame();
        // 30° roll around Z axis (camera tilts).
        float c = MathF.Cos(30f * MathF.PI / 180f);
        float s = MathF.Sin(30f * MathF.PI / 180f);
        float[] rotZ30 =
        [
            c, -s, 0, 0,
            s,  c, 0, 0,
            0,  0, 1, 0,
            0,  0, 0, 1,
        ];
        ValueRef pose = ValueRef.FromPrimitiveArray(rotZ30, DataKind.Float32);

        ValueRef result = await new PoseToEulerDegreesFunction().ExecuteAsync(
            new[] { pose }, f, default);

        ReadOnlySpan<float> angles = result.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);
        Assert.Equal(0f, angles[0], precision: 3);
        Assert.Equal(0f, angles[1], precision: 3);
        Assert.Equal(30f, angles[2], precision: 3);   // roll = 30°
    }

    [Fact]
    public async Task WrongLength_Throws()
    {
        ValueRef badPose = ValueRef.FromPrimitiveArray(new float[] { 1, 0, 0 }, DataKind.Float32);
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PoseToEulerDegreesFunction().ExecuteAsync(
                new[] { badPose }, CreateEvaluationFrame(), default));
        Assert.Contains("16", ex.Message);
    }

    [Fact]
    public async Task NullInput_PropagatesNullArray()
    {
        ValueRef result = await new PoseToEulerDegreesFunction().ExecuteAsync(
            new[] { ValueRef.NullArray(DataKind.Float32) },
            CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
    }
}
