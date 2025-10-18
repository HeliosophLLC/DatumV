using DatumIngest.DatumFile;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Statistics;

namespace DatumIngest.Ingestion;

/// <summary>
/// Ingests source files into the <c>.datum</c> column-store format.
/// Each call to <see cref="IngestAsync"/> converts a single source file into a single
/// <c>.datum</c> file, collecting schema, statistics, and a sample preview along the way.
/// </summary>
public class Ingester(
    FormatRegistry formatRegistry,
    Pool pool)
{
    /// <summary>
    /// Ingests a source file into a <c>.datum</c> file.
    /// </summary>
    /// <param name="source">Descriptor for the source file (path, options, format detection).</param>
    /// <param name="destination">Descriptor for the output <c>.datum</c> file.</param>
    /// <param name="progress">Optional callback for progress reporting.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ingestion result containing schema, statistics, sample, and output metadata.</returns>
    public async Task<IngestionResult> IngestAsync(
        FileFormatDescriptor source,
        OutputDescriptor destination,
        Action<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Per-ingestion state — scoped to this request.
        SchemaDetector schemaDetector = new();
        StatisticsCollector statisticsCollector = new();

        // Open the destination once; DatumFileWriter owns the stream lifetime from here.
        await using Stream outputStream = await destination.OpenAsync(cancellationToken);
        using DatumFileWriter writer = new(outputStream);

        SerializationContext sourceContext = new(pool);
        IFormatDeserializer deserializer = formatRegistry.CreateDeserializer(source);

        long rowCount = 0;

        await foreach (RowBatch batch in deserializer.DeserializeAsync(sourceContext, cancellationToken))
        {
            // First non-empty batch infers the schema and initializes the writer.
            if (!schemaDetector.IsDetected)
            {
                schemaDetector.Detect(batch);

                if (schemaDetector.IsDetected)
                {
                    writer.Initialize(DatumFileSchema.FromSchema(schemaDetector.Schema));
                }
            }

            // WriteRowBatch first so stats can resolve offsets through the writer's
            // page slice — the slice survives the batch returning to the pool.
            WriteHandle handle = writer.WriteRowBatch(batch);
            statisticsCollector.Collect(batch, handle.PageStore);

            rowCount += batch.Count;
            pool.ReturnRowBatch(batch);

            if (handle.RequiresFlush)
            {
                // Merge stats' per-row-group state before the writer resets its arena.
                statisticsCollector.FlushRowGroup(writer.WriterArena);
                writer.FlushRowGroup();
            }
        }

        // Edge case: empty source (no non-empty batches) — initialize with an empty schema.
        if (!schemaDetector.IsDetected)
        {
            writer.Initialize(DatumFileSchema.FromSchema(new Schema([])));
        }

        // Flush any pending stats state before Finalize internally flushes the last
        // row group and disposes the writer's arena.
        statisticsCollector.FlushRowGroup(writer.WriterArena);
        long bytesWritten = writer.Finalize();

        return new IngestionResult(
            OutputPath: destination.FilePath,
            RowCount: rowCount,
            BytesWritten: bytesWritten,
            Schema: schemaDetector.IsDetected ? schemaDetector.Schema : new Schema([]),
            Statistics: statisticsCollector.GetStatistics(),
            Sample: null);
    }
}
