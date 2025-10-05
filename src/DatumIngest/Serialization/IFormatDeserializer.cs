using DatumIngest.Model;

namespace DatumIngest.Serialization;

/// <summary>
/// Deserializes a file format into a stream of <see cref="RowBatch"/> instances.
/// Implementations are stateless — all per-deserialization state lives in the
/// <see cref="DeserializationContext"/> and the <see cref="FileFormatDescriptor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="DatumIngest.Catalog.ITableProvider"/>, this interface has no
/// <c>GetSchemaAsync</c> or <c>GetCapabilitiesAsync</c>. Schema is inferred
/// internally and embedded in the row data (column names and types). This keeps
/// the contract minimal for format conversion pipelines where the consumer
/// (e.g. the .datum writer) derives schema from the first batch.
/// </para>
/// </remarks>
public interface IFormatDeserializer
{
    /// <summary>
    /// Deserializes the source file into a stream of row batches.
    /// </summary>
    /// <param name="context">
    /// Shared deserialization resources: <see cref="DeserializationContext.Pool"/> for
    /// renting arrays and batches, <see cref="DeserializationContext.Arena"/> for
    /// string storage.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of <see cref="RowBatch"/> instances. The consumer
    /// must return each batch to the pool after processing.</returns>
    IAsyncEnumerable<RowBatch> DeserializeAsync(
        DeserializationContext context,
        CancellationToken cancellationToken = default);
}
