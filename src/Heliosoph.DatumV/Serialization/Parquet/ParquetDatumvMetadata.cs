using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Serialization.Parquet;

/// <summary>
/// Convention for the per-column-chunk key/value metadata
/// <c>ParquetExportSink</c> attaches to typed-media columns and
/// <c>OpenParquetFunction</c> reads back on import. External tools (DuckDB,
/// pandas, MeshLab, Spark, …) see opaque KV pairs and ignore them; Heliosoph.DatumV's
/// own reader uses them to re-route each tagged column to its original
/// <see cref="DataKind"/> without the user having to wrap with the matching
/// <c>_from_X</c> importer in SQL.
/// </summary>
/// <remarks>
/// <para>
/// Three keys are written together as a block on every annotated column:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="KindKey"/> — the typed <see cref="DataKind"/> name
///   (<c>"Mesh"</c>, <c>"PointCloud"</c>, <c>"Image"</c>, <c>"Audio"</c>, <c>"Video"</c>).</description></item>
///   <item><description><see cref="FormatKey"/> — the on-disk byte format
///   (<c>"gltf"</c>, <c>"ply"</c>, or <c>"passthrough"</c> when the bytes are
///   already in the universal interchange shape the typed kind uses).</description></item>
///   <item><description><see cref="VersionKey"/> — the convention version
///   (<see cref="CurrentVersion"/>). Bumped only on a breaking change to the
///   key/value semantics so older readers refuse rather than mis-interpret.</description></item>
/// </list>
/// </remarks>
internal static class ParquetDatumvMetadata
{
    /// <summary>Key carrying the typed <see cref="DataKind"/> name.</summary>
    public const string KindKey = "datumv.kind";

    /// <summary>Key carrying the on-disk byte format identifier.</summary>
    public const string FormatKey = "datumv.format";

    /// <summary>Key carrying the convention version.</summary>
    public const string VersionKey = "datumv.version";

    /// <summary>Convention version written by this build.</summary>
    public const string CurrentVersion = "1";

    /// <summary>
    /// Format value for kinds whose bytes already are in the universal
    /// interchange shape — Image (PNG/JPEG/WebP), Audio (WAV/MP3/FLAC),
    /// Video (MP4/WebM). Reading back is a kind retag, no decode.
    /// </summary>
    public const string FormatPassthrough = "passthrough";

    /// <summary>Format value for Mesh columns serialised as binary glTF 2.0 (.glb).</summary>
    public const string FormatGltf = "gltf";

    /// <summary>Format value for PointCloud columns serialised as binary PLY.</summary>
    public const string FormatPly = "ply";

    /// <summary>
    /// Format value for Json columns serialised as UTF-8 JSON text. Pandas /
    /// DuckDB / Spark / Polars all read the column as a plain string out of
    /// the box; the tag tells <c>open_parquet</c> to re-encode the text back
    /// to CBOR so the engine's <see cref="DataKind.Json"/> contract holds.
    /// </summary>
    public const string FormatJsonText = "text";
}
