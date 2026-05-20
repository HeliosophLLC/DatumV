using Heliosoph.DatumV.Ingestion;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Serialization;

/// <summary>
/// Deserializes a file format into a stream of <see cref="RowBatch"/> instances.
/// </summary>
public interface IFormatDeserializer
{
    /// <summary>
    /// Deserializes the source file into a stream of row batches.
    /// </summary>
    /// <param name="context">Context for this deserialization.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of <see cref="RowBatch"/> instances. The consumer
    /// must return each batch to the pool after processing.</returns>
    IAsyncEnumerable<RowBatch> DeserializeAsync(
        SerializationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Metrics captured for any pre-enumeration scan pass performed by this
    /// deserializer (e.g. the full-file type scan that the CSV deserializer
    /// runs for CSV sources in strict mode).
    /// </summary>
    /// <remarks>
    /// Populated after the first call to <see cref="DeserializeAsync"/> completes any
    /// pre-scan work and before the first <see cref="RowBatch"/> is yielded. Schema-driven
    /// formats (Parquet, HDF5, etc.) return <c>null</c> because no separate inference pass
    /// is needed — their types are already strict.
    /// </remarks>
    PassMetrics? ScanMetrics => null;
}