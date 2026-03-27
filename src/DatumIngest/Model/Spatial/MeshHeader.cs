using System.Buffers.Binary;
using System.Numerics;

namespace DatumIngest.Model.Spatial;

/// <summary>
/// Per-vertex feature flags stored in <see cref="MeshHeader.Flags"/>, plus
/// the embedded-texture flag stored alongside them. Determines which
/// attribute groups appear in the per-vertex payload (in fixed order:
/// position → color → normal → uv) and whether the blob carries an
/// encoded texture image after the triangle indices. Unset bits are
/// reserved; the format treats them as zero and Phase 1 readers reject
/// any flag bit outside <see cref="MeshHeader.SupportedFlags"/>.
/// </summary>
[Flags]
public enum MeshFlags : byte
{
    /// <summary>Position-only vertices (3 × float32 each); no per-vertex attributes.</summary>
    None = 0,

    /// <summary>Per-vertex RGBA color (4 × uint8) appended after position.</summary>
    HasColor = 1 << 0,

    /// <summary>Per-vertex unit normal (3 × float32) appended after color.</summary>
    HasNormals = 1 << 1,

    /// <summary>Per-vertex UV texture coordinate (2 × float32) appended after normal.</summary>
    HasUVs = 1 << 2,

    /// <summary>An encoded texture image (PNG / JPEG) is embedded at the blob tail.</summary>
    HasTexture = 1 << 3,
}

/// <summary>
/// Fixed 48-byte header preceding the vertex payload + index array + optional
/// embedded texture in a <see cref="DataKind.Mesh"/> blob. Version-tagged so
/// future revisions can change the vertex stride, add header fields, or
/// introduce new attribute groups without breaking older readers (they refuse
/// to deserialize, never mis-interpret).
/// </summary>
/// <remarks>
/// <para>
/// Wire layout (little-endian throughout):
/// <code>
/// offset  size  field
///   0      1   Version            (= 1 for this format)
///   1      1   Flags              (MeshFlags bitmask)
///   2      1   CoordinateFrame    (PointCloudCoordinateFrame enum; mesh inherits the cloud's frame)
///   3      1   reserved           (must be 0; pad to u32 align)
///   4      4   VertexCount        (uint32)
///   8      4   TriangleCount      (uint32)
///  12     12   BboxMin            (3 × float32)
///  24     12   BboxMax            (3 × float32)
///  36      4   TextureOffset      (uint32; absolute byte offset of embedded texture, 0 when HasTexture clear)
///  40      4   TextureLength      (uint32; byte length of embedded texture, 0 when HasTexture clear)
///  44      4   reserved           (must be 0; pad to 48)
/// total = 48
/// </code>
/// </para>
/// <para>
/// Vertex payload follows the header. Stride is derived from <see cref="Flags"/> —
/// position (12 bytes) + color (4 if HasColor) + normal (12 if HasNormals) +
/// uv (8 if HasUVs). Attribute groups appear in that fixed order.
/// </para>
/// <para>
/// Triangle indices follow the vertex payload: 3 × uint32 per triangle, in
/// CCW winding order. Each index addresses a vertex in the preceding payload
/// (0 ≤ idx &lt; VertexCount).
/// </para>
/// <para>
/// Optional encoded texture image (PNG / JPEG) lives at <c>TextureOffset</c>
/// for <c>TextureLength</c> bytes when <see cref="MeshFlags.HasTexture"/> is set.
/// Phase 2 only; Phase 1 readers reject the flag.
/// </para>
/// <para>
/// Coordinate frame uses <see cref="PointCloudCoordinateFrame"/> — a mesh
/// constructed from an organized PointCloud inherits the cloud's frame, and
/// ONNX mesh-from-image models in Phase 2 tag whatever frame they emit.
/// </para>
/// </remarks>
public readonly record struct MeshHeader(
    byte Version,
    MeshFlags Flags,
    PointCloudCoordinateFrame CoordinateFrame,
    uint VertexCount,
    uint TriangleCount,
    Vector3 BboxMin,
    Vector3 BboxMax,
    uint TextureOffset,
    uint TextureLength)
{
    /// <summary>Format version emitted by this build. Bumped only on breaking layout changes.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>Size of the fixed header in bytes.</summary>
    public const int SizeBytes = 48;

    /// <summary>Stride of the position group in the per-vertex payload (3 × float32).</summary>
    public const int PositionStrideBytes = 12;

    /// <summary>Stride of the color group when <see cref="MeshFlags.HasColor"/> is set (4 × uint8).</summary>
    public const int ColorStrideBytes = 4;

    /// <summary>Stride of the normal group when <see cref="MeshFlags.HasNormals"/> is set (3 × float32).</summary>
    public const int NormalStrideBytes = 12;

    /// <summary>Stride of the UV group when <see cref="MeshFlags.HasUVs"/> is set (2 × float32).</summary>
    public const int UvStrideBytes = 8;

    /// <summary>Stride of one triangle's indices (3 × uint32).</summary>
    public const int IndexStrideBytes = 12;

    /// <summary>
    /// Bitmask of flag bits supported by this build. Phase 1 emits and reads
    /// position + color + normals; the UV and texture bits are reserved for
    /// Phase 2 (ONNX-mesh-model ingestion + glTF texture embedding).
    /// </summary>
    public const MeshFlags SupportedFlags = MeshFlags.HasColor | MeshFlags.HasNormals;

    /// <summary>True when the header advertises per-vertex RGBA color.</summary>
    public bool HasColor => (Flags & MeshFlags.HasColor) != 0;

    /// <summary>True when the header advertises per-vertex unit normals.</summary>
    public bool HasNormals => (Flags & MeshFlags.HasNormals) != 0;

    /// <summary>True when the header advertises per-vertex UV texture coordinates.</summary>
    public bool HasUVs => (Flags & MeshFlags.HasUVs) != 0;

    /// <summary>True when the header advertises an embedded texture image at the blob tail.</summary>
    public bool HasTexture => (Flags & MeshFlags.HasTexture) != 0;

    /// <summary>
    /// Per-vertex byte stride derived from <see cref="Flags"/>. Sum of every
    /// enabled attribute group's stride; minimum 12 (position only).
    /// </summary>
    public int VertexStrideBytes =>
        PositionStrideBytes
        + (HasColor ? ColorStrideBytes : 0)
        + (HasNormals ? NormalStrideBytes : 0)
        + (HasUVs ? UvStrideBytes : 0);

    /// <summary>
    /// Total blob size including header, all per-vertex payload bytes, all
    /// triangle-index bytes, and the embedded texture when present.
    /// </summary>
    public long TotalSizeBytes =>
        SizeBytes
        + (long)VertexCount * VertexStrideBytes
        + (long)TriangleCount * IndexStrideBytes
        + (HasTexture ? TextureLength : 0);

    /// <summary>
    /// Deserializes a <see cref="MeshHeader"/> from the leading bytes of a
    /// mesh blob. Throws <see cref="InvalidDataException"/> when the version
    /// byte does not match this build, or when the flags carry bits outside
    /// <see cref="SupportedFlags"/> (Phase 1 actively rejects Phase 2 flags
    /// rather than silently dropping them so consumers never observe a Mesh
    /// value whose declared shape doesn't match what the reader produces).
    /// </summary>
    public static MeshHeader Read(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < SizeBytes)
        {
            throw new ArgumentException(
                $"Mesh blob must be at least {SizeBytes} bytes (header); got {blob.Length}.",
                nameof(blob));
        }

        byte version = blob[0];
        if (version != CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Mesh header version {version}; this build emits / supports v{CurrentVersion}.");
        }

        MeshFlags flags = (MeshFlags)blob[1];
        MeshFlags unsupported = flags & ~SupportedFlags;
        if (unsupported != MeshFlags.None)
        {
            throw new InvalidDataException(
                $"Mesh header declares unsupported flag bits {unsupported}; this build supports {SupportedFlags}.");
        }

        PointCloudCoordinateFrame frame = (PointCloudCoordinateFrame)blob[2];
        // blob[3] is reserved padding; ignored on read.

        uint vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(blob[4..8]);
        uint triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(blob[8..12]);
        Vector3 bboxMin = ReadVector3LittleEndian(blob[12..24]);
        Vector3 bboxMax = ReadVector3LittleEndian(blob[24..36]);
        uint textureOffset = BinaryPrimitives.ReadUInt32LittleEndian(blob[36..40]);
        uint textureLength = BinaryPrimitives.ReadUInt32LittleEndian(blob[40..44]);
        // blob[44..48] is reserved padding; ignored on read.

        return new MeshHeader(
            version, flags, frame, vertexCount, triangleCount,
            bboxMin, bboxMax, textureOffset, textureLength);
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
                $"Mesh header destination must be at least {SizeBytes} bytes; got {blob.Length}.",
                nameof(blob));
        }

        blob[0] = Version;
        blob[1] = (byte)Flags;
        blob[2] = (byte)CoordinateFrame;
        blob[3] = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(blob[4..8], VertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(blob[8..12], TriangleCount);
        WriteVector3LittleEndian(blob[12..24], BboxMin);
        WriteVector3LittleEndian(blob[24..36], BboxMax);
        BinaryPrimitives.WriteUInt32LittleEndian(blob[36..40], TextureOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(blob[40..44], TextureLength);
        blob[44] = 0;
        blob[45] = 0;
        blob[46] = 0;
        blob[47] = 0;
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
