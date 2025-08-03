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
            tableName: Path.GetFileNameWithoutExtension(filePath),
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
                tableName: Path.GetFileNameWithoutExtension(fileName),
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
        string tableName,
        string filePath,
        DatumIngesterOptions options,
        CancellationToken cancellationToken)
    {
        TableCatalog catalog = new();
        await catalog.RegisterAsync(filePath, cancellationToken).ConfigureAwait(false);

        TableDescriptor descriptor = catalog.Resolve(catalog.TableNames.First());
        ITableProvider provider = catalog.CreateProvider(descriptor);

        SourceFingerprint fingerprint;
        await using (FileStream sourceStream = File.OpenRead(filePath))
        {
            fingerprint = await SourceFingerprint.ComputeAsync(sourceStream, cancellationToken)
                .ConfigureAwait(false);
        }

        IncrementalIndexBuilder indexBuilder = new SourceIndexBuilder(
                bloomAllColumns: options.BloomAllColumns,
                indexAllColumns: options.IndexAllColumns,
                chunkSize: options.ChunkSize)
            .CreateIncrementalBuilder(fingerprint);

        StatisticsCollector statisticsCollector = new();

        MemoryStream datumStream = new();
        FusedDatumPipelineWriter datumWriter = new(datumStream, indexBuilder, statisticsCollector);

        Dictionary<string, DataKind> columnKinds = new();
        long rowCount = 0;

        await foreach (Row row in provider.OpenAsync(descriptor, requiredColumns: null, cancellationToken)
            .ConfigureAwait(false))
        {
            if (rowCount == 0)
            {
                List<ColumnInfo> columns = row.ColumnNames
                    .Select(name => new ColumnInfo(name, row[name].Kind, nullable: true))
                    .ToList();
                Schema schema = new(columns);
                await datumWriter.InitializeAsync(schema, cancellationToken).ConfigureAwait(false);
                foreach (ColumnInfo column in columns)
                {
                    columnKinds[column.Name] = column.Kind;
                }
            }

            await datumWriter.WriteRowAsync(row, cancellationToken).ConfigureAwait(false);
            rowCount++;
        }

        await datumWriter.FinalizeAsync(cancellationToken).ConfigureAwait(false);
        await datumWriter.DisposeAsync().ConfigureAwait(false);

        QueryResultsManifest tableManifest = ManifestBuilder.Build(
            datumWriter.Statistics!, columnKinds, rowCount);

        string schemaJson = SchemaSerializer.Serialize(tableName, datumWriter.Schema!);
        string manifestJson = ManifestSerializer.Serialize(tableName, tableManifest);

        MemoryStream indexStream = new();
        new IndexWriter().Write(SourceIndexSet.Create(tableName, datumWriter.CompletedIndex!), indexStream);

        datumStream.Position = 0;
        indexStream.Position = 0;

        return new DatumIngestionResult
        {
            TableName = tableName,
            DatumStream = datumStream,
            IndexStream = indexStream,
            SchemaJson = schemaJson,
            ManifestJson = manifestJson,
            RowCount = rowCount,
            FeatureCount = tableManifest.Features.Count,
        };
    }
}
