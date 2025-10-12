using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Reader-side context needed for decoding externalized column pages.
/// Passed to <see cref="DatumColumnDecoder.Decode(byte[], DatumEncoding, DatumCompression, int, int, DatumColumnDescriptor, DatumDecoderContext)"/> so decoders that store sidecar
/// blob paths can resolve the absolute path of each blob file.
/// </summary>
public sealed class DatumDecoderContext
{
    /// <summary>
    /// Absolute path to the <c>.datum</c> file being read.
    /// Used to resolve relative sidecar blob paths produced by <c>BinaryColumnEncoder</c>
    /// when externalization was triggered.
    /// </summary>
    public string DatumFilePath { get; init; } = string.Empty;

    /// <summary>
    /// Optional value store for string and binary payloads. When set, decoders use this
    /// store, enabling Arena-backed
    /// string storage without ambient state.
    /// </summary>
    public IValueStore? Store { get; init; }

    /// <summary>
    /// A context with no file path, suitable for in-memory decode scenarios
    /// that contain no externalized blobs.
    /// </summary>
    public static DatumDecoderContext Empty { get; } = new();
}
