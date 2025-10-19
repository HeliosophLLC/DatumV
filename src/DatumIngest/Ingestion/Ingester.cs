using System.Diagnostics;
using DatumIngest.DatumFile;
using DatumIngest.Ingestion.Sampling;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Statistics;

namespace DatumIngest.Ingestion;

/// <summary>
/// Ingests source files into the <c>.datum</c> column-store format. Each call to
/// <c>IngestAsync</c> converts a single source file into a single <c>.datum</c> file,
/// collecting schema, statistics, and a sample preview along the way.
/// </summary>
/// <remarks>
/// <para>
/// The ingester is format-agnostic: it reads whatever <see cref="IFormatDeserializer"/>
/// the <see cref="FormatRegistry"/> hands it and writes the result. Format-specific
/// preprocessing (e.g. the CSV full-file type scan) happens inside the deserializer
/// for that format — the CSV deserializer scans the file on first enumeration by
/// default so types are strict without the caller needing to opt in. Schema-driven
/// formats (Parquet, HDF5) skip scanning because their types are already authoritative.
/// </para>
/// <para>
/// Any scan metrics produced by a deserializer are surfaced through
/// <see cref="IFormatDeserializer.ScanMetrics"/> and included on the returned
/// <see cref="IngestionResult"/> so downstream consumers (dashboards, audit logs,
/// regression tests) see the full pass-level cost breakdown without the ingester
/// having to know which formats do what.
/// </para>
/// </remarks>
public class Ingester(
    FormatRegistry formatRegistry,
    Pool pool)
{
    /// <summary>
    /// Ingests a source file into a <c>.datum</c> file. The source format's
    /// deserializer is resolved via <see cref="FormatRegistry"/> and may perform
    /// format-specific preprocessing (e.g. a full-file type scan for CSV).
    /// </summary>
    public Task<IngestionResult> IngestAsync(
        FileFormatDescriptor source,
        OutputDescriptor destination,
        CancellationToken cancellationToken = default)
        => IngestAsync(source, destination, IngestionOptions.Default, cancellationToken);

    /// <summary>
    /// Ingests a source file with caller-specified memory/throughput options. Use
    /// <see cref="IngestionOptions.MultiTenantServer"/> in processes that share memory
    /// with concurrent query workloads.
    /// </summary>
    public Task<IngestionResult> IngestAsync(
        FileFormatDescriptor source,
        OutputDescriptor destination,
        IngestionOptions options,
        CancellationToken cancellationToken = default)
    {
        IFormatDeserializer deserializer = formatRegistry.CreateDeserializer(source);
        return IngestAsync(source, destination, deserializer, options, cancellationToken);
    }

    /// <summary>
    /// Ingests a source file using a caller-provided deserializer. Useful when the
    /// caller wants to pre-configure the deserializer (e.g. opt out of strict types
    /// or inject a pre-computed scan result).
    /// </summary>
    public Task<IngestionResult> IngestAsync(
        FileFormatDescriptor source,
        OutputDescriptor destination,
        IFormatDeserializer deserializer,
        CancellationToken cancellationToken = default)
        => IngestAsync(source, destination, deserializer, IngestionOptions.Default, cancellationToken);

    /// <summary>
    /// Ingests a source file using a caller-provided deserializer and memory options.
    /// </summary>
    public async Task<IngestionResult> IngestAsync(
        FileFormatDescriptor source,
        OutputDescriptor destination,
        IFormatDeserializer deserializer,
        IngestionOptions options,
        CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        SchemaDetector schemaDetector = new();
        StatisticsCollector statisticsCollector = new();
        SamplePreviewCollector sampleCollector = new();

        await using Stream outputStream = await destination.OpenAsync(cancellationToken);
        using DatumFileWriter writer = new(outputStream);
        writer.SetMemoryBudget(options.RowGroupByteThreshold, options.SerialColumnEncoding);

        SerializationContext sourceContext = new(pool, options.BatchByteTarget);

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

            // WriteRowBatch first so stats and samples can resolve offsets through
            // the writer's page slice — the slice survives the batch returning to
            // the pool, whereas the batch's own arena is reset on return.
            WriteHandle handle = writer.WriteRowBatch(batch);
            IValueStore resolutionStore = (IValueStore?)handle.PageStore ?? batch.Arena;
            statisticsCollector.Collect(batch, handle.PageStore);
            sampleCollector.Consider(batch, resolutionStore);

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

        Schema finalSchema = schemaDetector.IsDetected ? schemaDetector.Schema : new Schema([]);
        IReadOnlyDictionary<string, ColumnStatistics> statistics = statisticsCollector.GetStatistics();

        Dictionary<string, DataKind> columnKinds = new(finalSchema.Columns.Count);
        foreach (ColumnInfo column in finalSchema.Columns)
        {
            columnKinds[column.Name] = column.Kind;
        }

        QueryResultsManifest manifest = ManifestBuilder.Build(statistics, columnKinds, rowCount);
        SamplePreview sample = sampleCollector.Build(finalSchema);

        return new IngestionResult(
            OutputPath: destination.FilePath,
            RowCount: rowCount,
            BytesWritten: bytesWritten,
            Schema: finalSchema,
            Manifest: manifest,
            Sample: sample,
            ScanPass: deserializer.ScanMetrics,
            IngestPass: ingestMetrics);
    }
}
