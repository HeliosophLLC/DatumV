using DatumIngest.DatumFile;

namespace DatumIngest.Ingestion;

/// <summary>
/// Memory/throughput tradeoff knobs for <see cref="Ingester.IngestAsync(Serialization.FileFormatDescriptor, Serialization.OutputDescriptor, IngestionOptions, System.Threading.CancellationToken)"/>.
/// </summary>
/// <remarks>
/// The defaults are tuned for single-tenant bulk ingest (maximum throughput). For
/// multi-tenant servers that also serve queries concurrently, use
/// <see cref="MultiTenantServer"/> to cap peak working set at the cost of roughly
/// 20% ingest wall time.
/// </remarks>
public sealed record IngestionOptions
{
    /// <summary>
    /// Soft cap on writer-arena bytes before a row group is flushed, even if the row
    /// count hasn't reached <see cref="DatumFileConstants.DefaultRowGroupSize"/>. Lower
    /// values reduce peak memory during heavy-blob ingestion (images, large binary
    /// payloads) at the cost of more row groups, slightly larger footer metadata, and
    /// reduced per-page compression ratio.
    /// </summary>
    public int RowGroupByteThreshold { get; init; } = DatumFileConstants.RowGroupArenaByteThreshold;

    /// <summary>
    /// When <c>true</c>, the writer encodes one column at a time during row group flush
    /// instead of using <c>Parallel.For</c>. On wide tables (dozens of columns) parallel
    /// encoding multiplies the per-column raw buffer by the degree of parallelism;
    /// serial encoding caps peak at one column's worth of buffer.
    /// </summary>
    public bool SerialColumnEncoding { get; init; } = false;

    /// <summary>
    /// Target bytes per deserializer batch. Deserializers that honour this (currently
    /// <c>ZipDeserializer</c>) flush a batch once its arena reaches this size. Smaller
    /// batches reduce the amount of data in flight ahead of the writer at the cost of
    /// per-batch overhead. Default: 16 MB.
    /// </summary>
    public int BatchByteTarget { get; init; } = 16 * 1024 * 1024;

    /// <summary>Default options: maximum throughput, highest peak memory.</summary>
    public static IngestionOptions Default { get; } = new();

    /// <summary>
    /// Preset tuned for multi-tenant servers where concurrent queries share the process
    /// memory budget. Caps peak working set at roughly 256 MB for heavy-blob ingest
    /// (vs. ~1.5 GB under <see cref="Default"/> for image archives) in exchange for
    /// ~20% slower ingest wall time.
    /// </summary>
    public static IngestionOptions MultiTenantServer { get; } = new()
    {
        RowGroupByteThreshold = 32 * 1024 * 1024,
        SerialColumnEncoding = true,
        BatchByteTarget = 4 * 1024 * 1024,
    };
}
