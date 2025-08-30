using DatumIngest.Catalog;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;
using DatumIngest.Statistics.Interactions;

namespace DatumIngest.Analysis;

/// <summary>
/// Single-pass source file analyzer that produces both a chunk-level index
/// (<see cref="SourceIndexSet"/>) and a column-level statistics manifest
/// (<see cref="SourceManifest"/>) for one or more tables within a source file.
/// </summary>
/// <remarks>
/// <para>
/// Each table's rows are streamed exactly once. During the pass, rows are fed
/// simultaneously to an <see cref="IncrementalIndexBuilder"/> and a
/// <see cref="StatisticsCollector"/> (plus an optional
/// <see cref="ColumnInteractionCollector"/>), then finalized into paired
/// <see cref="SourceIndex"/> and <see cref="QueryResultsManifest"/> entries.
/// </para>
/// <para>
/// For index-only workloads, use <see cref="SourceIndexBuilder"/> directly.
/// </para>
/// </remarks>
public sealed class SourceAnalyzer
{
    private readonly int _chunkSize;
    private readonly IReadOnlySet<string>? _bloomColumns;
    private readonly IReadOnlySet<string>? _indexColumns;
    private readonly bool _bloomAllColumns;
    private readonly bool _indexAllColumns;
    private readonly bool _autoIndexColumns;
    private readonly bool _withInteractions;

    /// <summary>
    /// Creates a source analyzer with the specified options and optional column-specific indexes.
    /// </summary>
    /// <param name="chunkSize">Number of rows per index chunk (default: 10,000).</param>
    /// <param name="bloomColumns">Column names to build bloom filters for, or <c>null</c> for none.</param>
    /// <param name="indexColumns">Column names to build sorted value indexes for, or <c>null</c> for none.</param>
    /// <param name="withInteractions">Whether to collect pairwise column interaction statistics.</param>
    public SourceAnalyzer(
        int chunkSize = IndexConstants.DefaultChunkSize,
        IReadOnlySet<string>? bloomColumns = null,
        IReadOnlySet<string>? indexColumns = null,
        bool withInteractions = false)
    {
        _chunkSize = chunkSize;
        _bloomColumns = bloomColumns;
        _indexColumns = indexColumns;
        _bloomAllColumns = false;
        _indexAllColumns = false;
        _autoIndexColumns = false;
        _withInteractions = withInteractions;
    }

    /// <summary>
    /// Creates a source analyzer that discovers columns from the data and optionally indexes all of them.
    /// </summary>
    /// <param name="bloomAllColumns">When <c>true</c>, builds bloom filters for every column discovered in the data.</param>
    /// <param name="indexAllColumns">When <c>true</c>, builds sorted value indexes for every column discovered in the data.</param>
    /// <param name="chunkSize">Number of rows per index chunk (default: 10,000).</param>
    /// <param name="withInteractions">Whether to collect pairwise column interaction statistics.</param>
    /// <param name="autoIndexColumns">
    /// When <c>true</c> and <paramref name="indexAllColumns"/> is <c>false</c>,
    /// automatically selects compact columns for sorted indexing.
    /// </param>
    public SourceAnalyzer(
        bool bloomAllColumns,
        bool indexAllColumns,
        int chunkSize = IndexConstants.DefaultChunkSize,
        bool withInteractions = false,
        bool autoIndexColumns = false)
    {
        _chunkSize = chunkSize;
        _bloomColumns = null;
        _indexColumns = null;
        _bloomAllColumns = bloomAllColumns;
        _indexAllColumns = indexAllColumns;
        _autoIndexColumns = autoIndexColumns;
        _withInteractions = withInteractions;
    }

    /// <summary>
    /// Analyzes one or more tables from the same source file in a single pass each,
    /// producing both indexes and statistics.
    /// </summary>
    /// <param name="tables">
    /// One or more (descriptor, provider) pairs representing logical tables within a
    /// single source file.
    /// </param>
    /// <param name="sourceStream">
    /// Seekable stream over the source file for fingerprint computation,
    /// or <c>null</c> if fingerprinting is not possible.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Combined index set and statistics manifest for all tables.</returns>
    public async Task<SourceAnalysisResult> AnalyzeAsync(
        IReadOnlyList<(TableDescriptor Descriptor, ITableProvider Provider)> tables,
        Stream? sourceStream,
        CancellationToken cancellationToken)
    {
        return await AnalyzeAsync(tables, sourceStream, fingerprint: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Analyzes one or more tables from the same source file, using an
    /// externally-computed fingerprint to avoid redundant source file hashing.
    /// </summary>
    /// <param name="tables">
    /// One or more (descriptor, provider) pairs representing logical tables within a
    /// single source file.
    /// </param>
    /// <param name="sourceStream">
    /// Seekable stream, used only when <paramref name="fingerprint"/> is <c>null</c>.
    /// </param>
    /// <param name="fingerprint">
    /// Pre-computed fingerprint, or <c>null</c> to compute from <paramref name="sourceStream"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Combined index set and statistics manifest for all tables.</returns>
    public async Task<SourceAnalysisResult> AnalyzeAsync(
        IReadOnlyList<(TableDescriptor Descriptor, ITableProvider Provider)> tables,
        Stream? sourceStream,
        SourceFingerprint? fingerprint,
        CancellationToken cancellationToken)
    {
        fingerprint ??= sourceStream is not null
            ? await SourceFingerprint.ComputeAsync(sourceStream, cancellationToken).ConfigureAwait(false)
            : new SourceFingerprint(0, Array.Empty<byte>());

        SourceIndexBuilder indexBuilder = _bloomAllColumns || _indexAllColumns || _autoIndexColumns
            ? new(_bloomAllColumns, _indexAllColumns, _chunkSize, autoIndexColumns: _autoIndexColumns)
            : new SourceIndexBuilder(_chunkSize, _bloomColumns, _indexColumns);
        Dictionary<string, SourceIndex> tableIndexes = new();
        Dictionary<string, QueryResultsManifest> tableManifests = new();
        Dictionary<string, Schema> tableSchemas = new();

        foreach ((TableDescriptor descriptor, ITableProvider provider) in tables)
        {
            IncrementalIndexBuilder incremental = indexBuilder.CreateIncrementalBuilder(fingerprint);
            StatisticsCollector statisticsCollector = new();
            ColumnInteractionCollector? interactionCollector = _withInteractions ? new() : null;
            Dictionary<string, DataKind> columnKinds = new();
            List<ColumnInfo> columnInfos = new();
            long rowCount = 0;

            await foreach (RowBatch batch in provider.OpenAsync(
                descriptor, requiredColumns: null, cancellationToken).ConfigureAwait(false))
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    Row row = batch[i];
                    if (rowCount == 0)
                    {
                        foreach (string columnName in row.ColumnNames)
                        {
                            DataKind kind = row[columnName].Kind;
                            columnKinds[columnName] = kind;
                            columnInfos.Add(new ColumnInfo(columnName, kind, nullable: true));
                        }
                    }

                    incremental.AddRow(row);
                    statisticsCollector.AddRow(row);
                    interactionCollector?.AddRow(row);
                    rowCount++;
                }

                batch.Return();
            }

            SourceIndex index = incremental.Finalize();
            string sidecarTableName = GetSidecarTableName(descriptor);
            tableIndexes[sidecarTableName] = index;

            if (columnInfos.Count > 0)
            {
                tableSchemas[sidecarTableName] = new Schema(columnInfos);
            }

            IReadOnlyDictionary<string, ColumnStatistics> statistics = statisticsCollector.GetStatistics();
            IReadOnlyList<ColumnInteractionResult>? interactions = interactionCollector?.GetInteractions();
            QueryResultsManifest manifest = ManifestBuilder.Build(statistics, columnKinds, rowCount, interactions);
            tableManifests[sidecarTableName] = manifest;
        }

        SourceSchema sourceSchema = new() { Tables = tableSchemas };
        SourceIndexSet indexSet = new(fingerprint, tableIndexes);
        SourceManifest manifest2 = new() { Tables = tableManifests };

        return new SourceAnalysisResult(sourceSchema, indexSet, manifest2);
    }

    private static string GetSidecarTableName(TableDescriptor descriptor)
    {
        if (descriptor.Options.ContainsKey(TableCatalog.SubTableKeyOption))
        {
            return descriptor.Name;
        }

        return FileFormatDetector.DeriveTableName(descriptor.FilePath);
    }

    /// <summary>
    /// Analyzes all tables registered in a <see cref="TableCatalog"/>, resolving
    /// descriptors and providers automatically. The source file is opened as a
    /// stream for fingerprint computation when possible.
    /// </summary>
    /// <param name="catalog">Catalog containing the registered tables to analyze.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Combined index set and statistics manifest for all tables.</returns>
    public async Task<SourceAnalysisResult> AnalyzeAsync(
        TableCatalog catalog,
        CancellationToken cancellationToken)
    {
        List<(TableDescriptor Descriptor, ITableProvider Provider)> tables = new();

        foreach (string tableName in catalog.TableNames)
        {
            TableDescriptor descriptor = catalog.Resolve(tableName);
            ITableProvider provider = catalog.CreateProvider(descriptor);
            tables.Add((descriptor, provider));
        }

        // Open the source file for fingerprinting from the first table's path.
        Stream? sourceStream = null;

        try
        {
            if (tables.Count > 0 && File.Exists(tables[0].Descriptor.FilePath))
            {
                sourceStream = File.OpenRead(tables[0].Descriptor.FilePath);
            }

            return await AnalyzeAsync(tables, sourceStream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (sourceStream is not null)
            {
                await sourceStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
