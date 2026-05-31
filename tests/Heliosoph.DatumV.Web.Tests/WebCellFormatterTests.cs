using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Web.Execution;

namespace Heliosoph.DatumV.Web.Tests;

/// <summary>
/// Covers <see cref="WebCellFormatter"/>'s generic UInt8[] handling.
/// </summary>
/// <remarks>
/// The formatter used to render every UInt8[] column as an image-media
/// cell on the assumption that the bytes were a PNG / JPEG / WebP. After
/// the typed-media Parquet export started emitting UInt8[] columns full
/// of <c>.glb</c>, PLY, and audio bytes, that assumption silently broke
/// — those rows appeared as broken-image placeholders in the results
/// pane. The formatter now only emits a media cell when the bytes
/// actually match a known media format's magic; everything else falls
/// through to a text summary that names the binary format (when
/// recognisable) and points at the matching <c>_from_X</c> importer.
/// </remarks>
public sealed class WebCellFormatterTests
{
    [Fact]
    public void UInt8Array_PngMagic_RendersAsImageMedia()
    {
        using Arena arena = new();
        SidecarRegistry registry = new();

        byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD];
        DataValue value = DataValue.FromByteArray(pngHeader, arena);

        JsonCell cell = WebCellFormatter.Format(value, arena, registry);

        Assert.Equal("media", cell.Kind);
        Assert.Equal("image/png", cell.Mime);
        Assert.NotNull(cell.DataB64);
    }

    [Fact]
    public void UInt8Array_GltfMagic_RendersAsTextWithImporterHint()
    {
        using Arena arena = new();
        SidecarRegistry registry = new();

        // glTF 2.0 .glb starts with the 4-byte ASCII "glTF" magic.
        byte[] glbHeader = [0x67, 0x6C, 0x54, 0x46, 0x02, 0x00, 0x00, 0x00];
        DataValue value = DataValue.FromByteArray(glbHeader, arena);

        JsonCell cell = WebCellFormatter.Format(value, arena, registry);

        Assert.Equal("text", cell.Kind);
        Assert.NotNull(cell.Text);
        Assert.Contains("glTF", cell.Text);
        Assert.Contains("mesh_from_gltf", cell.Text);
    }

    [Fact]
    public void UInt8Array_PlyMagic_RendersAsTextWithImporterHint()
    {
        using Arena arena = new();
        SidecarRegistry registry = new();

        // PLY starts with "ply" followed by an LF (or CRLF).
        byte[] plyHeader = [0x70, 0x6C, 0x79, 0x0A, 0x66, 0x6F, 0x72];
        DataValue value = DataValue.FromByteArray(plyHeader, arena);

        JsonCell cell = WebCellFormatter.Format(value, arena, registry);

        Assert.Equal("text", cell.Kind);
        Assert.NotNull(cell.Text);
        Assert.Contains("PLY", cell.Text);
        Assert.Contains("pointcloud_from_ply", cell.Text);
    }

    [Fact]
    public void UInt8Array_UnknownBytes_RendersAsGenericBinaryText()
    {
        using Arena arena = new();
        SidecarRegistry registry = new();

        // Random bytes that match no known media or interchange magic. The
        // cell should NOT be classified as media — the front-end's <img>
        // tag would produce a broken-image placeholder otherwise.
        byte[] random = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77];
        DataValue value = DataValue.FromByteArray(random, arena);

        JsonCell cell = WebCellFormatter.Format(value, arena, registry);

        Assert.Equal("text", cell.Kind);
        Assert.NotNull(cell.Text);
        Assert.Contains("binary", cell.Text);
        Assert.Contains("8 bytes", cell.Text);
    }

    [Fact]
    public void TypedImageKind_UnknownMagic_StillRendersAsMedia()
    {
        using Arena arena = new();
        SidecarRegistry registry = new();

        // A DataKind.Image value whose magic bytes don't match any image
        // format we recognise. The producer's intent was an image, so we
        // still emit a media cell — only the generic UInt8[] case falls
        // through to text. Preserves backward compatibility for the typed
        // Image path; only the speculative UInt8[]-is-image heuristic
        // changed.
        byte[] unknown = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77];
        DataValue value = DataValue.FromImage(unknown, arena);

        JsonCell cell = WebCellFormatter.Format(value, arena, registry);

        Assert.Equal("media", cell.Kind);
        Assert.Equal("application/octet-stream", cell.Mime);
    }
}
