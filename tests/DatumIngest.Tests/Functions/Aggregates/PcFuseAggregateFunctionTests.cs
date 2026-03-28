using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Tests.Functions.Aggregates;

/// <summary>
/// Tests for <see cref="PcFuseAggregateFunction"/> — aggregate-shape PointCloud
/// concatenation. Mirrors PcFuseFunctionTests' coverage, plus the aggregate
/// lifecycle methods (Merge, Reset).
/// </summary>
public sealed class PcFuseAggregateFunctionTests
{
    [Fact]
    public async Task AccumulateThree_ProducesConcatenatedCloud()
    {
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseAggregateFunction().CreateAccumulator();

        DataValue a = BuildColoredCloud(arena, new[]
        {
            (new Vector3(0, 0, 0), (byte)255, (byte)0, (byte)0),
            (new Vector3(1, 0, 0), (byte)0, (byte)255, (byte)0),
        });
        DataValue b = BuildColoredCloud(arena, new[] { (new Vector3(0, 1, 0), (byte)0, (byte)0, (byte)255) });
        DataValue c = BuildColoredCloud(arena, new[] { (new Vector3(0, 0, 1), (byte)128, (byte)128, (byte)128) });

        acc.Accumulate(new[] { a }, frame);
        acc.Accumulate(new[] { b }, frame);
        acc.Accumulate(new[] { c }, frame);

        DataValue result = await acc.ResultAsync(frame);
        PointCloudHeader header = PointCloudHeader.Read(result.AsByteSpan(frame.Target));
        Assert.Equal(4u, header.PointCount);
        Assert.True(header.HasColor);
        Assert.False(header.IsOrganized);
    }

    [Fact]
    public async Task BboxUnionsAcrossContributions()
    {
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseAggregateFunction().CreateAccumulator();

        acc.Accumulate(new[] {
            BuildColoredCloud(arena, new[] { (new Vector3(-5, -5, -5), (byte)0, (byte)0, (byte)0) })
        }, frame);
        acc.Accumulate(new[] {
            BuildColoredCloud(arena, new[] { (new Vector3(3, 7, 2), (byte)0, (byte)0, (byte)0) })
        }, frame);

        DataValue result = await acc.ResultAsync(frame);
        PointCloudHeader header = PointCloudHeader.Read(result.AsByteSpan(frame.Target));
        Assert.Equal(new Vector3(-5, -5, -5), header.BboxMin);
        Assert.Equal(new Vector3(3, 7, 2), header.BboxMax);
    }

    [Fact]
    public async Task EmptyAggregation_ReturnsZeroPointCloud()
    {
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseAggregateFunction().CreateAccumulator();

        DataValue result = await acc.ResultAsync(frame);
        PointCloudHeader header = PointCloudHeader.Read(result.AsByteSpan(frame.Target));
        Assert.Equal(0u, header.PointCount);
        Assert.Equal(DataKind.PointCloud, result.Kind);
    }

    [Fact]
    public async Task NullInputs_AreSkipped()
    {
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseAggregateFunction().CreateAccumulator();

        acc.Accumulate(new[] { DataValue.Null(DataKind.PointCloud) }, frame);
        acc.Accumulate(new[] {
            BuildColoredCloud(arena, new[] { (new Vector3(1, 1, 1), (byte)0, (byte)0, (byte)0) })
        }, frame);
        acc.Accumulate(new[] { DataValue.Null(DataKind.PointCloud) }, frame);

        DataValue result = await acc.ResultAsync(frame);
        PointCloudHeader header = PointCloudHeader.Read(result.AsByteSpan(frame.Target));
        Assert.Equal(1u, header.PointCount);
    }

    [Fact]
    public async Task MixedColor_StripsColorFromOutput()
    {
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseAggregateFunction().CreateAccumulator();

        acc.Accumulate(new[] {
            BuildColoredCloud(arena, new[] { (new Vector3(0, 0, 0), (byte)100, (byte)100, (byte)100) })
        }, frame);
        acc.Accumulate(new[] { BuildPositionOnlyCloud(arena, new[] { new Vector3(1, 1, 1) }) }, frame);

        DataValue result = await acc.ResultAsync(frame);
        PointCloudHeader header = PointCloudHeader.Read(result.AsByteSpan(frame.Target));
        Assert.Equal(2u, header.PointCount);
        Assert.False(header.HasColor);  // stripped because one contributor was position-only
    }

    [Fact]
    public async Task ConflictingFrames_ThrowsOnAccumulate()
    {
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseAggregateFunction().CreateAccumulator();

        acc.Accumulate(new[] { BuildColoredCloud(arena, new[] { (new Vector3(0, 0, 0), (byte)0, (byte)0, (byte)0) }, PointCloudCoordinateFrame.CameraOpenGl) }, frame);

        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            acc.Accumulate(new[] {
                BuildColoredCloud(arena, new[] { (new Vector3(1, 1, 1), (byte)0, (byte)0, (byte)0) }, PointCloudCoordinateFrame.CameraOpenCv)
            }, frame));
        Assert.Contains("frame", ex.Message, StringComparison.OrdinalIgnoreCase);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Merge_CombinesTwoAccumulators()
    {
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateFunction func = new PcFuseAggregateFunction();
        IAggregateAccumulator a = func.CreateAccumulator();
        IAggregateAccumulator b = func.CreateAccumulator();

        a.Accumulate(new[] { BuildColoredCloud(arena, new[] { (new Vector3(0, 0, 0), (byte)0, (byte)0, (byte)0) }) }, frame);
        a.Accumulate(new[] { BuildColoredCloud(arena, new[] { (new Vector3(1, 0, 0), (byte)0, (byte)0, (byte)0) }) }, frame);
        b.Accumulate(new[] { BuildColoredCloud(arena, new[] { (new Vector3(2, 0, 0), (byte)0, (byte)0, (byte)0) }) }, frame);

        await a.MergeAsync(b, frame);
        DataValue result = await a.ResultAsync(frame);
        PointCloudHeader header = PointCloudHeader.Read(result.AsByteSpan(frame.Target));
        Assert.Equal(3u, header.PointCount);
    }

    [Fact]
    public async Task Reset_ClearsState()
    {
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseAggregateFunction().CreateAccumulator();

        acc.Accumulate(new[] { BuildColoredCloud(arena, new[] { (new Vector3(0, 0, 0), (byte)0, (byte)0, (byte)0) }) }, frame);
        acc.Reset();

        DataValue result = await acc.ResultAsync(frame);
        PointCloudHeader header = PointCloudHeader.Read(result.AsByteSpan(frame.Target));
        Assert.Equal(0u, header.PointCount);
    }

    [Fact]
    public void ValidateArguments_RejectsWrongKind()
    {
        IAggregateFunction func = new PcFuseAggregateFunction();
        Assert.Throws<ArgumentException>(() =>
            func.ValidateArguments(new[] { DataKind.Int32 }));
    }

    [Fact]
    public void ValidateArguments_AcceptsPointCloud()
    {
        IAggregateFunction func = new PcFuseAggregateFunction();
        DataKind result = func.ValidateArguments(new[] { DataKind.PointCloud });
        Assert.Equal(DataKind.PointCloud, result);
    }

    // ───────────────────────── Builders ─────────────────────────

    private static DataValue BuildColoredCloud(
        Arena arena,
        (Vector3 pos, byte r, byte g, byte b)[] points,
        PointCloudCoordinateFrame frameKind = PointCloudCoordinateFrame.CameraOpenGl)
    {
        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);
        foreach ((Vector3 p, _, _, _) in points)
        {
            bboxMin = Vector3.Min(bboxMin, p);
            bboxMax = Vector3.Max(bboxMax, p);
        }

        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: frameKind,
            PointCount: (uint)points.Length,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: 0,
            Height: 0);
        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        int offset = PointCloudHeader.SizeBytes;
        foreach ((Vector3 pos, byte r, byte g, byte b) in points)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 0, 4), pos.X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), pos.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), pos.Z);
            span[offset + 12] = r;
            span[offset + 13] = g;
            span[offset + 14] = b;
            span[offset + 15] = 255;
            offset += 16;
        }
        return DataValue.FromPointCloud(blob, arena);
    }

    private static DataValue BuildPositionOnlyCloud(Arena arena, Vector3[] points)
    {
        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);
        foreach (Vector3 p in points)
        {
            bboxMin = Vector3.Min(bboxMin, p);
            bboxMax = Vector3.Max(bboxMax, p);
        }
        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.None,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: (uint)points.Length,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: 0,
            Height: 0);
        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        int offset = PointCloudHeader.SizeBytes;
        foreach (Vector3 p in points)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 0, 4), p.X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), p.Z);
            offset += PointCloudHeader.PositionStrideBytes;
        }
        return DataValue.FromPointCloud(blob, arena);
    }
}
