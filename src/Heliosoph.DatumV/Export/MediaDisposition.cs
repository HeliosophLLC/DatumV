namespace Heliosoph.DatumV.Export;

/// <summary>
/// How a sink handles a single column whose <see cref="Model.DataKind"/> is a
/// typed-media kind (Image, Audio, Video, Mesh, PointCloud, Json, Drawing).
/// Resolved per-column at plan time so unsupported combinations fail with a
/// specific message before any rows flow.
/// </summary>
public enum MediaDisposition
{
    /// <summary>
    /// The column is encoded in the format's native byte representation
    /// (Parquet <c>BYTE_ARRAY</c>, HDF5 variable-length byte dataset,
    /// FITS image HDU, JSONL base64 string). The sink writes raw blob
    /// bytes inline alongside scalar columns.
    /// </summary>
    Inline,

    /// <summary>
    /// The column's blob bytes are written to a sibling file in a directory
    /// sink; the table cell holds a relative path. Only available when the
    /// target is a <see cref="ExportTarget.Directory"/>. Not implemented in
    /// this slice — Parquet uses <see cref="Inline"/> only — but reserved on
    /// the contract so format implementations can opt in.
    /// </summary>
    Sidecar,
}
