using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Spatial;

public sealed class PcFilterDepthPercentileFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task DropsFarthestThirty_OnLowerBoundOnly()
    {
        // 10 points at Z = -1, -2, -3, ..., -10. Lowest Z = farthest in GL frame.
        // (0.3, 1.0) drops the smallest 30% → drops Z=-10, -9, -8 → keeps 7 points.
        ValueRef pc = BuildCloudWithZValues(Enumerable.Range(1, 10).Select(i => -(float)i).ToArray());

        ValueRef result = await new PcFilterDepthPercentileFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(0.3f), ValueRef.FromFloat32(1.0f) },
            CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(7u, header.PointCount);
        // BboxMin.Z should be ~-7 (the cutoff at 30th percentile of [-10..-1]).
        Assert.True(header.BboxMin.Z >= -7.5f && header.BboxMin.Z <= -6.5f,
            $"expected BboxMin.Z ≈ -7, got {header.BboxMin.Z}");
    }

    [Fact]
    public async Task KeepsEverything_For0to1()
    {
        ValueRef pc = BuildCloudWithZValues(new[] { -1f, -2f, -3f, -4f, -5f });

        ValueRef result = await new PcFilterDepthPercentileFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f) },
            CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(5u, header.PointCount);
    }

    [Fact]
    public async Task SymmetricTrim_DropsBothEnds()
    {
        // Z = -1, -2, ..., -10. (0.1, 0.9) drops bottom 10% and top 10%.
        // 10% of 9 ≈ 0.9 → keeps from rank 0.9 to rank 8.1.
        // Should drop the most-extreme value on each end (rough heuristic).
        ValueRef pc = BuildCloudWithZValues(Enumerable.Range(1, 10).Select(i => -(float)i).ToArray());

        ValueRef result = await new PcFilterDepthPercentileFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(0.1f), ValueRef.FromFloat32(0.9f) },
            CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        // Linear-interpolation percentiles at 0.1 and 0.9 over 10 points produce
        // tight cutoffs that drop only the absolute extremes.
        Assert.InRange((int)header.PointCount, 8, 10);
        Assert.True(header.BboxMin.Z > -10f, "extreme far should be trimmed");
        Assert.True(header.BboxMax.Z < -1f, "extreme near should be trimmed");
    }

    [Fact]
    public async Task NaNAndInfDropped_Unconditionally()
    {
        ValueRef pc = BuildCloudWithZValues(new[] { -1f, -2f, float.NaN, float.PositiveInfinity, -3f, float.NegativeInfinity });

        // (0, 1) is "no percentile trim" — but NaN/Inf still get dropped.
        ValueRef result = await new PcFilterDepthPercentileFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f) },
            CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(3u, header.PointCount);   // -1, -2, -3 survive
    }

    [Fact]
    public async Task EmptyCloud_ReturnsEmpty()
    {
        ValueRef empty = await new PcEmptyFunction().ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty, CreateEvaluationFrame(), default);

        ValueRef result = await new PcFilterDepthPercentileFunction().ExecuteAsync(
            new[] { empty, ValueRef.FromFloat32(0.3f), ValueRef.FromFloat32(1.0f) },
            CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(0u, header.PointCount);
    }

    [Fact]
    public async Task LowerGreaterThanUpper_Throws()
    {
        ValueRef pc = BuildCloudWithZValues(new[] { -1f });

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PcFilterDepthPercentileFunction().ExecuteAsync(
                new[] { pc, ValueRef.FromFloat32(0.8f), ValueRef.FromFloat32(0.3f) },
                CreateEvaluationFrame(), default));
        Assert.Contains("upper", ex.Message);
    }

    [Fact]
    public async Task OutOfRangeBounds_Throw()
    {
        ValueRef pc = BuildCloudWithZValues(new[] { -1f });
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PcFilterDepthPercentileFunction().ExecuteAsync(
                new[] { pc, ValueRef.FromFloat32(-0.5f), ValueRef.FromFloat32(1.0f) },
                CreateEvaluationFrame(), default));
        Assert.Contains("[0, 1]", ex.Message);
    }

    [Fact]
    public async Task NullInput_PropagatesNull()
    {
        ValueRef result = await new PcFilterDepthPercentileFunction().ExecuteAsync(
            new[] {
                ValueRef.Null(DataKind.PointCloud),
                ValueRef.FromFloat32(0.0f),
                ValueRef.FromFloat32(0.7f),
            },
            CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.PointCloud, result.Kind);
    }

    [Fact]
    public async Task ScaleAdaptive_BehavesIdenticallyAcrossDifferentDepthScales()
    {
        // Same SHAPE of distribution, different absolute scales.
        // Both should keep the same FRACTION of points at percentile (0.3, 1.0).
        ValueRef normalized = BuildCloudWithZValues(new[] { -0.1f, -0.2f, -0.3f, -0.4f, -0.5f, -0.6f, -0.7f, -0.8f, -0.9f, -1.0f });
        ValueRef metric    = BuildCloudWithZValues(new[] { -1f, -2f, -3f, -4f, -5f, -6f, -7f, -8f, -9f, -10f });

        ValueRef normResult = await new PcFilterDepthPercentileFunction().ExecuteAsync(
            new[] { normalized, ValueRef.FromFloat32(0.3f), ValueRef.FromFloat32(1.0f) },
            CreateEvaluationFrame(), default);
        ValueRef metricResult = await new PcFilterDepthPercentileFunction().ExecuteAsync(
            new[] { metric, ValueRef.FromFloat32(0.3f), ValueRef.FromFloat32(1.0f) },
            CreateEvaluationFrame(), default);

        PointCloudHeader hNorm = PointCloudHeader.Read(normResult.AsPointCloud());
        PointCloudHeader hMet  = PointCloudHeader.Read(metricResult.AsPointCloud());
        Assert.Equal(hNorm.PointCount, hMet.PointCount);
    }

    private static ValueRef BuildCloudWithZValues(float[] zValues)
    {
        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);
        foreach (float z in zValues)
        {
            if (float.IsFinite(z))
            {
                bboxMin = Vector3.Min(bboxMin, new Vector3(0, 0, z));
                bboxMax = Vector3.Max(bboxMax, new Vector3(0, 0, z));
            }
        }
        if (!float.IsFinite(bboxMin.X)) { bboxMin = Vector3.Zero; bboxMax = Vector3.Zero; }

        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: (uint)zValues.Length,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: 0,
            Height: 0);
        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        int offset = PointCloudHeader.SizeBytes;
        foreach (float z in zValues)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 0, 4), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), z);
            span[offset + 12] = 128;
            span[offset + 13] = 128;
            span[offset + 14] = 128;
            span[offset + 15] = 255;
            offset += 16;
        }
        return ValueRef.FromPointCloud(blob);
    }
}
