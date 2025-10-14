using DatumIngest.Model;

namespace DatumIngest.Serialization;

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
}