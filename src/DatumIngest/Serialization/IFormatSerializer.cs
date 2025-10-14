using DatumIngest.Model;

namespace DatumIngest.Serialization;

/// <summary>
/// Serializes a stream of <see cref="RowBatch"/> instances into a file format.
/// Implementations receive an <see cref="OutputDescriptor"/> in their constructor
/// and write to the stream it provides.
/// </summary>
public interface IFormatSerializer
{
    /// <summary>
    /// Serializes the given row batches to the output stream.
    /// </summary>
    /// <param name="context">The serialization context.</param>
    /// <param name="rows">An async stream of <see cref="RowBatch"/> instances to serialize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SerializeAsync(
        SerializationContext context,
        IAsyncEnumerable<RowBatch> rows,
        CancellationToken cancellationToken = default);
}
