using System.Buffers.Binary;
using System.Numerics;

namespace Heliosoph.DatumV.Model.Spatial;

/// <summary>
/// Per-point feature flags stored in <see cref="PointCloudHeader.Flags"/>.
/// Determines which attribute groups appear in the per-point payload and in
/// what order (position → color → normal → intensity). Unset bits are reserved
/// for forward compatibility — the wire layout treats reserved bits as zero
/// and Slice A readers reject point clouds with any reserved bit set.
/// </summary>
[Flags]
public enum PointCloudFlags : byte
{
    /// <summary>Position only (3 × float32 per point).</summary>
    None = 0,

    /// <summary>Per-point RGBA color (4 × uint8) appended after position.</summary>
    HasColor = 1 << 0,

    /// <summary>Per-point unit normal (3 × float32) appended after color.</summary>
    HasNormals = 1 << 1,

    /// <summary>Per-point scalar intensity (1 × float32) appended after normal.</summary>
    HasIntensity = 1 << 2,
}

/// <summary>
/// Coordinate-system convention for the points in a <see cref="DataKind.PointCloud"/>
/// value. Tagged in the header so producers can self-describe and consumers can
/// transform to their preferred frame without out-of-band agreement.
/// </summary>
public enum PointCloudCoordinateFrame : byte
{
    /// <summary>
    /// No frame committed. Consumers should treat positions as raw and not
    /// apply any basis transform. Useful while a producer is still being
    /// designed; not recommended for persisted data.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Right-handed: +x right, +y up, −z forward. Matches OpenGL / Three.js /
    /// glTF camera-space conventions. Renderers in this family upload positions
    /// without a basis swap.
    /// </summary>
    CameraOpenGl = 1,

    /// <summary>
    /// Right-handed: +x right, +y down, +z forward. Matches OpenCV / most depth
    /// cameras (RealSense, Kinect) / classical RGB-D unprojection. To render in
    /// an OpenGL-family pipeline, negate y and negate z.
    /// </summary>
    CameraOpenCv = 2,
}

/// <summary>
/// Fixed 40-byte header preceding the interleaved per-point payload in a
/// <see cref="DataKind.PointCloud"/> blob. The layout is version-tagged so
/// future revisions can change the per-point stride or add header fields
/// without breaking older readers (they refuse to deserialize, never
/// mis-interpret).
/// </summary>
/// <remarks>
/// <para>
/// Wire layout (little-endian throughout):
/// <code>
/// offset  size  field
///   0      1   Version           (= 1 for this format)
///   1      1   Flags             (PointCloudFlags bitmask)
///   2      1   CoordinateFrame   (PointCloudCoordinateFrame enum)
///   3      1   reserved          (must be 0; pad to u32 align)
///   4      4   PointCount        (uint32)
///   8     12   BboxMin           (3 × float32)
///  20     12   BboxMax           (3 × float32)
///  32      4   Width             (uint32; 0 = unorganized)
///  36      4   Height            (uint32; 0 = unorganized)
/// total = 40
/// </code>
/// </para>
/// <para>
/// Per-point payload follows the header. Stride is derived from <see cref="Flags"/> —
/// position (12 bytes) + color (4 if HasColor) + normal (12 if HasNormals) +
/// intensity (4 if HasIntensity). Attribute groups appear in that fixed order.
/// </para>
/// <para>
/// A point cloud is "organized" when <see cref="Width"/> × <see cref="Height"/>
/// equals <see cref="PointCount"/>; consumers may then interpret points as a
/// row-major (u, v) grid and derive implicit topology (e.g. two triangles per
/// grid cell). Producers that have no grid structure (LiDAR scans, decimated
/// clouds, photogrammetry) leave both dimensions at 0.
/// </para>
/// </remarks>
public readonly record struct PointCloudHeader(
    byte Version,
    PointCloudFlags Flags,
    PointCloudCoordinateFrame CoordinateFrame,
    uint PointCount,
    Vector3 BboxMin,
    Vector3 BboxMax,
    uint Width,
    uint Height)
{
    /// <summary>Format version emitted by this build. Bumped only on breaking layout changes.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>Size of the fixed header in bytes.</summary>
    public const int SizeBytes = 40;

    /// <summary>Stride of the position group in the per-point payload (3 × float32).</summary>
    public const int PositionStrideBytes = 12;

    /// <summary>Stride of the color group when <see cref="PointCloudFlags.HasColor"/> is set (4 × uint8).</summary>
    public const int ColorStrideBytes = 4;

    /// <summary>Stride of the normal group when <see cref="PointCloudFlags.HasNormals"/> is set (3 × float32).</summary>
    public const int NormalStrideBytes = 12;

    /// <summary>Stride of the intensity group when <see cref="PointCloudFlags.HasIntensity"/> is set (1 × float32).</summary>
    public const int IntensityStrideBytes = 4;

    /// <summary>Bitmask of all flag bits supported by this build. Slice A: position + color only.</summary>
    public const PointCloudFlags SupportedFlags = PointCloudFlags.HasColor;

    /// <summary>True when the header advertises per-point RGBA color.</summary>
    public bool HasColor => (Flags & PointCloudFlags.HasColor) != 0;

    /// <summary>True when the header advertises per-point unit normals.</summary>
    public bool HasNormals => (Flags & PointCloudFlags.HasNormals) != 0;

    /// <summary>True when the header advertises per-point scalar intensity.</summary>
    public bool HasIntensity => (Flags & PointCloudFlags.HasIntensity) != 0;

    /// <summary>
    /// True when <see cref="Width"/> × <see cref="Height"/> equals
    /// <see cref="PointCount"/> and both dimensions are non-zero. Consumers
    /// may then interpret the points as a row-major grid.
    /// </summary>
    public bool IsOrganized => Width > 0 && Height > 0 && (long)Width * Height == PointCount;

    /// <summary>
    /// Per-point byte stride derived from <see cref="Flags"/>. Sum of every enabled
    /// attribute group's stride; minimum 12 (position only).
    /// </summary>
    public int PointStrideBytes =>
        PositionStrideBytes
        + (HasColor ? ColorStrideBytes : 0)
        + (HasNormals ? NormalStrideBytes : 0)
        + (HasIntensity ? IntensityStrideBytes : 0);

    /// <summary>Total blob size including header and all per-point payload bytes.</summary>
    public long TotalSizeBytes => SizeBytes + (long)PointCount * PointStrideBytes;

    /// <summary>
    /// Deserializes a <see cref="PointCloudHeader"/> from the leading bytes of
    /// a point-cloud blob. Throws <see cref="InvalidDataException"/> when the
    /// version byte does not match this build.
    /// </summary>
    public static PointCloudHeader Read(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < SizeBytes)
        {
            throw new ArgumentException(
                $"PointCloud blob must be at least {SizeBytes} bytes (header); got {blob.Length}.",
                nameof(blob));
        }

        byte version = blob[0];
        if (version != CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported PointCloud header version {version}; this build emits / supports v{CurrentVersion}.");
        }

        PointCloudFlags flags = (PointCloudFlags)blob[1];
        PointCloudCoordinateFrame frame = (PointCloudCoordinateFrame)blob[2];
        // blob[3] is reserved padding; ignored on read.

        uint pointCount = BinaryPrimitives.ReadUInt32LittleEndian(blob[4..8]);
        Vector3 bboxMin = ReadVector3LittleEndian(blob[8..20]);
        Vector3 bboxMax = ReadVector3LittleEndian(blob[20..32]);
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(blob[32..36]);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(blob[36..40]);

        return new PointCloudHeader(version, flags, frame, pointCount, bboxMin, bboxMax, width, height);
    }

    /// <summary>
    /// Serializes this header into the leading <see cref="SizeBytes"/> bytes of
    /// <paramref name="blob"/>. The destination span must be at least
    /// <see cref="SizeBytes"/> long.
    /// </summary>
    public void Write(Span<byte> blob)
    {
        if (blob.Length < SizeBytes)
        {
            throw new ArgumentException(
                $"PointCloud header destination must be at least {SizeBytes} bytes; got {blob.Length}.",
                nameof(blob));
        }

        blob[0] = Version;
        blob[1] = (byte)Flags;
        blob[2] = (byte)CoordinateFrame;
        blob[3] = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(blob[4..8], PointCount);
        WriteVector3LittleEndian(blob[8..20], BboxMin);
        WriteVector3LittleEndian(blob[20..32], BboxMax);
        BinaryPrimitives.WriteUInt32LittleEndian(blob[32..36], Width);
        BinaryPrimitives.WriteUInt32LittleEndian(blob[36..40], Height);
    }

    private static Vector3 ReadVector3LittleEndian(ReadOnlySpan<byte> span)
    {
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(span[0..4]),
            BinaryPrimitives.ReadSingleLittleEndian(span[4..8]),
            BinaryPrimitives.ReadSingleLittleEndian(span[8..12]));
    }

    private static void WriteVector3LittleEndian(Span<byte> span, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(span[0..4], v.X);
        BinaryPrimitives.WriteSingleLittleEndian(span[4..8], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(span[8..12], v.Z);
    }
}
