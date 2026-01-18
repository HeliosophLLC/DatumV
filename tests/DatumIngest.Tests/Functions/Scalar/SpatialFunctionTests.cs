using System.Numerics;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for the spatial function family — point construction, component
/// access, distance / squared distance — and registry resolution.
/// </summary>
public sealed class SpatialFunctionTests
{
    [Fact]
    public void Point2D_Construct_ReturnsPoint()
    {
        ValueRef result = Invoke(new Point2DFunction(), ValueRef.FromFloat32(1.5f), ValueRef.FromFloat32(-2.25f));
        Assert.Equal(DataKind.Point2D, result.Kind);
        Assert.Equal(new Vector2(1.5f, -2.25f), result.AsPoint2D());
    }

    [Fact]
    public void Point2D_AcceptsWidenedNumerics()
    {
        // Int32 inputs widen to Float32 — same friendliness as sqrt(int).
        ValueRef result = Invoke(new Point2DFunction(), ValueRef.FromInt32(3), ValueRef.FromFloat64(4.0));
        Assert.Equal(new Vector2(3f, 4f), result.AsPoint2D());
    }

    [Fact]
    public void Point2D_NullComponentReturnsNullPoint()
    {
        ValueRef result = Invoke(new Point2DFunction(), ValueRef.Null(DataKind.Float32), ValueRef.FromFloat32(1f));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Point2D, result.Kind);
    }

    [Fact]
    public void Point3D_Construct_ReturnsPoint()
    {
        ValueRef result = Invoke(new Point3DFunction(),
            ValueRef.FromFloat32(1f), ValueRef.FromFloat32(2f), ValueRef.FromFloat32(3f));
        Assert.Equal(DataKind.Point3D, result.Kind);
        Assert.Equal(new Vector3(1f, 2f, 3f), result.AsPoint3D());
    }

    [Fact]
    public void PointX_Y_OnPoint2D_ReturnsComponents()
    {
        ValueRef p = ValueRef.FromPoint2D(7f, 11f);
        Assert.Equal(7f, Invoke(new PointXFunction(), p).AsFloat32());
        Assert.Equal(11f, Invoke(new PointYFunction(), p).AsFloat32());
    }

    [Fact]
    public void PointX_Y_Z_OnPoint3D_ReturnsComponents()
    {
        ValueRef p = ValueRef.FromPoint3D(7f, 11f, 13f);
        Assert.Equal(7f, Invoke(new PointXFunction(), p).AsFloat32());
        Assert.Equal(11f, Invoke(new PointYFunction(), p).AsFloat32());
        Assert.Equal(13f, Invoke(new PointZFunction(), p).AsFloat32());
    }

    [Fact]
    public void PointZ_RejectsPoint2D()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new PointZFunction().ValidateArguments([DataKind.Point2D]));
    }

    [Fact]
    public void Distance_2D_IsEuclidean()
    {
        ValueRef a = ValueRef.FromPoint2D(0f, 0f);
        ValueRef b = ValueRef.FromPoint2D(3f, 4f);
        Assert.Equal(5f, Invoke(new DistanceFunction(), a, b).AsFloat32());
    }

    [Fact]
    public void Distance_3D_IsEuclidean()
    {
        ValueRef a = ValueRef.FromPoint3D(0f, 0f, 0f);
        ValueRef b = ValueRef.FromPoint3D(2f, 3f, 6f);
        Assert.Equal(7f, Invoke(new DistanceFunction(), a, b).AsFloat32());
    }

    [Fact]
    public void DistanceSq_2D_SkipsSqrt()
    {
        ValueRef a = ValueRef.FromPoint2D(0f, 0f);
        ValueRef b = ValueRef.FromPoint2D(3f, 4f);
        Assert.Equal(25f, Invoke(new DistanceSqFunction(), a, b).AsFloat32());
    }

    [Fact]
    public void Distance_RejectsMixedDimensionality()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new DistanceFunction().ValidateArguments([DataKind.Point2D, DataKind.Point3D]));
    }

    [Fact]
    public void Distance_NullArgReturnsNull()
    {
        ValueRef result = Invoke(new DistanceFunction(),
            ValueRef.Null(DataKind.Point2D), ValueRef.FromPoint2D(1f, 2f));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public void Spatial_RegisteredInDefaultRegistry()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<Point2DFunction>(registry.TryGetScalar("point2d"));
        Assert.IsType<Point3DFunction>(registry.TryGetScalar("point3d"));
        Assert.IsType<PointXFunction>(registry.TryGetScalar("point_x"));
        Assert.IsType<PointYFunction>(registry.TryGetScalar("point_y"));
        Assert.IsType<PointZFunction>(registry.TryGetScalar("point_z"));
        Assert.IsType<DistanceFunction>(registry.TryGetScalar("distance"));
        Assert.IsType<DistanceSqFunction>(registry.TryGetScalar("distance_sq"));
    }

    [Fact]
    public void Spatial_DescriptorsCarrySpatialCategory()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        FunctionDescriptor? d = registry.TryGetScalarDescriptor("distance");
        Assert.NotNull(d);
        Assert.Equal(FunctionCategory.Spatial, d.Category);
    }

    private static ValueRef Invoke(IScalarFunction function, params ValueRef[] arguments)
    {
        EvaluationFrame frame = default;
        return function.ExecuteAsync(arguments, frame, default).GetAwaiter().GetResult();
    }
}
