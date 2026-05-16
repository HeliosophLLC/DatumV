using System.Numerics;
using DatumIngest.IO;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Tests.Model.Spatial;

/// <summary>
/// Slice A coverage for <see cref="DataKind.PointCloud"/> — header layout
/// (read/write/derived fields) and the arena / wire-format round-trip for
/// <see cref="DataValue.FromPointCloud"/> ↔ <see cref="DataValue.AsPointCloud"/>.
/// Construction-side scalars (point_cloud_from_depth, accessors) ship in Slice B.
/// </summary>
public sealed class PointCloudRoundTripTests : ServiceTestBase
{
    // ─────────────────────── Header layout: read/write ───────────────────────

    [Fact]
    public void Header_RoundTrips_PositionOnlyUnorganized()
    {
        PointCloudHeader original = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.None,
            CoordinateFrame: PointCloudCoordinateFrame.Unspecified,
            PointCount: 1234,
            BboxMin: new Vector3(-1.5f, -2.25f, -3.75f),
            BboxMax: new Vector3(1.5f, 2.25f, 3.75f),
            Width: 0,
            Height: 0);

        Span<byte> buffer = stackalloc byte[PointCloudHeader.SizeBytes];
        original.Write(buffer);
        PointCloudHeader restored = PointCloudHeader.Read(buffer);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Header_RoundTrips_OrganizedWithColor()
    {
        PointCloudHeader original = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: 1920 * 1080,
            BboxMin: new Vector3(0, 0, 0),
            BboxMax: new Vector3(10, 8, 12),
            Width: 1920,
            Height: 1080);

        Span<byte> buffer = stackalloc byte[PointCloudHeader.SizeBytes];
        original.Write(buffer);
        PointCloudHeader restored = PointCloudHeader.Read(buffer);

        Assert.Equal(original, restored);
        Assert.True(restored.HasColor);
        Assert.True(restored.IsOrganized);
        Assert.Equal(PointCloudCoordinateFrame.CameraOpenGl, restored.CoordinateFrame);
    }

    [Fact]
    public void Header_Read_RejectsUnsupportedVersion()
    {
        Span<byte> buffer = stackalloc byte[PointCloudHeader.SizeBytes];
        buffer[0] = 99; // bogus version

        byte[] bufCopy = buffer.ToArray();
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PointCloudHeader.Read(bufCopy));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Header_Read_RejectsTooShortBuffer()
    {
        byte[] tooSmall = new byte[PointCloudHeader.SizeBytes - 1];
        Assert.Throws<ArgumentException>(() => PointCloudHeader.Read(tooSmall));
    }

    [Fact]
    public void Header_Write_RejectsTooShortBuffer()
    {
        PointCloudHeader header = MakeMinimalHeader(pointCount: 0);
        byte[] tooSmall = new byte[PointCloudHeader.SizeBytes - 1];
        Assert.Throws<ArgumentException>(() => header.Write(tooSmall));
    }

    [Fact]
    public void Header_SizeBytes_Is40()
    {
        Assert.Equal(40, PointCloudHeader.SizeBytes);
    }

    // ─────────────────────── Derived fields ───────────────────────

    [Theory]
    [InlineData(PointCloudFlags.None, 12)]
    [InlineData(PointCloudFlags.HasColor, 16)]
    [InlineData(PointCloudFlags.HasColor | PointCloudFlags.HasNormals, 28)]
    [InlineData(PointCloudFlags.HasColor | PointCloudFlags.HasNormals | PointCloudFlags.HasIntensity, 32)]
    [InlineData(PointCloudFlags.HasIntensity, 16)]
    public void PointStrideBytes_DerivedFromFlags(PointCloudFlags flags, int expectedStride)
    {
        PointCloudHeader header = MakeMinimalHeader(pointCount: 0) with { Flags = flags };
        Assert.Equal(expectedStride, header.PointStrideBytes);
    }

    [Theory]
    [InlineData(4u, 2u, 2u, true)]   // 2×2 = 4 points → organized
    [InlineData(0u, 0u, 0u, false)]  // empty unorganized
    [InlineData(4u, 0u, 0u, false)]  // dims absent → unorganized even with points
    [InlineData(4u, 3u, 2u, false)]  // dims don't multiply to count
    [InlineData(6u, 3u, 2u, true)]   // 3×2 = 6 → organized
    public void IsOrganized_TruthTable(uint pointCount, uint width, uint height, bool expected)
    {
        PointCloudHeader header = MakeMinimalHeader(pointCount) with { Width = width, Height = height };
        Assert.Equal(expected, header.IsOrganized);
    }

    [Fact]
    public void TotalSizeBytes_HeaderPlusStridePerPoint()
    {
        PointCloudHeader header = MakeMinimalHeader(pointCount: 100) with { Flags = PointCloudFlags.HasColor };
        Assert.Equal(PointCloudHeader.SizeBytes + 100L * 16, header.TotalSizeBytes);
    }

    // ─────────────────────── DataValue round-trip ───────────────────────

    [Fact]
    public void DataValue_FromPointCloud_AsPointCloud_RoundTripsBlob()
    {
        Arena store = CreateArena();
        byte[] blob = BuildSamplePointCloudBlob();

        DataValue value = DataValue.FromPointCloud(blob, store);

        Assert.Equal(DataKind.PointCloud, value.Kind);
        Assert.True(value.IsBlobKind);
        Assert.False(value.IsInSidecar);

        byte[] restored = value.AsPointCloud(store);
        Assert.Equal(blob, restored);
    }

    [Fact]
    public void DataValue_PointCloud_OffsetEqualityAndHash()
    {
        Arena store = CreateArena();
        byte[] blob = BuildSamplePointCloudBlob();

        DataValue a = DataValue.FromPointCloud(blob, store);
        DataValue b = a;  // same (_p0, _p1) in the same store — offset-equal

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // A second blob stored separately ends up at a different arena offset →
        // offset-inequality, even though byte contents match. This matches the
        // documented offset-equality semantics for Image/Audio/Video/Json.
        DataValue c = DataValue.FromPointCloud(blob, store);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void DataValue_AsByteSpan_ReadsPointCloudBlob()
    {
        Arena store = CreateArena();
        byte[] blob = BuildSamplePointCloudBlob();

        DataValue value = DataValue.FromPointCloud(blob, store);
        ReadOnlySpan<byte> span = value.AsByteSpan(store);

        Assert.Equal(blob.Length, span.Length);
        Assert.True(span.SequenceEqual(blob));
    }

    [Fact]
    public void DataValue_WireFormat_RoundTripsThroughDataValueWriterReader()
    {
        Arena writeStore = CreateArena();
        byte[] blob = BuildSamplePointCloudBlob();
        DataValue original = DataValue.FromPointCloud(blob, writeStore);

        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            DataValueWriter.WriteDataValue(writer, original, writeStore);
        }

        stream.Position = 0;
        Arena readStore = CreateArena();
        using BinaryReader reader = new(stream);
        DataValue restored = DataValueReader.ReadDataValue(reader, readStore);

        Assert.Equal(DataKind.PointCloud, restored.Kind);
        Assert.Equal(blob, restored.AsPointCloud(readStore));

        // Header survives the round-trip with semantically identical fields.
        PointCloudHeader expectedHeader = PointCloudHeader.Read(blob);
        PointCloudHeader actualHeader = PointCloudHeader.Read(restored.AsPointCloud(readStore));
        Assert.Equal(expectedHeader, actualHeader);
    }

    // ─────────────────────── Test helpers ───────────────────────

    /// <summary>
    /// Builds a 4-point organized PointCloud blob (2×2 grid, colored) — header
    /// plus 4 × 16-byte interleaved (xyz float32, rgba uint8) points. The points
    /// form a unit square in z=0 plane with primary-color corners; enough surface
    /// to assert byte-equality on round-trip.
    /// </summary>
    private static byte[] BuildSamplePointCloudBlob()
    {
        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: 4,
            BboxMin: new Vector3(0, 0, 0),
            BboxMax: new Vector3(1, 1, 0),
            Width: 2,
            Height: 2);

        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        (Vector3 pos, byte r, byte g, byte b, byte a)[] points =
        [
            (new(0, 0, 0), 255,   0,   0, 255),
            (new(1, 0, 0),   0, 255,   0, 255),
            (new(0, 1, 0),   0,   0, 255, 255),
            (new(1, 1, 0), 255, 255, 255, 255),
        ];

        int offset = PointCloudHeader.SizeBytes;
        foreach (var (pos, r, g, b, a) in points)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 0, 4), pos.X);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), pos.Y);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), pos.Z);
            span[offset + 12] = r;
            span[offset + 13] = g;
            span[offset + 14] = b;
            span[offset + 15] = a;
            offset += 16;
        }

        return blob;
    }

    private static PointCloudHeader MakeMinimalHeader(uint pointCount) => new(
        Version: PointCloudHeader.CurrentVersion,
        Flags: PointCloudFlags.None,
        CoordinateFrame: PointCloudCoordinateFrame.Unspecified,
        PointCount: pointCount,
        BboxMin: Vector3.Zero,
        BboxMax: Vector3.Zero,
        Width: 0,
        Height: 0);
}
