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
    Pool pool,
    //SchemaDetector schemaDetector,
    StatisticsCollector statisticsCollector)
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
        // TODO: Phase 5 — wire the pipeline:
        // 1. (DONE) Detect format → create IFormatDeserializer
        // 2. (DONE) Create deserializer Arena + serializer Arena
        // 3. Deserialize → SchemaDetector → (DONE) StatisticsCollector + SampleCollector → DatumSerializer
        // 4. Dispose both Arenas
        // 5. Return IngestionResult

        await foreach (RowBatch rowBatch in formatRegistry
            .CreateDeserializer(source)
            .DeserializeAsync(new SerializationContext(pool), cancellationToken: cancellationToken))
        {
            //schemaDetector.DetectAndPassthrough()
            statisticsCollector.Collect(rowBatch);
        }

        
        throw new NotImplementedException("Ingester.IngestAsync is not yet implemented.");
    }
}
