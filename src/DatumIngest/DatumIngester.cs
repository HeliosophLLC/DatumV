using DatumIngest.Catalog;
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
/// <see cref="IngestAsync(string, CancellationToken)"/> converts any format that
/// <see cref="TableCatalog"/> recognises (CSV, TSV, JSON, JSON Lines, Parquet, HDF5,
/// ZIP, IDX, or <c>.datum</c>) into <c>.datum</c> streams with a <see cref="SourceManifest"/>
/// containing per-column statistics. It does <em>not</em> build indexes.
/// </para>
/// <para>
/// <see cref="BuildIndexAsync(string, DatumIndexerOptions?, CancellationToken)"/> reads
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="DatumIngestionResult"/> containing the <c>.datum</c> stream,
    /// schema JSON, and manifest JSON. Does not include indexes.
    /// </returns>
    public static Task<DatumIngestionResult> IngestAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        return IngestCoreAsync(
            baseTableName: Path.GetFileName(filePath),
            filePath: filePath,
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
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<DatumIngestionResult> IngestAsync(
        string fileName,
        Stream source,
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
                baseTableName: Path.GetFileName(fileName),
                filePath: tempPath,
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="DatumIndexResult"/> containing the <c>.datum-index</c> stream
    /// and in-memory <see cref="SourceIndexSet"/>.
    /// </returns>
    public static Task<DatumIndexResult> BuildIndexAsync(
        string datumFilePath,
        DatumIndexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return BuildIndexCoreAsync(
            baseTableName: Path.GetFileName(datumFilePath),
            filePath: datumFilePath,
            options: options ?? DatumIndexerOptions.Default,
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
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<DatumIndexResult> BuildIndexAsync(
        string fileName,
        Stream datumSource,
        DatumIndexerOptions? options = null,
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
                baseTableName: Path.GetFileName(fileName),
                filePath: tempPath,
                options: options ?? DatumIndexerOptions.Default,
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
                descriptor, provider, cancellationToken).ConfigureAwait(false);

            tables[tableName] = tableResult;
            schemas[tableName] = tableResult.Schema;
            manifests[tableName] = tableResult.Manifest.Tables[tableName];
        }

        SourceSchema sourceSchema = new() { Tables = schemas };
        SourceManifest sourceManifest = new() { Tables = manifests };

        string schemaJson = SchemaSerializer.Serialize(sourceSchema);
        string manifestJson = ManifestSerializer.Serialize(sourceManifest);

        return new DatumIngestionResult
        {
            Fingerprint = fingerprint,
            Tables = tables,
            SourceSchema = sourceSchema,
            SourceManifest = sourceManifest,
            SchemaJson = schemaJson,
            ManifestJson = manifestJson,
        };
    }

    private static async Task<DatumIngestionTableResult> IngestTableAsync(
        TableDescriptor descriptor,
        ITableProvider provider,
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

        long rowCount = 0;
        await foreach (Row row in provider.OpenAsync(descriptor, requiredColumns: null, cancellationToken)
            .ConfigureAwait(false))
        {
            await datumWriter.WriteRowAsync(row, cancellationToken).ConfigureAwait(false);
            rowCount++;
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
        };
    }

    // ──────────────────── Indexing core ────────────────────

    private static async Task<DatumIndexResult> BuildIndexCoreAsync(
        string baseTableName,
        string filePath,
        DatumIndexerOptions options,
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
                descriptor, provider, fingerprint, options, cancellationToken).ConfigureAwait(false);

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

        await foreach (Row row in provider.OpenAsync(descriptor, requiredColumns: null, cancellationToken)
            .ConfigureAwait(false))
        {
            indexBuilder.AddRow(row);
        }

        SourceIndex index = indexBuilder.Finalize();

        // Write the index to a temporary file using streaming sorted indexes from the spill
        // writer. This avoids materializing the full ValueIndexEntry arrays (~5 GB for 32M rows)
        // and bypasses the 2 GB MemoryStream capacity limit.
        string indexTempPath = Path.Combine(Path.GetTempPath(), $"datum-ingest-idx-{Guid.NewGuid():N}.tmp");
        FileStream indexStream = new(
            indexTempPath, FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 65536, FileOptions.DeleteOnClose);
        IndexWriter indexWriter = new();
        indexWriter.Write(
            SourceIndexSet.Create(descriptor.Name, index),
            indexStream,
            indexBuilder.SpillWriter,
            compressIndexes: options.CompressIndexes);

        indexBuilder.Dispose();

        indexStream.Position = 0;

        return new DatumIndexTableResult
        {
            TableName = descriptor.Name,
            IndexFileName = $"{descriptor.Name}.datum-index",
            Index = index,
            IndexStream = indexStream,
        };
    }
}
