using DatumIngest.Catalog;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Output.Writers;
using DatumIngest.Statistics;

namespace DatumIngest;

/// <summary>
/// High-level entry point for converting a source data file into the <c>.datum</c>
/// columnar format, building a source index, and collecting column statistics — all
/// in a single streaming pass.
/// </summary>
/// <remarks>
/// <para>
/// Accepts any format that <see cref="TableCatalog"/> recognises: CSV, TSV, JSON,
/// JSON Lines, Parquet, HDF5, ZIP, IDX, or <c>.datum</c>.
/// </para>
/// <para>
/// Returns a <see cref="DatumIngestionResult"/> whose streams are positioned at
/// offset 0 and ready to upload. Call <see cref="IAsyncDisposable.DisposeAsync"/>
/// on the result when uploads are complete.
/// </para>
/// <para>
/// Multi-table sources (for example a root-object JSON file with several array properties)
/// produce one <c>.datum</c> stream and one <c>.datum-index</c> stream per discovered table.
/// Use <see cref="DatumIngestionResult.Tables"/> to access them.
/// </para>
/// </remarks>
public static class DatumIngester
{
    /// <summary>
    /// Ingests a source file from disk in a single streaming pass.
    /// </summary>
    /// <param name="filePath">Absolute path to the source file.</param>
    /// <param name="options">Optional ingestion options. Defaults are used when <c>null</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="DatumIngestionResult"/> containing the <c>.datum</c> stream,
    /// index stream, schema JSON, and manifest JSON.
    /// </returns>
    public static Task<DatumIngestionResult> IngestAsync(
        string filePath,
        DatumIngesterOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return IngestCoreAsync(
            baseTableName: Path.GetFileName(filePath),
            filePath: filePath,
            options: options ?? DatumIngesterOptions.Default,
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
    /// <param name="options">Optional ingestion options. Defaults are used when <c>null</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<DatumIngestionResult> IngestAsync(
        string fileName,
        Stream source,
        DatumIngesterOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The TableCatalog requires a real file path for format detection and
        // provider construction. Write to a temp file with the original extension
        // so extension-based detection works correctly, then clean up.
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
                options: options ?? DatumIngesterOptions.Default,
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

    // ──────────────────── Core implementation ────────────────────

    private static async Task<DatumIngestionResult> IngestCoreAsync(
        string baseTableName,
        string filePath,
        DatumIngesterOptions options,
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
        Dictionary<string, SourceIndex> indexes = new(StringComparer.OrdinalIgnoreCase);

        foreach (string tableName in catalog.TableNames)
        {
            TableDescriptor descriptor = catalog.Resolve(tableName);
            ITableProvider provider = catalog.CreateProvider(descriptor);
            DatumIngestionTableResult tableResult = await IngestTableAsync(
                descriptor, provider, fingerprint, options, cancellationToken).ConfigureAwait(false);

            tables[tableName] = tableResult;
            schemas[tableName] = tableResult.Schema;
            manifests[tableName] = tableResult.Manifest.Tables[tableName];
            indexes[tableName] = tableResult.Index;
        }

        SourceSchema sourceSchema = new() { Tables = schemas };
        SourceManifest sourceManifest = new() { Tables = manifests };
        SourceIndexSet indexSet = new(fingerprint, indexes);

        string schemaJson = SchemaSerializer.Serialize(sourceSchema);
        string manifestJson = ManifestSerializer.Serialize(sourceManifest);

        return new DatumIngestionResult
        {
            Fingerprint = fingerprint,
            Tables = tables,
            SourceSchema = sourceSchema,
            SourceManifest = sourceManifest,
            IndexSet = indexSet,
            SchemaJson = schemaJson,
            ManifestJson = manifestJson,
        };
    }

    private static async Task<DatumIngestionTableResult> IngestTableAsync(
        TableDescriptor descriptor,
        ITableProvider provider,
        SourceFingerprint fingerprint,
        DatumIngesterOptions options,
        CancellationToken cancellationToken)
    {
        Schema schema = await provider.GetSchemaAsync(descriptor, cancellationToken).ConfigureAwait(false);
        IncrementalIndexBuilder indexBuilder = new SourceIndexBuilder(
                bloomAllColumns: options.BloomAllColumns,
                indexAllColumns: options.IndexAllColumns,
                chunkSize: options.ChunkSize,
                autoIndexColumns: options.AutoIndexColumns)
            .CreateIncrementalBuilder(fingerprint);
        StatisticsCollector statisticsCollector = new();
        MemoryStream datumStream = new();
        FusedDatumPipelineWriter datumWriter = new(datumStream, indexBuilder, statisticsCollector);

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
        SourceIndex index = datumWriter.CompletedIndex
            ?? throw new InvalidOperationException("The datum writer did not produce an index.");
        QueryResultsManifest queryManifest = ManifestBuilder.Build(statistics, columnKinds, rowCount);
        SourceManifest manifest = SourceManifest.Create(descriptor.Name, queryManifest);
        string schemaJson = SchemaSerializer.Serialize(descriptor.Name, schema);
        string manifestJson = ManifestSerializer.Serialize(manifest);

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
            datumWriter.SortedIndexSpillWriter);

        // Dispose the datum writer (and its index builder / spill writer) after streaming.
        await datumWriter.DisposeAsync().ConfigureAwait(false);

        datumStream.Position = 0;
        indexStream.Position = 0;

        return new DatumIngestionTableResult
        {
            TableName = descriptor.Name,
            FileName = $"{descriptor.Name}.datum",
            IndexFileName = $"{descriptor.Name}.datum-index",
            ManifestFileName = $"{descriptor.Name}.datum-manifest",
            Schema = schema,
            Manifest = manifest,
            Index = index,
            DatumStream = datumStream,
            IndexStream = indexStream,
            SchemaJson = schemaJson,
            ManifestJson = manifestJson,
            RowCount = rowCount,
            FeatureCount = queryManifest.Features.Count,
        };
    }
}
