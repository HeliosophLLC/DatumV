using System.Numerics;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Tests.Model.Spatial;

/// <summary>
/// Coverage for <see cref="MeshHeader"/> — read/write round-trip, version
/// guard, supported-flag guard (Phase 1 actively rejects Phase 2 flag bits
/// per the design decision in the project memory), too-short-buffer guard,
/// flag-derived stride, and total-size computation including the optional
/// embedded texture.
/// </summary>
public sealed class MeshHeaderTests : ServiceTestBase
{
    // ─────────────────────── Layout constants ───────────────────────

    [Fact]
    public void Header_SizeBytes_Is48()
    {
        Assert.Equal(48, MeshHeader.SizeBytes);
    }

    [Fact]
    public void Header_SupportedFlags_IsHasColorPlusHasNormals()
    {
        // Phase 1 contract — UV / Texture are reserved.
        Assert.Equal(MeshFlags.HasColor | MeshFlags.HasNormals, MeshHeader.SupportedFlags);
    }

    // ─────────────────────── Header read/write round-trip ───────────────────────

    [Fact]
    public void Header_RoundTrips_PositionOnly()
    {
        // Position-only is a degenerate Phase 1 case — still a valid header,
        // just no per-vertex attributes. Stride = 12 (position only).
        MeshHeader original = new(
            Version: MeshHeader.CurrentVersion,
            Flags: MeshFlags.None,
            CoordinateFrame: PointCloudCoordinateFrame.Unspecified,
            VertexCount: 100,
            TriangleCount: 50,
            BboxMin: new Vector3(-1, -1, -1),
            BboxMax: new Vector3(1, 1, 1),
            TextureOffset: 0,
            TextureLength: 0);

        Span<byte> buffer = stackalloc byte[MeshHeader.SizeBytes];
        original.Write(buffer);
        MeshHeader restored = MeshHeader.Read(buffer);

        Assert.Equal(original, restored);
        Assert.False(restored.HasColor);
        Assert.False(restored.HasNormals);
    }

    [Fact]
    public void Header_RoundTrips_ColorAndNormals()
    {
        MeshHeader original = new(
            Version: MeshHeader.CurrentVersion,
            Flags: MeshFlags.HasColor | MeshFlags.HasNormals,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            VertexCount: 1024,
            TriangleCount: 2046,
            BboxMin: new Vector3(-0.5f, -0.5f, -1.0f),
            BboxMax: new Vector3(0.5f, 0.5f, -0.1f),
            TextureOffset: 0,
            TextureLength: 0);

        Span<byte> buffer = stackalloc byte[MeshHeader.SizeBytes];
        original.Write(buffer);
        MeshHeader restored = MeshHeader.Read(buffer);

        Assert.Equal(original, restored);
        Assert.True(restored.HasColor);
        Assert.True(restored.HasNormals);
        Assert.Equal(PointCloudCoordinateFrame.CameraOpenGl, restored.CoordinateFrame);
    }

    // ─────────────────────── Guard tests ───────────────────────

    [Fact]
    public void Header_Read_RejectsUnsupportedVersion()
    {
        Span<byte> buffer = stackalloc byte[MeshHeader.SizeBytes];
        buffer[0] = 99; // bogus version

        byte[] bufCopy = buffer.ToArray();
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MeshHeader.Read(bufCopy));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MeshFlags.HasUVs)]
    [InlineData(MeshFlags.HasTexture)]
    [InlineData(MeshFlags.HasUVs | MeshFlags.HasTexture)]
    [InlineData(MeshFlags.HasColor | MeshFlags.HasUVs)]
    public void Header_Read_RejectsUnsupportedPhase2Flags(MeshFlags phase2Flags)
    {
        // Phase 1 must actively reject Phase 2 flag bits rather than silently
        // drop them — otherwise consumers would observe a Mesh value whose
        // declared flags claim attributes the reader didn't decode.
        Span<byte> buffer = stackalloc byte[MeshHeader.SizeBytes];
        buffer[0] = MeshHeader.CurrentVersion;
        buffer[1] = (byte)phase2Flags;

        byte[] bufCopy = buffer.ToArray();
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MeshHeader.Read(bufCopy));
        Assert.Contains("unsupported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Header_Read_RejectsTooShortBuffer()
    {
        byte[] tooSmall = new byte[MeshHeader.SizeBytes - 1];
        Assert.Throws<ArgumentException>(() => MeshHeader.Read(tooSmall));
    }

    [Fact]
    public void Header_Write_RejectsTooShortBuffer()
    {
        MeshHeader header = MakeMinimalHeader(vertexCount: 0, triangleCount: 0);
        byte[] tooSmall = new byte[MeshHeader.SizeBytes - 1];
        Assert.Throws<ArgumentException>(() => header.Write(tooSmall));
    }

    // ─────────────────────── Derived fields ───────────────────────

    [Theory]
    [InlineData(MeshFlags.None, 12)]                                                  // position only
    [InlineData(MeshFlags.HasColor, 16)]                                              // pos + 4-byte color
    [InlineData(MeshFlags.HasNormals, 24)]                                            // pos + 12-byte normal
    [InlineData(MeshFlags.HasColor | MeshFlags.HasNormals, 28)]                       // pos + color + normal
    public void VertexStrideBytes_DerivedFromFlags(MeshFlags flags, int expectedStride)
    {
        MeshHeader header = MakeMinimalHeader(vertexCount: 0, triangleCount: 0) with { Flags = flags };
        Assert.Equal(expectedStride, header.VertexStrideBytes);
    }

    [Fact]
    public void TotalSizeBytes_HeaderPlusVerticesPlusTriangles()
    {
        // 100 vertices × 28 bytes (pos+color+normal) = 2800
        // 50 triangles × 12 bytes (3 × uint32) = 600
        // header 48
        // total 3448
        MeshHeader header = new(
            Version: MeshHeader.CurrentVersion,
            Flags: MeshFlags.HasColor | MeshFlags.HasNormals,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            VertexCount: 100,
            TriangleCount: 50,
            BboxMin: Vector3.Zero,
            BboxMax: Vector3.One,
            TextureOffset: 0,
            TextureLength: 0);

        Assert.Equal(48 + 100L * 28 + 50L * 12, header.TotalSizeBytes);
    }

    // ─────────────────────── Helpers ───────────────────────

    private static MeshHeader MakeMinimalHeader(uint vertexCount, uint triangleCount) => new(
        Version: MeshHeader.CurrentVersion,
        Flags: MeshFlags.None,
        CoordinateFrame: PointCloudCoordinateFrame.Unspecified,
        VertexCount: vertexCount,
        TriangleCount: triangleCount,
        BboxMin: Vector3.Zero,
        BboxMax: Vector3.Zero,
        TextureOffset: 0,
        TextureLength: 0);
}
