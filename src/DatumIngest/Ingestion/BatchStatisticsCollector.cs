using System.Runtime.CompilerServices;
using DatumIngest.Model;
using DatumIngest.Statistics;

namespace DatumIngest.Ingestion;

/// <summary>
/// A passthrough batch stream wrapper that accumulates per-column statistics
/// on every row without modifying the data. The caller owns the
/// <see cref="StatisticsCollector"/> and can read results after the stream
/// has been fully consumed.
/// </summary>
public sealed class BatchStatisticsCollector
{
    private readonly StatisticsCollector _collector;

    /// <summary>Creates a new statistics collector wrapping the given collector.</summary>
    /// <param name="collector">The collector to accumulate into.</param>
    public BatchStatisticsCollector(StatisticsCollector collector)
    {
        _collector = collector;
    }

    /// <summary>
    /// Iterates the input stream, accumulates statistics on every row,
    /// and yields each batch unchanged.
    /// </summary>
    /// <param name="source">The input batch stream.</param>
    /// <param name="store">Value store for resolving reference-type payloads.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The same batches, in order.</returns>
    public async IAsyncEnumerable<RowBatch> CollectAndPassthrough(
        IAsyncEnumerable<RowBatch> source,
        IValueStore store,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (RowBatch batch in source.WithCancellation(cancellationToken))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                _collector.AddRow(batch[i], store);
            }

            yield return batch;
        }
    }
}
