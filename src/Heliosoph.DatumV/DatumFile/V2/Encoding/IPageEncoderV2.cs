using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.DatumFile.V2.Encoding;

/// <summary>
/// Per-column page encoder for the v2 columnar format. One instance per
/// (writer, column) pair; the writer reuses the encoder across pages by
/// calling <see cref="Append"/> until <see cref="IsFull"/> is true, then
/// <see cref="Flush"/> to obtain the page bytes, then continues with the
/// next page.
/// </summary>
internal interface IPageEncoderV2
{
    /// <summary>
    /// Whether the encoder has accumulated <see cref="DatumFormatV2.DefaultPageSize"/>
    /// rows (or whatever the configured page size is) and must be flushed
    /// before further appends.
    /// </summary>
    bool IsFull { get; }

    /// <summary>Rows currently buffered for the in-progress page.</summary>
    int RowCount { get; }

    /// <summary>
    /// Appends one row's value. <paramref name="store"/> resolves
    /// arena-backed payloads (Strings, byte arrays, etc.) and
    /// is unused by encoders whose kinds are always inline. The
    /// <c>VariableSlotPageEncoderV2</c> (when implemented) additionally
    /// uses <paramref name="sidecar"/> to spill non-inline payloads.
    /// </summary>
    void Append(DataValue value, IValueStore? store, IBlobSink? sidecar);

    /// <summary>
    /// Builds the on-disk page bytes and zone map for the buffered rows,
    /// and resets the encoder for the next page. Caller is responsible for
    /// stamping the resulting <see cref="EncodedPageV2"/> into a
    /// <see cref="PageDescriptorV2"/> at the chosen file offset.
    /// </summary>
    EncodedPageV2 Flush();
}
