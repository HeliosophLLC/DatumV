using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Writer-side context passed to every column encoder during page encoding.
/// Carries the writer's arena and the per-page layout so reference-type encoders
/// can resolve arena-backed <see cref="DataValue"/> offsets via
/// <see cref="Arena.Slice(int, int)"/>.
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

    /// <summary>
    /// The writer's arena. Holds verbatim byte copies of every incoming batch's arena
    /// data, laid out as sequential pages. Reference-type encoders iterate
    /// <see cref="Pages"/> and slice this arena per page to resolve DataValue offsets.
    /// </summary>
    public Arena Store { get; init; } = new();

    /// <summary>
    /// Per-page layout of the column buffers being encoded. One entry per source
    /// <see cref="RowBatch"/> that contributed rows to the current row group.
    /// Scalar encoders may ignore this; reference-type encoders iterate pages and
    /// call <c>Store.Slice(page.ArenaBase, page.ArenaLength)</c> per page.
    /// </summary>
    internal IReadOnlyList<PageSpan> Pages { get; init; } = Array.Empty<PageSpan>();
}
