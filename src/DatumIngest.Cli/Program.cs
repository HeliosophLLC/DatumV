using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DatumIngest.Catalog;
using DatumIngest.Cli;
using DatumIngest.Compute.Grpc;
using DatumIngest.Compute.Services;
using Grpc.Core;
using GrpcClient = global::DatumIngest.Compute.Grpc.DatumCompute.DatumComputeClient;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Output;
using DatumIngest.Output.Checkpoint;
using CheckpointFingerprint = DatumIngest.Output.Checkpoint.SourceFingerprint;
using DatumIngest.Manifest;
using DatumIngest.Manifest.SchemaMatching;
using DatumIngest.Output.Writers;
using DatumIngest.Cli.Shell;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Interactions;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

try
{
    CliOptions options = CliOptions.Parse(args);
    TableCatalog catalog = BuildCatalog(options);

    // Expand multi-table sources (e.g. JSON files with multiple array properties).
    await catalog.ExpandMultiTableSourcesAsync(CancellationToken.None);

    // Load any pre-built indexes from explicit --index paths.
    LoadIndexes(catalog, options);

    // Commands that build artifacts from raw data — no manifests or schemas needed.
    if (options.Command == "index")
    {
        return await RunIndexAsync(catalog, options);
    }

    if (options.Command == "ingest")
    {
        return await RunIngestAsync(catalog, options);
    }

    // Remaining commands may require sidecar data (manifests, schemas, vocabularies).
    catalog.DiscoverSidecars();

    if (options.Command == "index-manifest")
    {
        return await RunIndexManifestAsync(catalog, options);
    }

    if (options.Command == "manifest-schema")
    {
        return await RunManifestSchemaAsync(catalog, options.OutputPath);
    }

    if (options.Command == "star-schema")
    {
        return await RunStarSchemaAsync(catalog, options);
    }

    // schema is purely compile-time resolution — no execution needed, no gRPC.
    if (options.Command == "schema")
    {
        if (options.SqlFile is not null)
        {
            options.Sql = options.SqlFile == "-"
                ? await Console.In.ReadToEndAsync()
                : await File.ReadAllTextAsync(options.SqlFile);
        }

        QueryExpression schemaQuery = SqlParser.Parse(options.Sql);
        return await RunSchemaAsync(schemaQuery, catalog);
    }

    // All remaining commands (shell, query, explore, stats, explain, manifest)
    // go through the in-process gRPC server.
    await using EmbeddedComputeHost host = await EmbeddedComputeHost.StartAsync(catalog, opts =>
    {
        opts.MemoryBudgetBytes = options.MemoryBudgetBytes;
    });

    CreateSessionResponse sessionResp = await host.Client.CreateSessionAsync(new CreateSessionRequest
    {
        Role = "admin",
        DatasetId = EmbeddedComputeHost.EmbeddedDatasetId,
    });

    CreateQueryContextResponse contextResp = await host.Client.CreateQueryContextAsync(
        new CreateQueryContextRequest
        {
            SessionId = sessionResp.SessionId,
            Label = "CLI",
        });

    string sessionId = sessionResp.SessionId;
    string contextId = contextResp.ContextId;

    if (options.Command == "shell")
    {
        InteractiveShell shell = new(host.Client, sessionId, contextId, catalog);
        return await shell.RunAsync(CancellationToken.None);
    }

    // Load SQL from file when --sql-file is specified.
    if (options.SqlFile is not null)
    {
        options.Sql = options.SqlFile == "-"
            ? await Console.In.ReadToEndAsync()
            : await File.ReadAllTextAsync(options.SqlFile);
    }

    // Build gRPC parameter map from CLI --param values.
    Dictionary<string, DataValueMessage>? grpcParameters = null;
    if (options.Parameters.Count > 0)
    {
        grpcParameters = new Dictionary<string, DataValueMessage>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in options.Parameters)
        {
            DataValue value = ParameterValueParser.Parse(entry.Value);
            grpcParameters[entry.Key] = ProtoConverter.ToProto(value, new Arena());

            throw new Exception("Arena logic above is incorrect!");
        }
    }

    return options.Command switch
    {
        "query" => await RunQueryViaGrpcAsync(host.Client, sessionId, contextId, options, grpcParameters),
        "explore" => await RunExploreViaGrpcAsync(host.Client, sessionId, contextId, options.Sql, options.Limit, grpcParameters),
        "stats" => await RunStatsViaGrpcAsync(host.Client, sessionId, contextId, options.Sql, grpcParameters),
        "explain" => await RunExplainViaGrpcAsync(host.Client, sessionId, contextId, options.Sql, options.Analyze),
        "manifest" => await RunManifestViaGrpcAsync(host.Client, sessionId, contextId, options.Sql, options.OutputPath, grpcParameters),
        _ => throw new ArgumentException($"Unknown command: {options.Command}. Use 'query', 'explore', 'stats', 'explain', 'manifest', 'manifest-schema', 'schema', 'shell', 'index', 'index-manifest', 'ingest', or 'star-schema'.")
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    return 2;
}

static TableCatalog BuildCatalog(CliOptions options)
{
    TableCatalog catalog = new();

    // Load catalog file if specified
    if (options.CatalogPath is not null)
    {
        LoadCatalogFile(catalog, options.CatalogPath);
    }

    // Parse inline --source definitions (override same-named catalog entries).
    // If a source is a directory path, auto-discover all supported files within it.
    foreach (string source in options.Sources)
    {
        if (Directory.Exists(source))
        {
            RegisterDirectory(catalog, source);
        }
        else
        {
            TableDescriptor descriptor = ParseSourceDefinition(source);
            catalog.Register(descriptor);
        }
    }

    return catalog;
}

static void LoadIndexes(TableCatalog catalog, CliOptions options)
{
    foreach (string indexPath in options.IndexPaths)
    {
        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException($"Index file not found: {indexPath}");
        }

        SourceIndexSet indexSet;

        MappedSourceIndexSet mapped = UnifiedIndexReader.Open(indexPath);
        catalog.TrackMappedIndexSet(mapped);
        indexSet = mapped.IndexSet;

        // Derive table name from the index file path by stripping the .datum-index suffix.
        string fileName = Path.GetFileName(indexPath);
        string tableName = fileName.EndsWith(".datum-index", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".datum-index".Length]
            : Path.GetFileNameWithoutExtension(fileName);

        // Register each table entry from the sidecar.
        foreach (KeyValuePair<string, SourceIndex> entry in indexSet.Tables)
        {
            string resolvedName = entry.Key.Length > 0 ? $"{tableName}.{entry.Key}" : tableName;

            // If a table with this name is registered, attach the index.
            // Otherwise check if stripping one more extension matches.
            if (catalog.TryResolve(resolvedName, out _))
            {
                catalog.RegisterIndex(resolvedName, entry.Value);
            }
            else
            {
                string baseName = Path.GetFileNameWithoutExtension(resolvedName);

                if (catalog.TryResolve(baseName, out _))
                {
                    catalog.RegisterIndex(baseName, entry.Value);
                }
                else
                {
                    catalog.RegisterIndex(resolvedName, entry.Value);
                }
            }
        }
    }
}

static SourceIndexBuilder CreateIndexBuilder(CliOptions options)
{
    if (options.BloomAllColumns || options.IndexAllColumns || options.AutoIndexColumns)
    {
        if (options.BloomColumns.Count > 0 || options.IndexColumns.Count > 0)
        {
            throw new ArgumentException(
                "Cannot combine --bloom-all/--index-all/--auto-index with --bloom-columns/--index-columns. Use one approach or the other.");
        }

        return new SourceIndexBuilder(options.BloomAllColumns, options.IndexAllColumns, options.ChunkSize, options.AutoIndexColumns);
    }

    HashSet<string>? bloomColumns = options.BloomColumns.Count > 0 ? options.BloomColumns : null;
    HashSet<string>? indexColumns = options.IndexColumns.Count > 0 ? options.IndexColumns : null;
    return new SourceIndexBuilder(options.ChunkSize, bloomColumns, indexColumns);
}

static async Task<int> RunIndexAsync(TableCatalog catalog, CliOptions options)
{
    SourceIndexBuilder builder = CreateIndexBuilder(options);

    // Collect descriptors from inline --source definitions.
    // Directory sources are handled by BuildCatalog and skipped here so the
    // fallback "index every table in the catalog" path picks them up.
    List<TableDescriptor> descriptors = new();

    foreach (string source in options.Sources)
    {
        if (Directory.Exists(source))
        {
            continue;
        }

        TableDescriptor descriptor = ParseSourceDefinition(source);

        if (!catalog.TryResolve(descriptor.Name, out _))
        {
            catalog.Register(descriptor);
        }

        descriptors.Add(descriptor);
    }

    // When no explicit sources are given, index every table in the catalog.
    if (descriptors.Count == 0)
    {
        foreach (string tableName in catalog.TableNames)
        {
            descriptors.Add(catalog.Resolve(tableName));
        }
    }

    if (descriptors.Count == 0)
    {
        throw new ArgumentException("The 'index' command requires at least one --source definition or a --catalog with tables.");
    }

    foreach (IGrouping<string, TableDescriptor> group in descriptors
        .GroupBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase))
    {
        await BuildGroupedIndexAsync(group, catalog, builder, options.ChunkSize);
    }

    return 0;
}

static async Task BuildGroupedIndexAsync(
    IGrouping<string, TableDescriptor> group,
    TableCatalog catalog,
    SourceIndexBuilder builder,
    int chunkSize)
{
    string filePath = group.Key;
    Stream? sourceStream = null;

    if (File.Exists(filePath))
    {
        sourceStream = File.OpenRead(filePath);
    }

    try
    {
        DatumIngest.Indexing.SourceFingerprint fingerprint = sourceStream is not null
            ? await DatumIngest.Indexing.SourceFingerprint.ComputeAsync(
                sourceStream, CancellationToken.None).ConfigureAwait(false)
            : new DatumIngest.Indexing.SourceFingerprint(0, Array.Empty<byte>());

        foreach (TableDescriptor descriptor in group)
        {
            ITableProvider provider = catalog.CreateProvider(descriptor);
            IncrementalIndexBuilder indexBuilder = builder.CreateIncrementalBuilder(fingerprint);

            await foreach (RowBatch batch in provider.OpenAsync(descriptor, requiredColumns: null, CancellationToken.None))
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    Row row = batch[i];
                    indexBuilder.AddRow(row);
                }
                batch.Return();
            }

            SourceIndex index = indexBuilder.Finalize();

            Console.WriteLine($"  Table '{descriptor.Name}':");
            Console.WriteLine($"    Schema: {index.Schema.Schema.Columns.Count} columns, {index.Schema.TotalRowCount} rows");
            Console.WriteLine($"    Chunks: {index.Chunks.Count} (chunk size: {chunkSize})");

            if (index.BloomFilters is not null)
            {
                Console.WriteLine($"    Bloom filters: {string.Join(", ", index.BloomFilters.ColumnNames)}");
            }

            // Write the index using the streaming spill writer path, avoiding materialization
            // of full ValueIndexEntry arrays that cause OOM for large datasets.
            string sidecarTableName = GetSidecarTableName(descriptor);
            SourceIndexSet indexSet = SourceIndexSet.Create(sidecarTableName, index);
            string indexPath = FileFormatDetector.GetSidecarBasePath(filePath) + ".datum-index";

            using FileStream outputStream = File.Create(indexPath);
            UnifiedIndexWriter.Write(indexSet, outputStream, indexBuilder.SpillWriter);

            indexBuilder.Dispose();

            Console.WriteLine($"Index created: {indexPath}");
        }
    }
    finally
    {
        if (sourceStream is not null)
        {
            await sourceStream.DisposeAsync();
        }
    }
}

static async Task<int> RunIndexManifestAsync(TableCatalog catalog, CliOptions options)
{
    SourceIndexBuilder builder = CreateIndexBuilder(options);

    List<TableDescriptor> descriptors = new();

    foreach (string source in options.Sources)
    {
        if (Directory.Exists(source))
        {
            continue;
        }

        TableDescriptor descriptor = ParseSourceDefinition(source);

        if (!catalog.TryResolve(descriptor.Name, out _))
        {
            catalog.Register(descriptor);
        }

        descriptors.Add(descriptor);
    }

    if (descriptors.Count == 0)
    {
        foreach (string tableName in catalog.TableNames)
        {
            descriptors.Add(catalog.Resolve(tableName));
        }
    }

    if (descriptors.Count == 0)
    {
        throw new ArgumentException("The 'index-manifest' command requires at least one --source definition or a --catalog with tables.");
    }

    foreach (IGrouping<string, TableDescriptor> group in descriptors
        .GroupBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase))
    {
        await BuildGroupedIndexAndManifestAsync(group, catalog, builder, options);
    }

    return 0;
}

static async Task BuildGroupedIndexAndManifestAsync(
    IGrouping<string, TableDescriptor> group,
    TableCatalog catalog,
    SourceIndexBuilder builder,
    CliOptions options)
{
    string filePath = group.Key;
    Stream? sourceStream = null;

    if (File.Exists(filePath))
    {
        sourceStream = File.OpenRead(filePath);
    }

    try
    {
        DatumIngest.Indexing.SourceFingerprint fingerprint = sourceStream is not null
            ? await DatumIngest.Indexing.SourceFingerprint.ComputeAsync(
                sourceStream, CancellationToken.None).ConfigureAwait(false)
            : new DatumIngest.Indexing.SourceFingerprint(0, Array.Empty<byte>());

        Dictionary<string, SourceIndex> tableIndexes = new();
        Dictionary<string, QueryResultsManifest> tableManifests = new();

        foreach (TableDescriptor descriptor in group)
        {
            ITableProvider provider = catalog.CreateProvider(descriptor);
            IncrementalIndexBuilder indexBuilder = builder.CreateIncrementalBuilder(fingerprint);
            StatisticsCollector statisticsCollector = new();
            using Arena statisticsArena = new(); // TODO: remove when CLI ingestion is refactored
            ColumnInteractionCollector? interactionCollector = options.WithInteractions ? new() : null;
            ProgressReporter progress = new();
            Dictionary<string, DataKind> columnKinds = new();
            long rowCount = 0;

            await foreach (RowBatch batch in provider.OpenAsync(
                descriptor, requiredColumns: null, CancellationToken.None).ConfigureAwait(false))
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    Row row = batch[i];
                    if (rowCount == 0)
                    {
                        foreach (string columnName in row.ColumnNames)
                        {
                            columnKinds[columnName] = row[columnName].Kind;
                        }
                    }

                    indexBuilder.AddRow(row);
                    statisticsCollector.AddRow(row, statisticsArena);
                    interactionCollector?.AddRow(row);
                    rowCount++;
                    progress.ReportRow();
                }
                batch.Return();
            }

            progress.WriteSummary();

            SourceIndex index = indexBuilder.Finalize();
            string sidecarTableName = GetSidecarTableName(descriptor);
            tableIndexes[sidecarTableName] = index;

            Console.WriteLine($"  Table '{descriptor.Name}':");
            Console.WriteLine($"    Schema: {index.Schema.Schema.Columns.Count} columns, {index.Schema.TotalRowCount} rows");
            Console.WriteLine($"    Chunks: {index.Chunks.Count} (chunk size: {options.ChunkSize})");

            if (index.BloomFilters is not null)
            {
                Console.WriteLine($"    Bloom filters: {string.Join(", ", index.BloomFilters.ColumnNames)}");
            }

            if (index.SortedIndexes is not null)
            {
                Console.WriteLine($"    Sorted indexes: {string.Join(", ", index.SortedIndexes.ColumnNames)}");
            }

            IReadOnlyDictionary<string, ColumnStatistics> statistics = statisticsCollector.GetStatistics();
            IReadOnlyList<ColumnInteractionResult>? interactions = interactionCollector?.GetInteractions();
            QueryResultsManifest manifest = ManifestBuilder.Build(statistics, columnKinds, rowCount, interactions);
            tableManifests[sidecarTableName] = manifest;

            Console.WriteLine($"    Features: {manifest.Features.Count}");

            if (interactions is { Count: > 0 })
            {
                Console.WriteLine($"    Interactions: {interactions.Count} pairs");
            }
        }

        // Write grouped index sidecar.
        SourceIndexSet indexSet = new(fingerprint, tableIndexes);
        string indexPath = FileFormatDetector.GetSidecarBasePath(filePath) + ".datum-index";
        using (FileStream outputStream = File.Create(indexPath))
        {
            UnifiedIndexWriter.Write(indexSet, outputStream);
        }

        Console.WriteLine($"Index created: {indexPath}");

        // Write grouped manifest sidecar.
        SourceManifest sourceManifest = new() { Tables = tableManifests };
        string manifestPath = options.OutputPath ?? FileFormatDetector.GetSidecarBasePath(filePath) + ".datum-manifest";
        await ManifestSerializer.WriteToFileAsync(sourceManifest, manifestPath).ConfigureAwait(false);

        Console.WriteLine($"Manifest created: {manifestPath}");

        // Write grouped vocabulary sidecar when any columns have attached vocabularies.
        SourceVocabularySet? vocabularySet = SourceVocabularySet.ExtractFrom(sourceManifest);

        if (vocabularySet is not null)
        {
            string vocabularyPath = FileFormatDetector.GetSidecarBasePath(filePath) + ".datum-vocabulary";
            await ManifestSerializer.WriteVocabularyToFileAsync(vocabularySet, vocabularyPath).ConfigureAwait(false);

            int columnCount = vocabularySet.Tables.Values.Sum(table => table.Columns.Count);
            Console.WriteLine($"Vocabulary created: {vocabularyPath} ({columnCount} column(s))");
        }
    }
    finally
    {
        if (sourceStream is not null)
        {
            await sourceStream.DisposeAsync();
        }
    }
}

static async Task<int> RunIngestAsync(TableCatalog catalog, CliOptions options)
{
    List<TableDescriptor> descriptors = new();

    foreach (string source in options.Sources)
    {
        if (Directory.Exists(source))
        {
            continue;
        }

        TableDescriptor descriptor = ParseSourceDefinition(source);

        if (!catalog.TryResolve(descriptor.Name, out _))
        {
            catalog.Register(descriptor);
        }

        descriptors.Add(descriptor);
    }

    // When no explicit sources are given, ingest every table in the catalog.
    if (descriptors.Count == 0)
    {
        foreach (string tableName in catalog.TableNames)
        {
            descriptors.Add(catalog.Resolve(tableName));
        }
    }

    if (descriptors.Count == 0)
    {
        throw new ArgumentException("The 'ingest' command requires at least one --source definition or a --catalog with tables.");
    }

    bool withIndex = options.WithIndex || options.AutoIndexColumns
        || options.IndexAllColumns || options.IndexColumns.Count > 0
        || options.BloomAllColumns || options.BloomColumns.Count > 0
        || options.BitmapAllColumns || options.BitmapColumns.Count > 0;

    SourceIndexBuilder? indexBuilder = withIndex ? CreateIndexBuilder(options) : null;

    foreach (TableDescriptor descriptor in descriptors)
    {
        await IngestTableAsync(descriptor, catalog, indexBuilder, options);
    }

    return 0;
}

static async Task IngestTableAsync(
    TableDescriptor descriptor,
    TableCatalog catalog,
    SourceIndexBuilder? indexBuilder,
    CliOptions options)
{
    ITableProvider provider = catalog.CreateProvider(descriptor);
    StatisticsCollector statisticsCollector = new();
    ColumnInteractionCollector? interactionCollector = options.WithInteractions ? new() : null;

    // Compute fingerprint from source file.
    DatumIngest.Indexing.SourceFingerprint fingerprint;
    if (File.Exists(descriptor.FilePath))
    {
        await using FileStream sourceStream = File.OpenRead(descriptor.FilePath);
        fingerprint = await DatumIngest.Indexing.SourceFingerprint.ComputeAsync(
            sourceStream, CancellationToken.None).ConfigureAwait(false);
    }
    else
    {
        fingerprint = new DatumIngest.Indexing.SourceFingerprint(0, Array.Empty<byte>());
    }

    IncrementalIndexBuilder? incrementalBuilder = indexBuilder?.CreateIncrementalBuilder(fingerprint);

    // Determine output path.
    string outputDirectory = options.OutputDirectory
        ?? Path.GetDirectoryName(descriptor.FilePath)
        ?? Directory.GetCurrentDirectory();
    Directory.CreateDirectory(outputDirectory);

    string tableName = GetSidecarTableName(descriptor);
    string datumPath = Path.Combine(outputDirectory, tableName + ".datum");

    Console.WriteLine($"Ingesting '{descriptor.Name}' → {datumPath}");

    FusedDatumPipelineWriter datumWriter = new(datumPath, incrementalBuilder, statisticsCollector);

    Dictionary<string, DataKind> columnKinds = new(StringComparer.OrdinalIgnoreCase);
    ProgressReporter progress = new();
    long rowCount = 0;

    await foreach (RowBatch batch in provider.OpenAsync(
        descriptor, requiredColumns: null, CancellationToken.None).ConfigureAwait(false))
    {
        for (int i = 0; i < batch.Count; i++)
        {
            Row row = batch[i];
            if (rowCount == 0)
            {
                // Initialize the writer with the inferred schema from the first row.
                Schema schema = InferSchema(row);
                await datumWriter.InitializeAsync(schema).ConfigureAwait(false);

                foreach (string columnName in row.ColumnNames)
                {
                    columnKinds[columnName] = row[columnName].Kind;
                }
            }

            datumWriter.WriteRow(row);
            interactionCollector?.AddRow(row);
            rowCount++;
            progress.ReportRow();
        }
        batch.Return();
    }

    if (rowCount == 0)
    {
        Console.WriteLine($"  No rows — skipping.");
        await datumWriter.DisposeAsync().ConfigureAwait(false);
        return;
    }

    OutputSummary summary = await datumWriter.FinalizeAsync().ConfigureAwait(false);
    progress.WriteSummary();

    Console.WriteLine($"  {rowCount:N0} rows, {summary.BytesWritten:N0} bytes");

    foreach (string file in summary.FilesCreated)
    {
        Console.WriteLine($"  Created: {file}");
    }

    // Build and write manifest sidecar.
    IReadOnlyDictionary<string, ColumnStatistics> statistics = datumWriter.Statistics
        ?? statisticsCollector.GetStatistics();
    IReadOnlyList<ColumnInteractionResult>? interactions = interactionCollector?.GetInteractions();
    QueryResultsManifest manifest = ManifestBuilder.Build(statistics, columnKinds, rowCount, interactions);
    SourceManifest sourceManifest = SourceManifest.Create(tableName, manifest);
    string manifestPath = Path.Combine(outputDirectory, tableName + ".datum-manifest");
    await ManifestSerializer.WriteToFileAsync(sourceManifest, manifestPath).ConfigureAwait(false);
    Console.WriteLine($"  Manifest: {manifestPath}");

    if (interactions is { Count: > 0 })
    {
        Console.WriteLine($"    Interactions: {interactions.Count} pairs");
    }

    // Write vocabulary sidecar when any columns have attached vocabularies.
    SourceVocabularySet? vocabularySet = SourceVocabularySet.ExtractFrom(sourceManifest);
    if (vocabularySet is not null)
    {
        string vocabularyPath = Path.Combine(outputDirectory, tableName + ".datum-vocabulary");
        await ManifestSerializer.WriteVocabularyToFileAsync(vocabularySet, vocabularyPath).ConfigureAwait(false);
        int columnCount = vocabularySet.Tables.Values.Sum(table => table.Columns.Count);
        Console.WriteLine($"  Vocabulary: {vocabularyPath} ({columnCount} column(s))");
    }

    await datumWriter.DisposeAsync().ConfigureAwait(false);
}

static async Task<int> RunStarSchemaAsync(TableCatalog catalog, CliOptions options)
{
    List<TableDescriptor> descriptors = new();

    foreach (string source in options.Sources)
    {
        if (Directory.Exists(source))
        {
            continue;
        }

        TableDescriptor descriptor = ParseSourceDefinition(source);

        if (!catalog.TryResolve(descriptor.Name, out _))
        {
            catalog.Register(descriptor);
        }

        descriptors.Add(descriptor);
    }

    if (descriptors.Count == 0)
    {
        foreach (string tableName in catalog.TableNames)
        {
            descriptors.Add(catalog.Resolve(tableName));
        }
    }

    if (descriptors.Count == 0)
    {
        throw new ArgumentException("The 'star-schema' command requires at least one --source definition or a --catalog with tables.");
    }

    List<ManifestWithName> manifests = new();

    foreach (TableDescriptor descriptor in descriptors)
    {
        ITableProvider provider = catalog.CreateProvider(descriptor);
        StatisticsCollector statisticsCollector = new();
        using Arena statisticsArena2 = new(); // TODO: remove when star-schema is refactored
        Dictionary<string, DataKind> columnKinds = new();
        long rowCount = 0;

        await foreach (RowBatch batch in provider.OpenAsync(
            descriptor, requiredColumns: null, CancellationToken.None).ConfigureAwait(false))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                if (rowCount == 0)
                {
                    foreach (string columnName in row.ColumnNames)
                    {
                        columnKinds[columnName] = row[columnName].Kind;
                    }
                }

                statisticsCollector.AddRow(row, statisticsArena2);
                rowCount++;
            }
            batch.Return();
        }

        IReadOnlyDictionary<string, ColumnStatistics> statistics = statisticsCollector.GetStatistics();
        QueryResultsManifest manifest = ManifestBuilder.Build(statistics, columnKinds, rowCount);
        string tableName = GetSidecarTableName(descriptor);
        manifests.Add(new ManifestWithName(tableName, manifest));

        Console.WriteLine($"  {tableName}: {rowCount:N0} rows, {columnKinds.Count} columns");
    }

    StarSchemaResult result = StarSchemaDetector.Detect(manifests);

    string json = ManifestSerializer.SerializeStarSchema(result);

    if (options.OutputPath is not null)
    {
        await File.WriteAllTextAsync(options.OutputPath, json).ConfigureAwait(false);
        Console.WriteLine($"Star schema written: {options.OutputPath}");
    }
    else
    {
        Console.WriteLine(json);
    }

    return 0;
}

static string GetSidecarTableName(TableDescriptor descriptor)
{
    // Expanded multi-table sources already use a stable qualified name.
    if (descriptor.Options.ContainsKey(TableCatalog.SubTableKeyOption))
    {
        return descriptor.Name;
    }

    // Single-table sources should always key sidecars by the derived SQL table name.
    return FileFormatDetector.DeriveTableName(descriptor.FilePath);
}

static void LoadCatalogFile(TableCatalog catalog, string catalogPath)
{
    if (!File.Exists(catalogPath))
    {
        throw new FileNotFoundException($"Catalog file not found: {catalogPath}");
    }

    string json = File.ReadAllText(catalogPath);
    CatalogEntry[]? entries = JsonSerializer.Deserialize(json, CatalogJsonContext.Default.CatalogEntryArray);

    if (entries is null)
    {
        throw new InvalidOperationException("Catalog file contains invalid JSON.");
    }

    foreach (CatalogEntry entry in entries)
    {
        Dictionary<string, string> entryOptions = entry.Options ?? [];
        catalog.Register(new TableDescriptor(entry.Provider, entry.Name, entry.FilePath, entryOptions));
    }
}

// Scans a directory for all supported data files and registers each one in the
// catalog, mirroring the behaviour of DatasetCatalogFactory in DatumIngest.Compute.
static void RegisterDirectory(TableCatalog catalog, string directoryPath)
{
    foreach (string pattern in FileFormatDetector.SupportedFilePatterns)
    {
        foreach (string filePath in Directory.EnumerateFiles(directoryPath, pattern))
        {
            string tableName = FileFormatDetector.DeriveTableName(filePath);

            if (catalog.TryResolve(tableName, out _))
            {
                continue;
            }

            catalog.Register(filePath);
        }
    }
}

static TableDescriptor ParseSourceDefinition(string source)
{
    // Two supported formats:
    //   Explicit:    provider:name=path[;key=value;...]
    //   Auto-detect: name=path[;key=value;...]
    //
    // Heuristic: if '=' appears before ':' (or there is no ':'), the source
    // omits the provider prefix and we auto-detect from the file.
    // Windows drive letters (e.g. C:\) are handled because the '=' in
    // "name=C:\..." always precedes the colon in the path.
    int colonIndex = source.IndexOf(':');
    int equalsIndex = source.IndexOf('=');

    if (equalsIndex < 0)
    {
        // Bare file path — auto-detect provider and derive name from the file name.
        if (File.Exists(source))
        {
            DetectedFormat format = FileFormatDetector.DetectFormat(source)
                ?? throw new ArgumentException(
                    $"Cannot determine file format for '{source}'. " +
                    "Specify explicitly: name=path or provider:name=path");
            string derivedName = FileFormatDetector.DeriveTableName(source);
            return new TableDescriptor(format.Provider, derivedName, source, new Dictionary<string, string>(), format.Compression);
        }

        throw new ArgumentException(
            $"Invalid source format: '{source}'. " +
            "Expected format: name=path or provider:name=path[;key=value]");
    }

    bool hasExplicitProvider = colonIndex >= 0 && colonIndex < equalsIndex;

    string provider;
    string remainder;

    if (hasExplicitProvider)
    {
        provider = source[..colonIndex];
        remainder = source[(colonIndex + 1)..];
    }
    else
    {
        provider = null!; // Resolved below after parsing the file path.
        remainder = source;
    }

    int nameEqualsIndex = remainder.IndexOf('=');
    string rawName = remainder[..nameEqualsIndex];
    string name = FileFormatDetector.DeriveTableName(rawName);
    string pathAndOptions = remainder[(nameEqualsIndex + 1)..];

    Dictionary<string, string> options = new();
    string filePath;

    int semiIndex = pathAndOptions.IndexOf(';');
    if (semiIndex >= 0)
    {
        filePath = pathAndOptions[..semiIndex];
        string optionsPart = pathAndOptions[(semiIndex + 1)..];

        foreach (string pair in optionsPart.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int keyValueSplit = pair.IndexOf('=');
            if (keyValueSplit > 0)
            {
                options[pair[..keyValueSplit]] = pair[(keyValueSplit + 1)..];
            }
        }
    }
    else
    {
        filePath = pathAndOptions;
    }

    if (!hasExplicitProvider)
    {
        DetectedFormat format = FileFormatDetector.DetectFormat(filePath)
            ?? throw new ArgumentException(
                $"Cannot detect file format for '{filePath}'. " +
                $"Supported formats: {FileFormatDetector.SupportedFormatList}. " +
                "Use the explicit format: provider:name=path");
        provider = format.Provider;
        return new TableDescriptor(provider, name, filePath, options, format.Compression);
    }

    return new TableDescriptor(provider, name, filePath, options);
}

static async Task<int> RunSchemaAsync(QueryExpression query, TableCatalog catalog)
{
    if (query is not SelectQueryExpression selectQuery)
    {
        Console.Error.WriteLine("Error: 'schema' command requires a simple SELECT statement.");
        return 1;
    }

    FunctionRegistry functionRegistry = FunctionRegistry.CreateDefault();
    VirtualSchemaRegistry virtualSchemaRegistry = VirtualSchemaRegistry.CreateDefault();
    QuerySchemaResolver resolver = new(catalog, functionRegistry, virtualSchemaRegistry);
    ResolvedQuerySchema schema = await resolver.ResolveAsync(selectQuery.Statement, CancellationToken.None);

    // Print header.
    Console.WriteLine($"{"Column",-30} {"Type",-12} {"Nullable",-10} {"Source"}");
    Console.WriteLine(new string('-', 70));

    // Print each column.
    foreach (ResolvedColumn column in schema.Columns)
    {
        Console.WriteLine(
            $"{column.ColumnName,-30} {column.Kind,-12} {(column.Nullable ? "YES" : "NO"),-10} {column.SourceTableOrAlias ?? ""}");
    }

    Console.WriteLine($"\n({schema.Columns.Count} column(s) from {schema.TableNames.Count()} source(s))");
    return 0;
}

// ═══════════════════════════════════════════════════════════════
//  gRPC-routed command implementations
// ═══════════════════════════════════════════════════════════════

static QueryRequest BuildQueryRequest(
    string sessionId, string contextId, string sql,
    Dictionary<string, DataValueMessage>? parameters = null,
    long maxRows = 0)
{
    QueryRequest request = new()
    {
        SessionId = sessionId,
        ContextId = contextId,
        Sql = sql,
        MaxRows = maxRows,
    };

    if (parameters is not null)
    {
        foreach (KeyValuePair<string, DataValueMessage> entry in parameters)
        {
            request.Parameters[entry.Key] = entry.Value;
        }
    }

    return request;
}

static async Task<int> RunQueryViaGrpcAsync(
    GrpcClient client,
    string sessionId,
    string contextId,
    CliOptions options,
    Dictionary<string, DataValueMessage>? parameters)
{
    for (int iteration = 0; iteration < options.Repeat; iteration++)
    {
        if (options.Repeat > 1)
        {
            Console.Error.WriteLine($"--- Iteration {iteration + 1}/{options.Repeat} ---");
        }

        int result = await RunSingleQueryViaGrpcAsync(client, sessionId, contextId, options, parameters);
        if (result != 0) return result;
    }

    return 0;
}

static async Task<int> RunSingleQueryViaGrpcAsync(
    GrpcClient client,
    string sessionId,
    string contextId,
    CliOptions options,
    Dictionary<string, DataValueMessage>? parameters)
{
    // Parse SQL locally to extract INTO clause (server ignores it during planning).
    QueryExpression query = SqlParser.Parse(options.Sql);
    IntoClause? intoClause = ExtractIntoClause(query);

    QueryRequest request = BuildQueryRequest(sessionId, contextId, options.Sql, parameters);
    AsyncServerStreamingCall<QueryResult> call = client.Query(request);
    GrpcQueryResult grpcResult = await GrpcResultAdapter.ReadQueryAsync(call);

    ProgressReporter progress = new();

    if (options.Checkpoint && intoClause?.Shard is null)
    {
        Console.Error.WriteLine("Warning: --checkpoint requires SHARD ON; checkpointing disabled.");
    }

    if (intoClause is not null)
    {
        IOutputWriter outputWriter = CreateOutputWriter(intoClause);
        await using IOutputWriter writer = outputWriter;

        bool schemaInitialized = false;
        await foreach (RowBatch batch in grpcResult.Rows)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                if (!schemaInitialized)
                {
                    Schema schema = InferSchema(row);
                    await writer.InitializeAsync(schema);
                    schemaInitialized = true;
                }

                await writer.WriteRowAsync(row);
                progress.ReportRow();
            }
            batch.Return();
        }

        if (!schemaInitialized)
        {
            Console.WriteLine("No rows produced by query.");
            return 0;
        }

        OutputSummary summary = await writer.FinalizeAsync();
        progress.WriteSummary();
        Console.WriteLine($"Output: {summary.FilesCreated.Count} file(s), {summary.BytesWritten:N0} bytes");

        foreach (string file in summary.FilesCreated)
        {
            Console.WriteLine($"  {file}");
        }
    }
    else
    {
        await foreach (RowBatch batch in grpcResult.Rows)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                PrintRow(row);
                progress.ReportRow();
            }
            batch.Return();
        }
        progress.WriteSummary();
    }

    return 0;
}

static async Task<int> RunExploreViaGrpcAsync(
    GrpcClient client,
    string sessionId,
    string contextId,
    string sql,
    int limit,
    Dictionary<string, DataValueMessage>? parameters)
{
    QueryRequest request = BuildQueryRequest(sessionId, contextId, sql, parameters, maxRows: limit);
    AsyncServerStreamingCall<QueryResult> call = client.Query(request);
    GrpcQueryResult grpcResult = await GrpcResultAdapter.ReadQueryAsync(call);

    int count = 0;
    bool headerPrinted = false;
    Stopwatch stopwatch = Stopwatch.StartNew();

    await foreach (RowBatch batch in grpcResult.Rows)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            Row row = batch[i];
            if (!headerPrinted)
            {
                PrintHeader(row);
                headerPrinted = true;
            }

            PrintRow(row);
            count++;
        }
        batch.Return();
    }

    stopwatch.Stop();
    Console.WriteLine($"\n{count} row(s) in {stopwatch.Elapsed.TotalSeconds:F2} second(s)");
    return 0;
}

static async Task<int> RunStatsViaGrpcAsync(
    GrpcClient client,
    string sessionId,
    string contextId,
    string sql,
    Dictionary<string, DataValueMessage>? parameters)
{
    QueryRequest request = BuildQueryRequest(sessionId, contextId, sql, parameters);
    AsyncServerStreamingCall<QueryResult> call = client.Query(request);
    GrpcQueryResult grpcResult = await GrpcResultAdapter.ReadQueryAsync(call);

    StatisticsCollector collector = new();
    using Arena statsArena = new(); // TODO: remove when CLI is refactored
    ProgressReporter progress = new();

    await foreach (RowBatch batch in grpcResult.Rows)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            Row row = batch[i];
            collector.AddRow(row, statsArena);
            progress.ReportRow();
        }
        batch.Return();
    }

    progress.WriteSummary();
    Console.WriteLine();

    IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();

    foreach (KeyValuePair<string, ColumnStatistics> entry in stats)
    {
        Console.WriteLine($"Column: {entry.Key}");

        foreach (KeyValuePair<string, StatisticResult> stat in entry.Value.Results)
        {
            Console.WriteLine($"  {stat.Key}: {FormatStatResult(stat.Value)}");
        }

        Console.WriteLine();
    }

    return 0;
}

static async Task<int> RunExplainViaGrpcAsync(
    GrpcClient client,
    string sessionId,
    string contextId,
    string sql,
    bool analyze)
{
    ExplainResponse response = await client.ExplainAsync(new ExplainRequest
    {
        SessionId = sessionId,
        Sql = sql,
        Analyze = analyze,
        ContextId = contextId,
    });

    if (response.Root is not null)
    {
        ExplainPlanNode plan = ProtoConverter.FromProto(response.Root);
        Console.WriteLine(plan.Render());
    }
    else
    {
        Console.WriteLine(response.PlanText);
    }

    return 0;
}

static async Task<int> RunManifestViaGrpcAsync(
    GrpcClient client,
    string sessionId,
    string contextId,
    string sql,
    string? outputPath,
    Dictionary<string, DataValueMessage>? parameters)
{
    QueryRequest request = BuildQueryRequest(sessionId, contextId, sql, parameters);
    AsyncServerStreamingCall<QueryResult> call = client.Query(request);
    GrpcQueryResult grpcResult = await GrpcResultAdapter.ReadQueryAsync(call);

    StatisticsCollector collector = new();
    using Arena statsArena2 = new(); // TODO: remove when CLI is refactored
    ColumnInteractionCollector interactionCollector = new();
    ProgressReporter progress = new();
    Dictionary<string, DataKind> columnKinds = new();
    long rowCount = 0;

    await foreach (RowBatch batch in grpcResult.Rows)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            Row row = batch[i];
            if (rowCount == 0)
            {
                foreach (string columnName in row.ColumnNames)
                {
                    columnKinds[columnName] = row[columnName].Kind;
                }
            }

            collector.AddRow(row, statsArena2);
            interactionCollector.AddRow(row);
            rowCount++;
            progress.ReportRow();
        }
        batch.Return();
    }

    progress.WriteSummary();

    IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
    IReadOnlyList<ColumnInteractionResult> interactions = interactionCollector.GetInteractions();
    QueryResultsManifest manifest = ManifestBuilder.Build(stats, columnKinds, rowCount, interactions);
    SourceManifest sourceManifest = SourceManifest.Create("result", manifest);
    string json = ManifestSerializer.Serialize(sourceManifest);

    if (outputPath is not null)
    {
        await File.WriteAllTextAsync(outputPath, json);
        Console.WriteLine($"Manifest written to: {outputPath}");
    }
    else
    {
        Console.WriteLine(json);
    }

    return 0;
}

static async Task<int> RunManifestSchemaAsync(TableCatalog catalog, string? outputPath)
{
    FunctionRegistry functionRegistry = FunctionRegistry.CreateDefault();

    // Resolve table schemas from all registered sources.
    List<TableSchemaEntry> tableEntries = new();

    foreach (string tableName in catalog.TableNames)
    {
        Schema tableSchema = await catalog.GetSchemaAsync(tableName, CancellationToken.None);
        List<TableColumnEntry> columns = new();

        foreach (ColumnInfo column in tableSchema.Columns)
        {
            columns.Add(new TableColumnEntry
            {
                Name = column.Name,
                Kind = column.Kind.ToString(),
                Nullable = column.Nullable,
            });
        }

        tableEntries.Add(new TableSchemaEntry
        {
            Name = tableName,
            Columns = columns,
        });
    }

    // Collect function documentation.
    List<FunctionSignature> functions = new(FunctionDocumentation.All);

    // Collect keywords — the authoritative list lives in Program.ManifestKeywords.cs
    // and is sync-tested against MonarchGrammarFactory.ClauseKeywords().
    List<string> keywords = new(Program.ManifestKeywords());

    LanguageServerManifest manifest = new()
    {
        Tables = tableEntries,
        Functions = functions,
        Keywords = keywords,
    };

    if (outputPath is not null)
    {
        await LanguageServerManifestSerializer.WriteToFileAsync(manifest, outputPath);
        Console.WriteLine($"Language server manifest written to: {outputPath}");
    }
    else
    {
        string json = LanguageServerManifestSerializer.Serialize(manifest);
        Console.WriteLine(json);
    }

    Console.Error.WriteLine($"({tableEntries.Count} table(s), {functions.Count} function(s), {keywords.Count} keyword(s))");
    return 0;
}

static IOutputWriter CreateOutputWriter(IntoClause into)
{
    if (into.Shard is not null)
    {
        DatumIngest.Output.ShardMode mode = into.Shard.Mode switch
        {
            DatumIngest.Parsing.Ast.ShardMode.SampleCount => DatumIngest.Output.ShardMode.SampleCount,
            DatumIngest.Parsing.Ast.ShardMode.ByteSize => DatumIngest.Output.ShardMode.ByteSize,
            _ => throw new ArgumentException($"Unknown shard mode: {into.Shard.Mode}")
        };

        ShardStrategy strategy = new(mode, into.Shard.Value);

        return new ShardingOutputWriter(
            path => CreateBaseWriter(into.Format, path),
            strategy,
            into.Path);
    }

    return CreateBaseWriter(into.Format, into.Path);
}

static IOutputWriter CreateBaseWriter(OutputFormat format, string path)
{
    return format switch
    {
        OutputFormat.Csv => new CsvOutputWriter(path),
        OutputFormat.Hdf5 => new Hdf5OutputWriter(path),
        OutputFormat.Parquet => new ParquetOutputWriter(path),
        OutputFormat.Datum => new DatumOutputWriter(path),
        _ => throw new ArgumentException($"Unsupported output format: {format}")
    };
}

// Extracts the INTO clause from a query expression, regardless of whether it is
// a simple SELECT or a compound set operation.
static IntoClause? ExtractIntoClause(QueryExpression query)
{
    return query switch
    {
        SelectQueryExpression select => select.Statement.Into,
        CompoundQueryExpression compound => compound.Into,
        _ => null,
    };
}

static Schema InferSchema(Row row)
{
    string[] names = new string[row.FieldCount];
    for (int i = 0; i < row.FieldCount; i++)
    {
        names[i] = row.ColumnNames[i];
    }

    ColumnNameResolver.DeduplicateNames(names, aliasedPositions: null);

    ColumnInfo[] columns = new ColumnInfo[row.FieldCount];
    for (int i = 0; i < row.FieldCount; i++)
    {
        DataValue value = row[i];
        columns[i] = new ColumnInfo(names[i], value.Kind, value.IsNull);
    }

    return new Schema(columns);
}

static void PrintHeader(Row row)
{
    Console.WriteLine(string.Join("\t", row.ColumnNames));
    Console.WriteLine(new string('-', row.ColumnNames.Count * 16));
}

static void PrintRow(Row row)
{
    List<string> values = new();
    for (int i = 0; i < row.FieldCount; i++)
    {
        DataValue value = row[i];
        values.Add(value.IsNull ? "NULL" : FormatValue(value));
    }

    Console.WriteLine(string.Join("\t", values));
}

static string FormatValue(DataValue value) => value.ToDisplayString();

static string FormatStatResult(StatisticResult result)
{
    return result.Value?.ToString() ?? "N/A";
}

/// <summary>
/// Catalog file entry for JSON deserialization.
/// </summary>
internal sealed class CatalogEntry
{
    public string Provider { get; set; } = "";
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public Dictionary<string, string>? Options { get; set; }
}

[JsonSerializable(typeof(CatalogEntry[]))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class CatalogJsonContext : JsonSerializerContext
{
}
