using System.Buffers.Binary;
using System.Numerics;
using System.Text;

using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="PointCloudToPlyFunction"/> — binary PLY export.
/// Verifies the ASCII header structure plus the binary payload byte layout.
/// </summary>
public sealed class PointCloudToPlyFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task ColoredCloud_HeaderDeclaresRgbProperties()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(0, 0, 0), (byte)10, (byte)20, (byte)30),
            (new Vector3(1, 1, 1), (byte)40, (byte)50, (byte)60),
        });

        ValueRef result = await new PointCloudToPlyFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        string headerText = ReadAsciiHeader(result.AsBytes());

        Assert.StartsWith("ply\n", headerText);
        Assert.Contains("format binary_little_endian 1.0", headerText);
        Assert.Contains("element vertex 2", headerText);
        Assert.Contains("property float x", headerText);
        Assert.Contains("property float y", headerText);
        Assert.Contains("property float z", headerText);
        Assert.Contains("property uchar red", headerText);
        Assert.Contains("property uchar green", headerText);
        Assert.Contains("property uchar blue", headerText);
        Assert.DoesNotContain("property uchar alpha", headerText);   // intentionally dropped
        Assert.EndsWith("end_header\n", headerText);
    }

    [Fact]
    public async Task PositionOnlyCloud_HeaderOmitsColorProperties()
    {
        ValueRef pc = BuildPositionOnlyCloud(new[] { new Vector3(0, 0, 0), new Vector3(1, 1, 1) });

        ValueRef result = await new PointCloudToPlyFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        string headerText = ReadAsciiHeader(result.AsBytes());

        Assert.Contains("property float x", headerText);
        Assert.DoesNotContain("property uchar red", headerText);
        Assert.DoesNotContain("property uchar green", headerText);
        Assert.DoesNotContain("property uchar blue", headerText);
    }

    [Fact]
    public async Task ColoredCloud_BinaryPayloadHasExpectedByteLayout()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(1.5f, 2.5f, 3.5f), (byte)200, (byte)100, (byte)50),
        });

        ValueRef result = await new PointCloudToPlyFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        byte[] ply = result.AsBytes();

        int headerEnd = FindHeaderEnd(ply);
        ReadOnlySpan<byte> payload = ply.AsSpan(headerEnd);

        // 12 bytes position + 3 bytes RGB = 15 bytes per vertex.
        Assert.Equal(15, payload.Length);
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(payload[0..4]));
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(payload[4..8]));
        Assert.Equal(3.5f, BinaryPrimitives.ReadSingleLittleEndian(payload[8..12]));
        Assert.Equal(200, payload[12]);
        Assert.Equal(100, payload[13]);
        Assert.Equal(50, payload[14]);
    }

    [Fact]
    public async Task CameraOpenCvCloud_NegatesYAndZ()
    {
        // Source point in OpenCV frame: (1, 2, 3). PLY emits in OpenGL frame
        // (+Y up, -Z forward), so y and z flip sign at the export boundary —
        // matches the GLTF/OBJ/STL exporters' convention.
        ValueRef pc = BuildColoredCloud(
            new[] { (new Vector3(1, 2, 3), (byte)0, (byte)0, (byte)0) },
            frame: PointCloudCoordinateFrame.CameraOpenCv);

        ValueRef result = await new PointCloudToPlyFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        byte[] ply = result.AsBytes();

        int headerEnd = FindHeaderEnd(ply);
        ReadOnlySpan<byte> payload = ply.AsSpan(headerEnd);
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(payload[0..4]));
        Assert.Equal(-2f, BinaryPrimitives.ReadSingleLittleEndian(payload[4..8]));
        Assert.Equal(-3f, BinaryPrimitives.ReadSingleLittleEndian(payload[8..12]));
    }

    [Fact]
    public async Task EmptyCloud_EmitsHeaderOnly()
    {
        ValueRef empty = await new PcEmptyFunction().ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty, CreateEvaluationFrame(), default);

        ValueRef result = await new PointCloudToPlyFunction().ExecuteAsync(
            new[] { empty }, CreateEvaluationFrame(), default);
        byte[] ply = result.AsBytes();

        string headerText = ReadAsciiHeader(ply);
        Assert.Contains("element vertex 0", headerText);
        Assert.Equal(headerText.Length, ply.Length);   // no binary payload
    }

    [Fact]
    public async Task NullInput_ReturnsNullArray()
    {
        ValueRef result = await new PointCloudToPlyFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.PointCloud) }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.UInt8, result.Kind);
    }

    private static string ReadAsciiHeader(byte[] ply)
    {
        int headerEnd = FindHeaderEnd(ply);
        return Encoding.ASCII.GetString(ply, 0, headerEnd);
    }

    private static int FindHeaderEnd(byte[] ply)
    {
        const string Terminator = "end_header\n";
        ReadOnlySpan<byte> span = ply;
        ReadOnlySpan<byte> needle = Encoding.ASCII.GetBytes(Terminator);
        int idx = span.IndexOf(needle);
        Assert.True(idx >= 0, "PLY output is missing end_header terminator");
        return idx + needle.Length;
    }

    private static ValueRef BuildColoredCloud(
        (Vector3 pos, byte r, byte g, byte b)[] points,
        PointCloudCoordinateFrame frame = PointCloudCoordinateFrame.CameraOpenGl)
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
            CoordinateFrame: frame,
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
        return ValueRef.FromPointCloud(blob);
    }

    private static ValueRef BuildPositionOnlyCloud(Vector3[] points)
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
        return ValueRef.FromPointCloud(blob);
    }
}
