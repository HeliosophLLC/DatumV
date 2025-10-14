using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Writer-side context passed to every column encoder during page encoding.
/// Encoders that do not externalize blobs may safely ignore this value.
/// </summary>
public sealed class DatumEncoderContext
{
    /// <summary>A shared singleton with no file context, for callers that do not externalize blobs.</summary>
    internal static readonly DatumEncoderContext Empty = new();

    /// <summary>
    /// Absolute path to the <c>.datum</c> file being written.
    /// Used by <see cref="BinaryColumnEncoder"/> to construct sidecar blob paths of the form
    /// <c>{DatumFilePath}.datum_blobs/{columnName}/{rowGroupIndex}_{blobIndex}.dat</c>.
    /// </summary>
    public string DatumFilePath { get; init; } = string.Empty;

    /// <summary>Zero-based index of the row group currently being encoded.</summary>
    public int RowGroupIndex { get; init; }
}
