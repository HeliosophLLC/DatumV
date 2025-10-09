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
    /// <param name="context">
    /// Shared serialization resources: <see cref="SerializationContext.Pool"/> for
    /// renting arrays and batches, <see cref="SerializationContext.Arena"/> for
    /// temporary string storage.
    /// </param>
    /// <param name="rows">An async stream of <see cref="RowBatch"/> instances to serialize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SerializeAsync(
        SerializationContext context,
        IAsyncEnumerable<RowBatch> rows,
        CancellationToken cancellationToken = default);
}
