using System.Diagnostics;
using DatumIngest.DatumFile;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
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

        // The sidecar is created lazily — no .datum-blob file appears on disk unless a
        // deserializer actually appends to it. Pure-tabular ingests therefore pay no
        // on-disk cost. The writer learns its fingerprint and rewrites image-column
        // descriptors with the SidecarBlobs flag during Initialize.
        string sidecarPath = SidecarPathFor(destination.FilePath);
        using SidecarWriteStore sidecar = new(sidecarPath);
        writer.AttachSidecar(sidecar);

        SerializationContext sourceContext = new(pool, options.BatchByteTarget, lboStore: sidecar);

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

        // Capture the sidecar's state before disposing — Dispose nulls the stream
        // and would flip WasMaterialized back to false. Then close the sidecar so
        // the file is no longer held for write, and we can mmap it for the sample
        // preview thumbnail pass below.
        bool sidecarMaterialized = sidecar.WasMaterialized;
        ulong sidecarFingerprint = sidecar.Fingerprint;
        sidecar.Dispose();

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

        // Sidecar-backed image cells in the reservoir hold (storeId, offset, length)
        // placeholders; the sample collector resolves them via a tiny ad-hoc registry
        // wrapping the just-finalised .datum-blob. The deserializer stamped storeId=0
        // onto its values (default for FromImageInSidecar), so the registry only
        // needs the one source registered (also gets storeId=0). Bounded by reservoir
        // size (~25 rows), so this is a tiny mmap'd post-pass.
        SidecarRegistry? sampleRegistry = null;
        SidecarReadStore? sidecarReader = null;
        try
        {
            if (sidecarMaterialized)
            {
                sidecarReader = new SidecarReadStore(sidecarPath, sidecarFingerprint);
                sampleRegistry = new SidecarRegistry();
                sampleRegistry.Register(sidecarReader);
            }
            SamplePreview sample = sampleCollector.Build(finalSchema, sampleRegistry);

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
        finally
        {
            sidecarReader?.Dispose();
        }
    }

    /// <summary>
    /// Returns the companion sidecar path for a given <c>.datum</c> output path.
    /// Strips the <c>.datum</c> extension if present and appends <c>.datum-blob</c>;
    /// otherwise appends <c>.datum-blob</c> to the full path. Result lives next to
    /// the <c>.datum</c> file so a single move/copy covers both files.
    /// </summary>
    private static string SidecarPathFor(string datumPath)
    {
        return Path.ChangeExtension(datumPath, SidecarConstants.FileExtension);
    }

    // ──────────────────── V2 format ingest path ────────────────────

    /// <summary>
    /// V2 format counterpart to <see cref="IngestAsync(FileFormatDescriptor, OutputDescriptor, IngestionOptions, CancellationToken)"/>.
    /// Uses <see cref="DatumFileWriterV2"/> to produce an uncompressed
    /// columnar-with-sidecar file per <c>project_datum_format_v2.md</c>. While
    /// v1 still ships, this path is the debug entry-point for verifying
    /// end-to-end v2 file creation against real source files (CSV / JSON /
    /// Parquet / HDF5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Differences from the v1 path: no row-group concept (pages auto-flush
    /// at 1024 rows), no <see cref="WriteHandle"/> (the writer streams
    /// directly to disk), and stats / sample collection resolves through
    /// each batch's arena (not a consolidated writer arena). Stats
    /// accumulators that retain DataValues across batches will see them go
    /// stale once the source batch is returned to the pool — a known gap
    /// for stats-correctness in this path that will be addressed when v1
    /// is dropped (Phase 5).
    /// </para>
    /// </remarks>
    public Task<IngestionResult> IngestV2Async(
        FileFormatDescriptor source,
        OutputDescriptor destination,
        CancellationToken cancellationToken = default)
        => IngestV2Async(source, destination, IngestionOptions.Default, cancellationToken);

    /// <summary>V2 ingest with explicit options.</summary>
    public Task<IngestionResult> IngestV2Async(
        FileFormatDescriptor source,
        OutputDescriptor destination,
        IngestionOptions options,
        CancellationToken cancellationToken = default)
    {
        IFormatDeserializer deserializer = formatRegistry.CreateDeserializer(source);
        return IngestV2Async(source, destination, deserializer, options, cancellationToken);
    }

    /// <summary>V2 ingest with caller-provided deserializer.</summary>
    public Task<IngestionResult> IngestV2Async(
        FileFormatDescriptor source,
        OutputDescriptor destination,
        IFormatDeserializer deserializer,
        CancellationToken cancellationToken = default)
        => IngestV2Async(source, destination, deserializer, IngestionOptions.Default, cancellationToken);

    /// <summary>V2 ingest with caller-provided deserializer and options.</summary>
    public async Task<IngestionResult> IngestV2Async(
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

        // Sidecar created lazily per the v1 pattern. We construct it
        // up-front so its Fingerprint can be captured (needed for the
        // sample-preview read pass) and so the deserializer can target it
        // as the IBlobSink for image-bearing rows.
        string sidecarPath = SidecarPathFor(destination.FilePath);
        SidecarWriteStore sidecar = new(sidecarPath);
        ulong sidecarFingerprint = sidecar.Fingerprint;
        bool sidecarMaterialized;

        DatumFileWriterV2 writer = new(outputStream, sidecar);

        SerializationContext sourceContext = new(pool, options.BatchByteTarget, lboStore: sidecar);

        // Long-lived stats arena — accumulators that retain DataValues
        // (e.g. SpaceSavingSketch's top-K samples) need their offsets to
        // stay resolvable past the source batch's pool.ReturnRowBatch.
        // We stabilize each value into this arena before handing it to
        // the collector; the arena lives until after GetStatistics().
        Arena statsArena = new();
        DataValue[]? stableScratch = null;

        long rowCount = 0;
        long batchCount = 0;
        long totalArenaBytes = 0;
        bool initialized = false;

        try
        {
            await foreach (RowBatch batch in deserializer.DeserializeAsync(sourceContext, cancellationToken))
            {
                if (!schemaDetector.IsDetected)
                {
                    schemaDetector.Detect(batch);

                    if (schemaDetector.IsDetected)
                    {
                        ColumnDescriptorV2[] descriptors = ToV2Descriptors(schemaDetector.Schema);
                        writer.Initialize(descriptors);
                        initialized = true;
                    }
                }

                // Stats: stabilize per-row into statsArena, then accumulate
                // against statsArena. Accumulators that retain values get
                // arena-bound offsets that resolve for the rest of the run.
                int colCount = batch.ColumnLookup.Count;
                if (colCount > 0)
                {
                    if (stableScratch is null || stableScratch.Length < colCount)
                    {
                        stableScratch = new DataValue[colCount];
                    }
                    for (int rowI = 0; rowI < batch.Count; rowI++)
                    {
                        Row row = batch[rowI];
                        for (int colI = 0; colI < colCount; colI++)
                        {
                            stableScratch[colI] = DataValueRetention.Stabilize(
                                row[colI], batch.Arena, statsArena);
                        }
                        statisticsCollector.AddRow(new Row(batch.ColumnLookup, stableScratch), statsArena);
                    }
                }

                // SamplePreviewCollector eagerly materializes to managed
                // object?[] inside Consider — no arena retention required.
                sampleCollector.Consider(batch, batch.Arena);

                writer.WriteRowBatch(batch);

                rowCount += batch.Count;
                batchCount++;
                totalArenaBytes += batch.Arena.BytesWritten;
                pool.ReturnRowBatch(batch);
            }

            if (!initialized)
            {
                writer.Initialize(ToV2Descriptors(new Schema([])));
            }

            writer.FinalizeWriter();
        }
        finally
        {
            writer.Dispose();
            sidecarMaterialized = sidecar.WasMaterialized;
            sidecar.Dispose();
        }

        // outputStream is disposed by the await using; the file is now
        // closed and we can stat it for the byte count.
        long bytesWritten;
        try
        {
            bytesWritten = new FileInfo(destination.FilePath).Length;
        }
        catch (FileNotFoundException)
        {
            // OutputDescriptor may not be a regular file path (e.g. memory
            // stream destination); fall back to zero.
            bytesWritten = 0;
        }

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

        SidecarRegistry? sampleRegistry = null;
        SidecarReadStore? sidecarReader = null;
        try
        {
            if (sidecarMaterialized)
            {
                sidecarReader = new SidecarReadStore(sidecarPath, sidecarFingerprint);
                sampleRegistry = new SidecarRegistry();
                sampleRegistry.Register(sidecarReader);
            }
            SamplePreview sample = sampleCollector.Build(finalSchema, sampleRegistry);

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
        finally
        {
            sidecarReader?.Dispose();
        }
    }

    /// <summary>
    /// Converts a <see cref="Schema"/> to the v2 column-descriptor list.
    /// Encoder kind is picked by <see cref="ColumnDescriptorV2.EncoderFor"/>;
    /// nullability comes from <see cref="ColumnInfo.Nullable"/>; array kind
    /// is inferred from <see cref="ColumnInfo.ArrayElementKind"/>. Fixed
    /// shape isn't carried on <see cref="ColumnInfo"/> today, so it is
    /// always <see langword="null"/> here — Vector / Matrix / Tensor
    /// columns will get their shape populated by a later schema-enrichment
    /// pass when one lands.
    /// </summary>
    private static ColumnDescriptorV2[] ToV2Descriptors(Schema schema)
    {
        ColumnDescriptorV2[] descriptors = new ColumnDescriptorV2[schema.Columns.Count];
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            ColumnInfo col = schema.Columns[i];
            bool isArray = col.Kind == DataKind.Array || col.ArrayElementKind is not null;
            descriptors[i] = new ColumnDescriptorV2(
                Name: col.Name,
                Kind: col.Kind,
                Encoder: ColumnDescriptorV2.EncoderFor(col.Kind, isArray),
                IsNullable: col.Nullable,
                IsArray: isArray);
        }
        return descriptors;
    }
}
