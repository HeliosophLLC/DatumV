using System.Diagnostics;
using DatumIngest.DatumFile;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Statistics;

namespace DatumIngest.Ingestion;

/// <summary>
/// Ingests source files into the <c>.datum</c> column-store format.
/// Each call to <c>IngestAsync</c> converts a single source file into a single
/// <c>.datum</c> file, collecting schema, statistics, and a sample preview along the way.
/// </summary>
/// <remarks>
/// The core ingester handles the write pass only. Two-pass ingestion (full-file
/// scan + narrowed-type write) is composed by the caller: scan with the appropriate
/// format's scanner, build a pre-configured deserializer from the scan result, and
/// pass it to the deserializer-accepting <c>IngestAsync</c> overload. This keeps
/// the core project independent of format-specific scanner types (e.g.
/// <c>CsvScanResult</c>) that live in the serialization project.
/// </remarks>
public class Ingester(
    FormatRegistry formatRegistry,
    Pool pool)
{
    /// <summary>
    /// Ingests a source file into a <c>.datum</c> file using sample-based type inference.
    /// Suitable for single-pass use; for strict-types two-pass ingestion, build a
    /// pre-configured deserializer via the source format's scanner and call the
    /// deserializer-accepting overload.
    /// </summary>
    public Task<IngestionResult> IngestAsync(
        FileFormatDescriptor source,
        OutputDescriptor destination,
        CancellationToken cancellationToken = default)
    {
        IFormatDeserializer deserializer = formatRegistry.CreateDeserializer(source);
        return IngestAsync(source, destination, deserializer, scanMetrics: null, cancellationToken);
    }

    /// <summary>
    /// Ingests a source file using a caller-provided deserializer. Used by two-pass
    /// flows to feed a pre-configured deserializer (e.g. a <c>CsvDeserializer</c>
    /// initialised from a <c>CsvScanResult</c>) so the write pass skips sample-based
    /// inference.
    /// </summary>
    /// <param name="source">Descriptor for the source file.</param>
    /// <param name="destination">Descriptor for the output <c>.datum</c> file.</param>
    /// <param name="deserializer">Pre-built deserializer to use for the write pass.</param>
    /// <param name="scanMetrics">Metrics captured for the optional preceding scan pass, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IngestionResult> IngestAsync(
        FileFormatDescriptor source,
        OutputDescriptor destination,
        IFormatDeserializer deserializer,
        PassMetrics? scanMetrics,
        CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        // Per-ingestion state — scoped to this request.
        SchemaDetector schemaDetector = new();
        StatisticsCollector statisticsCollector = new();

        await using Stream outputStream = await destination.OpenAsync(cancellationToken);
        using DatumFileWriter writer = new(outputStream);

        SerializationContext sourceContext = new(pool);

        long rowCount = 0;
        long batchCount = 0;
        long totalArenaBytes = 0;

        await foreach (RowBatch batch in deserializer.DeserializeAsync(sourceContext, cancellationToken))
        {
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
            batchCount++;
            totalArenaBytes += batch.Arena.BytesWritten;
            pool.ReturnRowBatch(batch);

            if (handle.RequiresFlush)
            {
                statisticsCollector.FlushRowGroup(writer.WriterArena);
                writer.FlushRowGroup();
            }
        }

        if (!schemaDetector.IsDetected)
        {
            writer.Initialize(DatumFileSchema.FromSchema(new Schema([])));
        }

        statisticsCollector.FlushRowGroup(writer.WriterArena);
        long bytesWritten = writer.Finalize();

        sw.Stop();

        PassMetrics ingestMetrics = new(
            RowCount: rowCount,
            BatchCount: batchCount,
            BytesRead: 0,
            ArenaBytesWritten: totalArenaBytes,
            Elapsed: sw.Elapsed);

        return new IngestionResult(
            OutputPath: destination.FilePath,
            RowCount: rowCount,
            BytesWritten: bytesWritten,
            Schema: schemaDetector.IsDetected ? schemaDetector.Schema : new Schema([]),
            Statistics: statisticsCollector.GetStatistics(),
            Sample: null,
            ScanPass: scanMetrics,
            IngestPass: ingestMetrics);
    }
}
