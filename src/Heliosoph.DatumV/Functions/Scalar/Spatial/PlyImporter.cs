using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// Parses a binary little-endian PLY file (the shape <see cref="PlyExporter"/>
/// emits, plus the common subset most third-party PLY producers use) into a
/// <see cref="Heliosoph.DatumV.Model.DataKind.PointCloud"/> blob the engine
/// understands. Inverse of <see cref="PlyExporter"/>, closing the round-trip
/// loop after a Parquet COPY → re-import.
/// </summary>
/// <remarks>
/// <para>
/// Recognised properties (in any order; consumer reads by property name):
/// <c>x</c>, <c>y</c>, <c>z</c> (float32, required);
/// <c>red</c>, <c>green</c>, <c>blue</c> (uchar; optional but all three must
/// be present together);
/// <c>nx</c>, <c>ny</c>, <c>nz</c> (float32; optional, all three together).
/// Any other property on the <c>element vertex</c> line is skipped at parse
/// time (its bytes are stepped over in the binary payload). Non-vertex
/// elements (faces, edges, etc.) after the vertex section are ignored — PLY
/// is a polygon format too, but the PointCloud kind doesn't model
/// connectivity.
/// </para>
/// <para>
/// Coordinate frame: PLY has no on-disk frame tag. We emit
/// <see cref="PointCloudCoordinateFrame.CameraOpenGl"/> (right-handed +Y up)
/// since that's what <see cref="PlyExporter"/> normalises to on export, and
/// what every standard PLY producer (MeshLab, CloudCompare, Open3D's PLY
/// writer) targets. PLY files from a CV-frame source already have y/z flipped
/// by their exporter and re-importing them keeps them in GL-frame internally
/// — a no-op transform at import means the round-trip stays bit-identical
/// for clouds that started in GL and acquires the canonical frame for clouds
/// that started in CV.
/// </para>
/// </remarks>
internal static class PlyImporter
{
    /// <summary>
    /// Parses <paramref name="plyBytes"/> and returns a freshly allocated
    /// <see cref="Heliosoph.DatumV.Model.DataKind.PointCloud"/> blob.
    /// Throws <see cref="InvalidDataException"/> when the PLY header is
    /// malformed, advertises an unsupported format, or the binary payload
    /// is shorter than the header declared element count requires.
    /// </summary>
    public static byte[] Import(ReadOnlySpan<byte> plyBytes)
    {
        ParsedHeader header = ParseHeader(plyBytes, out int payloadStart);

        int pointCount = header.VertexCount;
        bool hasColor = header.HasColor;

        // Build the output blob — header + interleaved per-point payload.
        // Position (12) + optional color (4). Normals are advertised in the
        // PLY but currently dropped: PointCloud Slice A only models
        // HasColor, and serialising HasNormals into a blob this build can't
        // read back would trip MeshHeader.SupportedFlags on the consumer
        // side. Re-enable when PointCloudFlags.HasNormals lands in
        // SupportedFlags.
        int outStride = PointCloudHeader.PositionStrideBytes
            + (hasColor ? PointCloudHeader.ColorStrideBytes : 0);
        long payloadBytes = (long)pointCount * outStride;
        byte[] blob = new byte[PointCloudHeader.SizeBytes + payloadBytes];

        // Compute bbox while iterating — saves a second pass.
        Vector3 bboxMin = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        ReadOnlySpan<byte> payload = plyBytes[payloadStart..];
        if (payload.Length < (long)pointCount * header.PerVertexBytes)
        {
            throw new InvalidDataException(
                $"PLY binary payload is shorter than the header declared " +
                $"({payload.Length} bytes for {pointCount} vertices × {header.PerVertexBytes} bytes).");
        }

        Span<byte> dst = blob.AsSpan(PointCloudHeader.SizeBytes);
        int dstOffset = 0;
        int srcOffset = 0;
        PropertyOffsets offsets = header.Offsets;

        for (int i = 0; i < pointCount; i++)
        {
            float x = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(srcOffset + offsets.X, 4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(srcOffset + offsets.Y, 4));
            float z = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(srcOffset + offsets.Z, 4));

            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(dstOffset + 0, 4), x);
            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(dstOffset + 4, 4), y);
            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(dstOffset + 8, 4), z);
            dstOffset += 12;

            if (x < bboxMin.X) bboxMin.X = x; if (x > bboxMax.X) bboxMax.X = x;
            if (y < bboxMin.Y) bboxMin.Y = y; if (y > bboxMax.Y) bboxMax.Y = y;
            if (z < bboxMin.Z) bboxMin.Z = z; if (z > bboxMax.Z) bboxMax.Z = z;

            if (hasColor)
            {
                dst[dstOffset + 0] = payload[srcOffset + offsets.R];
                dst[dstOffset + 1] = payload[srcOffset + offsets.G];
                dst[dstOffset + 2] = payload[srcOffset + offsets.B];
                dst[dstOffset + 3] = 255; // PLY drops alpha; restore to opaque.
                dstOffset += 4;
            }

            srcOffset += header.PerVertexBytes;
        }

        // Degenerate empty-cloud case — leave bbox at all-zeros so the
        // header reads sensibly rather than carrying ±infinity.
        if (pointCount == 0)
        {
            bboxMin = Vector3.Zero;
            bboxMax = Vector3.Zero;
        }

        PointCloudFlags flags = hasColor ? PointCloudFlags.HasColor : PointCloudFlags.None;
        PointCloudHeader outHeader = new(
            PointCloudHeader.CurrentVersion,
            flags,
            PointCloudCoordinateFrame.CameraOpenGl,
            (uint)pointCount,
            bboxMin,
            bboxMax,
            Width: 0,
            Height: 0);
        outHeader.Write(blob);
        return blob;
    }

    // ──────────────── Header parsing ────────────────

    private readonly record struct ParsedHeader(
        int VertexCount,
        int PerVertexBytes,
        bool HasColor,
        PropertyOffsets Offsets);

    private readonly record struct PropertyOffsets(
        int X, int Y, int Z,
        int R, int G, int B);

    private static ParsedHeader ParseHeader(ReadOnlySpan<byte> blob, out int payloadStart)
    {
        // PLY headers are LF- or CRLF-terminated ASCII. The whole header is
        // upper-bounded by a few hundred bytes for our exporter; ~64 KB is
        // a generous cap for foreign producers.
        const int MaxHeaderBytes = 64 * 1024;
        int scanLimit = System.Math.Min(blob.Length, MaxHeaderBytes);
        int endHeaderEnd = FindEndHeader(blob[..scanLimit]);
        if (endHeaderEnd < 0)
        {
            throw new InvalidDataException(
                "PLY file is missing the 'end_header' marker in the first 64 KB; not a valid PLY.");
        }
        payloadStart = endHeaderEnd;

        string headerText = System.Text.Encoding.ASCII.GetString(blob[..endHeaderEnd]);
        string[] lines = headerText.Split('\n');

        bool sawMagic = false;
        bool sawFormat = false;
        int vertexCount = -1;
        int perVertexBytes = 0;
        bool hasColor = false;
        int posXOffset = -1, posYOffset = -1, posZOffset = -1;
        int rOffset = -1, gOffset = -1, bOffset = -1;
        bool inVertexElement = false;
        int cursor = 0; // byte offset within one vertex record

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("comment ", StringComparison.Ordinal)) continue;
            if (line == "end_header") break;

            if (!sawMagic)
            {
                if (line != "ply")
                {
                    throw new InvalidDataException(
                        $"PLY file must begin with the magic line 'ply'; got '{line}'.");
                }
                sawMagic = true;
                continue;
            }

            if (line.StartsWith("format ", StringComparison.Ordinal))
            {
                if (line != "format binary_little_endian 1.0")
                {
                    throw new InvalidDataException(
                        $"Only 'format binary_little_endian 1.0' PLY files are supported; got '{line}'. " +
                        "Re-export from MeshLab / CloudCompare with the binary-little-endian option.");
                }
                sawFormat = true;
                continue;
            }

            if (line.StartsWith("element ", StringComparison.Ordinal))
            {
                string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length != 3)
                {
                    throw new InvalidDataException($"Malformed PLY element line: '{line}'.");
                }
                if (tokens[1] == "vertex")
                {
                    if (vertexCount >= 0)
                    {
                        throw new InvalidDataException("PLY file declares more than one 'vertex' element.");
                    }
                    if (!int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out vertexCount)
                        || vertexCount < 0)
                    {
                        throw new InvalidDataException($"Invalid vertex count in PLY element line: '{line}'.");
                    }
                    inVertexElement = true;
                }
                else
                {
                    // Past the vertex element — done collecting offsets. We
                    // intentionally don't consume face/edge data; PointCloud
                    // doesn't model topology.
                    inVertexElement = false;
                }
                continue;
            }

            if (line.StartsWith("property ", StringComparison.Ordinal) && inVertexElement)
            {
                (int byteWidth, string name) = ParseProperty(line);
                switch (name)
                {
                    case "x": posXOffset = cursor; break;
                    case "y": posYOffset = cursor; break;
                    case "z": posZOffset = cursor; break;
                    case "red": rOffset = cursor; break;
                    case "green": gOffset = cursor; break;
                    case "blue": bOffset = cursor; break;
                    // Other properties (nx/ny/nz, alpha, intensity, etc.)
                    // are skipped — their byte width still advances the
                    // cursor so the per-vertex stride is correct.
                }
                cursor += byteWidth;
                continue;
            }
        }

        if (!sawFormat)
        {
            throw new InvalidDataException("PLY file is missing the 'format' header line.");
        }
        if (vertexCount < 0)
        {
            throw new InvalidDataException("PLY file is missing the 'element vertex' header line.");
        }
        if (posXOffset < 0 || posYOffset < 0 || posZOffset < 0)
        {
            throw new InvalidDataException(
                "PLY vertex element is missing one or more of 'property float x|y|z'.");
        }

        bool allColorPresent = rOffset >= 0 && gOffset >= 0 && bOffset >= 0;
        bool someColorPresent = rOffset >= 0 || gOffset >= 0 || bOffset >= 0;
        if (someColorPresent && !allColorPresent)
        {
            throw new InvalidDataException(
                "PLY vertex element declares some but not all of 'property uchar red|green|blue'. " +
                "Mixed color presence is not supported.");
        }
        hasColor = allColorPresent;

        perVertexBytes = cursor;
        return new ParsedHeader(
            vertexCount,
            perVertexBytes,
            hasColor,
            new PropertyOffsets(posXOffset, posYOffset, posZOffset, rOffset, gOffset, bOffset));
    }

    /// <summary>
    /// Parses one <c>property &lt;type&gt; &lt;name&gt;</c> line, returning
    /// the byte width of that property and its name. Supports the scalar
    /// types PlyExporter and standard producers emit; throws on unknown
    /// types or PLY-list properties (which only appear on faces, never
    /// vertices in our world).
    /// </summary>
    private static (int ByteWidth, string Name) ParseProperty(string line)
    {
        string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3 || tokens[0] != "property")
        {
            throw new InvalidDataException($"Malformed PLY property line: '{line}'.");
        }
        if (tokens[1] == "list")
        {
            throw new InvalidDataException(
                $"PLY list properties on vertex elements are not supported: '{line}'.");
        }
        int width = tokens[1] switch
        {
            "char" or "int8" => 1,
            "uchar" or "uint8" => 1,
            "short" or "int16" => 2,
            "ushort" or "uint16" => 2,
            "int" or "int32" => 4,
            "uint" or "uint32" => 4,
            "float" or "float32" => 4,
            "double" or "float64" => 8,
            _ => throw new InvalidDataException(
                $"Unsupported PLY property type '{tokens[1]}' in line '{line}'."),
        };
        return (width, tokens[2]);
    }

    /// <summary>
    /// Returns the byte index immediately after the LF that terminates
    /// the <c>end_header</c> line, or −1 when the marker is absent.
    /// </summary>
    private static int FindEndHeader(ReadOnlySpan<byte> blob)
    {
        ReadOnlySpan<byte> marker = "end_header\n"u8;
        for (int i = 0; i + marker.Length <= blob.Length; i++)
        {
            if (blob.Slice(i, marker.Length).SequenceEqual(marker))
            {
                return i + marker.Length;
            }
        }
        return -1;
    }
}
