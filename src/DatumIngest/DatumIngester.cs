using DatumIngest.Catalog;
using DatumIngest.Diagnostics;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Output.Writers;
using DatumIngest.Statistics;

namespace DatumIngest;

/// <summary>
/// High-level entry point for converting source data files into the <c>.datum</c>
/// columnar format with column statistics, and for building indexes from existing
/// <c>.datum</c> files.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IngestAsync(string, Action{IngestionProgress}?, CancellationToken)"/> converts any format that
/// <see cref="TableCatalog"/> recognises (CSV, TSV, JSON, JSON Lines, Parquet, HDF5,
/// ZIP, IDX, or <c>.datum</c>) into <c>.datum</c> streams with a <see cref="SourceManifest"/>
/// containing per-column statistics. It does <em>not</em> build indexes.
/// </para>
/// <para>
/// <see cref="BuildIndexAsync(string, DatumIndexerOptions?, Action{IndexingProgress}?, CancellationToken)"/> reads
/// an existing <c>.datum</c> file and builds a <c>.datum-index</c> containing bloom filters
/// and sorted value indexes. This is a separate pass so that ingestion and indexing can be
/// performed independently.
/// </para>
/// <para>
/// Multi-table sources (for example a root-object JSON file with several array properties)
/// produce one <c>.datum</c> stream per discovered table.
/// Use <see cref="DatumIngestionResult.Tables"/> to access them.
/// </para>
/// </remarks>
public static class DatumIngester
{
    // ──────────────────── Ingestion ────────────────────

    /// <summary>
    /// Ingests a source file from disk in a single streaming pass, producing a
    /// <c>.datum</c> stream, schema, and manifest with column statistics.
    /// </summary>
    /// <param name="filePath">Absolute path to the source file.</param>
    /// <param name="progress">
    /// Optional progress callback. When provided, receives <see cref="IngestionProgress"/>
    /// snapshots synchronously at every 5% completion boundary and at 100% when the table finishes.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="DatumIngestionResult"/> containing the <c>.datum</c> stream,
    /// schema JSON, and manifest JSON. Does not include indexes.
    /// </returns>
    public static Task<DatumIngestionResult> IngestAsync(
        string filePath,
        Action<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return IngestCoreAsync(
            baseTableName: FileFormatDetector.DeriveTableName(filePath),
            filePath: filePath,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Ingests a source file from an in-memory stream in a single streaming pass.
    /// The stream is written to a temporary file for format detection and provider
    /// access, then deleted on completion.
    /// </summary>
    /// <param name="fileName">
    /// The original file name (with extension) used for format detection and as the
    /// logical table name. For example: <c>"survey.csv"</c>, <c>"embeddings.parquet"</c>.
    /// </param>
    /// <param name="source">Readable stream containing the source file bytes.</param>
    /// <param name="progress">
    /// Optional progress callback. When provided, receives <see cref="IngestionProgress"/>
    /// snapshots synchronously at every 5% completion boundary and at 100% when the table finishes.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<DatumIngestionResult> IngestAsync(
        string fileName,
        Stream source,
        Action<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(),
            $"datum_ingest_{Guid.NewGuid():N}{Path.GetExtension(fileName)}");

        try
        {
            await using (FileStream tempFile = File.Create(tempPath))
            {
                await source.CopyToAsync(tempFile, cancellationToken).ConfigureAwait(false);
            }

            return await IngestCoreAsync(
                baseTableName: FileFormatDetector.DeriveTableName(fileName),
                filePath: tempPath,
                progress: progress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    // ──────────────────── Indexing ────────────────────

    /// <summary>
    /// Builds a source index from an existing <c>.datum</c> file on disk.
    /// </summary>
    /// <param name="datumFilePath">Absolute path to the <c>.datum</c> file.</param>
    /// <param name="options">Optional indexing options. Defaults are used when <c>null</c>.</param>
    /// <param name="progress">
    /// Optional progress callback. When provided, receives <see cref="IndexingProgress"/>
    /// snapshots synchronously at every 5% completion boundary and at 100% when the table finishes.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="DatumIndexResult"/> containing the <c>.datum-index</c> stream
    /// and in-memory <see cref="SourceIndexSet"/>.
    /// </returns>
    public static Task<DatumIndexResult> BuildIndexAsync(
        string datumFilePath,
        DatumIndexerOptions? options = null,
        Action<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return BuildIndexCoreAsync(
            baseTableName: FileFormatDetector.DeriveTableName(datumFilePath),
            filePath: datumFilePath,
            options: options ?? DatumIndexerOptions.Default,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Builds a source index from an in-memory <c>.datum</c> stream.
    /// The stream is written to a temporary file for provider access, then deleted
    /// on completion.
    /// </summary>
    /// <param name="fileName">
    /// The logical file name (with <c>.datum</c> extension) used for table registration.
    /// </param>
    /// <param name="datumSource">Readable stream containing the <c>.datum</c> file bytes.</param>
    /// <param name="options">Optional indexing options. Defaults are used when <c>null</c>.</param>
    /// <param name="progress">
    /// Optional progress callback. When provided, receives <see cref="IndexingProgress"/>
    /// snapshots synchronously at every 5% completion boundary and at 100% when the table finishes.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<DatumIndexResult> BuildIndexAsync(
        string fileName,
        Stream datumSource,
        DatumIndexerOptions? options = null,
        Action<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(),
            $"datum_index_{Guid.NewGuid():N}{Path.GetExtension(fileName)}");

        try
        {
            await using (FileStream tempFile = File.Create(tempPath))
            {
                await datumSource.CopyToAsync(tempFile, cancellationToken).ConfigureAwait(false);
            }

            return await BuildIndexCoreAsync(
                baseTableName: FileFormatDetector.DeriveTableName(fileName),
                filePath: tempPath,
                options: options ?? DatumIndexerOptions.Default,
                progress: progress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    // ──────────────────── Ingestion core ────────────────────

    private static async Task<DatumIngestionResult> IngestCoreAsync(
        string baseTableName,
        string filePath,
        Action<IngestionProgress>? progress,
        CancellationToken cancellationToken)
    {
        TableCatalog catalog = new();
        await catalog.RegisterAsync(baseTableName, filePath, cancellationToken).ConfigureAwait(false);

        SourceFingerprint fingerprint;
        await using (FileStream sourceStream = File.OpenRead(filePath))
        {
            fingerprint = await SourceFingerprint.ComputeAsync(sourceStream, cancellationToken)
                .ConfigureAwait(false);
        }

        Dictionary<string, DatumIngestionTableResult> tables = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Schema> schemas = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, QueryResultsManifest> manifests = new(StringComparer.OrdinalIgnoreCase);

        foreach (string tableName in catalog.TableNames)
        {
            TableDescriptor descriptor = catalog.Resolve(tableName);
            ITableProvider provider = catalog.CreateProvider(descriptor);
            DatumIngestionTableResult tableResult = await IngestTableAsync(
                descriptor, provider, progress, cancellationToken).ConfigureAwait(false);

            tables[tableName] = tableResult;
            schemas[tableName] = tableResult.Schema;
            manifests[tableName] = tableResult.Manifest.Tables[tableName];
        }

        SourceSchema sourceSchema = new() { Tables = schemas };
        SourceManifest sourceManifest = new() { Tables = manifests };

        string schemaJson = SchemaSerializer.Serialize(sourceSchema);
        string manifestJson = ManifestSerializer.Serialize(sourceManifest);

        Dictionary<string, SamplePreview> samples = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string tableName, DatumIngestionTableResult tableResult) in tables)
        {
            if (tableResult.SamplePreview is not null)
            {
                samples[tableName] = tableResult.SamplePreview;
            }
        }

        return new DatumIngestionResult
        {
            Fingerprint = fingerprint,
            Tables = tables,
            SourceSchema = sourceSchema,
            SourceManifest = sourceManifest,
            SchemaJson = schemaJson,
            ManifestJson = manifestJson,
            Samples = samples,
        };
    }

    private static async Task<DatumIngestionTableResult> IngestTableAsync(
        TableDescriptor descriptor,
        ITableProvider provider,
        Action<IngestionProgress>? progress,
        CancellationToken cancellationToken)
    {
        Schema schema = await provider.GetSchemaAsync(descriptor, cancellationToken).ConfigureAwait(false);
        StatisticsCollector statisticsCollector = new();
        MemoryStream datumStream = new();
        FusedDatumPipelineWriter datumWriter = new(datumStream, indexBuilder: null, statisticsCollector);

        await datumWriter.InitializeAsync(schema, cancellationToken).ConfigureAwait(false);

        Dictionary<string, DataKind> columnKinds = new(StringComparer.OrdinalIgnoreCase);
        foreach (ColumnInfo column in schema.Columns)
        {
            columnKinds[column.Name] = column.Kind;
        }

        long? totalRows = null;
        int lastReportedPercent = -1;

        if (progress is not null)
        {
            ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(descriptor, cancellationToken)
                .ConfigureAwait(false);
            totalRows = capabilities.EstimatedRowCount;
        }

        SamplePreviewCollector sampleCollector = new();

        long rowCount = 0;
        await foreach (Row row in provider.OpenAsync(descriptor, requiredColumns: null, cancellationToken)
            .ConfigureAwait(false))
        {
            datumWriter.WriteRow(row);
            sampleCollector.Consider(row);
            rowCount++;

            if (progress is not null && totalRows is > 0)
            {
                int currentPercent = (int)Math.Min(100, rowCount * 100 / totalRows.Value);
                if (currentPercent >= lastReportedPercent + 5)
                {
                    lastReportedPercent = currentPercent;
                    progress(new IngestionProgress(
                        descriptor.Name, rowCount, totalRows, currentPercent));
                }
            }
        }

        if (progress is not null && lastReportedPercent < 100)
        {
            progress(new IngestionProgress(
                descriptor.Name, rowCount, totalRows, 100));
        }

        await datumWriter.FinalizeAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, ColumnStatistics> statistics = datumWriter.Statistics
            ?? throw new InvalidOperationException("The datum writer did not produce statistics.");
        QueryResultsManifest queryManifest = ManifestBuilder.Build(statistics, columnKinds, rowCount);
        SourceManifest manifest = SourceManifest.Create(descriptor.Name, queryManifest);
        string schemaJson = SchemaSerializer.Serialize(descriptor.Name, schema);
        string manifestJson = ManifestSerializer.Serialize(manifest);

        await datumWriter.DisposeAsync().ConfigureAwait(false);

        datumStream.Position = 0;

        SamplePreview samplePreview = sampleCollector.Build(schema);

        return new DatumIngestionTableResult
        {
            TableName = descriptor.Name,
            FileName = $"{descriptor.Name}.datum",
            ManifestFileName = $"{descriptor.Name}.datum-manifest",
            Schema = schema,
            Manifest = manifest,
            DatumStream = datumStream,
            SchemaJson = schemaJson,
            ManifestJson = manifestJson,
            RowCount = rowCount,
            FeatureCount = queryManifest.Features.Count,
            SamplePreview = samplePreview,
        };
    }

    // ──────────────────── Indexing core ────────────────────

    private static async Task<DatumIndexResult> BuildIndexCoreAsync(
        string baseTableName,
        string filePath,
        DatumIndexerOptions options,
        Action<IndexingProgress>? progress,
        CancellationToken cancellationToken)
    {
        TableCatalog catalog = new();
        await catalog.RegisterAsync(baseTableName, filePath, cancellationToken).ConfigureAwait(false);

        SourceFingerprint fingerprint;
        await using (FileStream sourceStream = File.OpenRead(filePath))
        {
            fingerprint = await SourceFingerprint.ComputeAsync(sourceStream, cancellationToken)
                .ConfigureAwait(false);
        }

        Dictionary<string, DatumIndexTableResult> tables = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SourceIndex> indexes = new(StringComparer.OrdinalIgnoreCase);

        foreach (string tableName in catalog.TableNames)
        {
            TableDescriptor descriptor = catalog.Resolve(tableName);
            ITableProvider provider = catalog.CreateProvider(descriptor);
            DatumIndexTableResult tableResult = await BuildIndexForTableAsync(
                descriptor, provider, fingerprint, options, progress, cancellationToken).ConfigureAwait(false);

            tables[tableName] = tableResult;
            indexes[tableName] = tableResult.Index;
        }

        SourceIndexSet indexSet = new(fingerprint, indexes);

        return new DatumIndexResult
        {
            Fingerprint = fingerprint,
            Tables = tables,
            IndexSet = indexSet,
        };
    }

    private static async Task<DatumIndexTableResult> BuildIndexForTableAsync(
        TableDescriptor descriptor,
        ITableProvider provider,
        SourceFingerprint fingerprint,
        DatumIndexerOptions options,
        Action<IndexingProgress>? progress,
        CancellationToken cancellationToken)
    {
        Schema schema = await provider.GetSchemaAsync(descriptor, cancellationToken).ConfigureAwait(false);
        IncrementalIndexBuilder indexBuilder = new SourceIndexBuilder(
                bloomAllColumns: options.BloomAllColumns,
                indexAllColumns: options.IndexAllColumns,
                chunkSize: options.ChunkSize,
                autoIndexColumns: options.AutoIndexColumns,
                maxIndexedColumns: options.MaxIndexedColumns)
            .CreateIncrementalBuilder(fingerprint);

        Action<IndexingDiagnosticEvent>? diagnostics = options.Diagnostics;

        if (diagnostics is not null)
        {
            indexBuilder.OnChunkFlushed = (chunkIndex, rowCount) =>
                diagnostics(new IndexingDiagnosticEvent
                {
                    Kind = IndexingDiagnosticEventKind.ChunkFlushed,
                    TableName = descriptor.Name,
                    ChunkIndex = chunkIndex,
                    RowsProcessed = rowCount,
                });
        }

        long? totalRows = null;
        int lastReportedPercent = -1;

        if (progress is not null)
        {
            ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(descriptor, cancellationToken)
                .ConfigureAwait(false);
            totalRows = capabilities.EstimatedRowCount;
        }

        long rowsProcessed = 0;

        await foreach (Row row in provider.OpenAsync(descriptor, requiredColumns: null, cancellationToken)
            .ConfigureAwait(false))
        {
            indexBuilder.AddRow(row);
            rowsProcessed++;

            if (progress is not null && totalRows is > 0)
            {
                int currentPercent = (int)Math.Min(100, rowsProcessed * 100 / totalRows.Value);
                if (currentPercent >= lastReportedPercent + 5)
                {
                    lastReportedPercent = currentPercent;
                    progress(new IndexingProgress(
                        descriptor.Name, rowsProcessed, totalRows.Value, currentPercent));
                }
            }
        }

        if (progress is not null && totalRows is > 0 && lastReportedPercent < 100)
        {
            progress(new IndexingProgress(
                descriptor.Name, rowsProcessed, totalRows.Value, 100));
        }

        SourceIndex index = indexBuilder.Finalize();

        diagnostics?.Invoke(new IndexingDiagnosticEvent
        {
            Kind = IndexingDiagnosticEventKind.ScanningCompleted,
            TableName = descriptor.Name,
            RowsProcessed = rowsProcessed,
            TotalChunks = index.Chunks.Count,
        });

        // Write the index to a temporary file using streaming sorted indexes from the spill
        // writer. This avoids materializing the full ValueIndexEntry arrays (~5 GB for 32M rows)
        // and bypasses the 2 GB MemoryStream capacity limit.
        string indexTempPath = Path.Combine(Path.GetTempPath(), $"datum-ingest-idx-{Guid.NewGuid():N}.tmp");
        FileStream indexStream = new(
            indexTempPath, FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 65536, FileOptions.DeleteOnClose);
        IndexWriter indexWriter = new();
        string sidecarTableName = GetSidecarTableName(descriptor);

        indexWriter.Write(
            SourceIndexSet.Create(sidecarTableName, index),
            indexStream,
            indexBuilder.SpillWriter,
            compressIndexes: options.CompressIndexes);

        diagnostics?.Invoke(new IndexingDiagnosticEvent
        {
            Kind = IndexingDiagnosticEventKind.IndexWriteCompleted,
            TableName = descriptor.Name,
            RowsProcessed = rowsProcessed,
            TotalChunks = index.Chunks.Count,
            BytesWritten = indexStream.Length,
        });

        indexBuilder.Dispose();

        indexStream.Position = 0;

        return new DatumIndexTableResult
        {
            TableName = descriptor.Name,
            IndexFileName = $"{sidecarTableName}.datum-index",
            Index = index,
            IndexStream = indexStream,
        };
    }

    private static string GetSidecarTableName(TableDescriptor descriptor)
    {
        if (descriptor.Options.ContainsKey(TableCatalog.SubTableKeyOption))
        {
            return descriptor.Name;
        }

        return FileFormatDetector.DeriveTableName(descriptor.FilePath);
    }
}
