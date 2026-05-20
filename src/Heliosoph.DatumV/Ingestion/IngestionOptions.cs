namespace Heliosoph.DatumV.Ingestion;

/// <summary>
/// Memory/throughput tradeoff knobs for <see cref="Ingester.IngestAsync(Serialization.FileFormatDescriptor, Serialization.OutputDescriptor, IngestionOptions, System.Threading.CancellationToken)"/>.
/// </summary>
/// <remarks>
/// The defaults are tuned for single-tenant bulk ingest (maximum throughput). For
/// multi-tenant servers that also serve queries concurrently, use
/// <see cref="MultiTenantServer"/> to cap peak working set.
/// </remarks>
public sealed record IngestionOptions
{
    /// <summary>
    /// Hint to deserializers / spill writers about how big a working
    /// arena to budget per row group / batch. Heavy-blob ingestion
    /// (images, long strings) honours this to cap peak working set;
    /// pure-tabular ingestion mostly ignores it. Default: 128 MB.
    /// </summary>
    public int RowGroupByteThreshold { get; init; } = 128 * 1024 * 1024;

    /// <summary>
    /// When <c>true</c>, deserializers / writers serialize per-column work
    /// rather than parallelize it. Lowers peak memory at the cost of wall
    /// time on wide tables.
    /// </summary>
    public bool SerialColumnEncoding { get; init; } = false;

    /// <summary>
    /// Target bytes per deserializer batch. Deserializers that honour this (currently
    /// <c>MediaBagDeserializer</c> for ZIP / TAR archives) flush a batch once its arena
    /// reaches this size. Smaller batches reduce the amount of data in flight ahead of
    /// the writer at the cost of per-batch overhead. Default: 16 MB.
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
