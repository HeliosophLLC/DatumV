using System.Text.Json;
using System.Text.Json.Serialization;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Cli;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Output;
using DatumIngest.Output.Checkpoint;
using CheckpointFingerprint = DatumIngest.Output.Checkpoint.SourceFingerprint;
using DatumIngest.Manifest;
using DatumIngest.Output.Writers;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Interactions;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

try
{
    CliOptions options = CliOptions.Parse(args);
    TableCatalog catalog = BuildCatalog(options);

    // Load any pre-built indexes.
    LoadIndexes(catalog, options);

    // Commands that do not require SQL.
    if (options.Command == "index")
    {
        return await RunIndexAsync(catalog, options);
    }

    if (options.Command == "manifest-schema")
    {
        return await RunManifestSchemaAsync(catalog, options.OutputPath);
    }

    SelectStatement statement = SqlParser.Parse(options.Sql);

    return options.Command switch
    {
        "query" => await RunQueryAsync(statement, catalog, options),
        "explore" => await RunExploreAsync(statement, catalog, options.Limit),
        "stats" => await RunStatsAsync(statement, catalog),
        "explain" => await RunExplainAsync(statement, catalog, options.Analyze),
        "manifest" => await RunManifestAsync(statement, catalog, options.OutputPath),
        "schema" => await RunSchemaAsync(statement, catalog),
        _ => throw new ArgumentException($"Unknown command: {options.Command}. Use 'query', 'explore', 'stats', 'explain', 'manifest', 'manifest-schema', 'schema', or 'index'.")
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

    // Register all built-in providers
    catalog.RegisterProvider("csv", () => new CsvTableProvider());
    catalog.RegisterProvider("json", () => new JsonTableProvider());
    catalog.RegisterProvider("jsonl", () => new JsonlTableProvider());
    catalog.RegisterProvider("zip", () => new ZipTableProvider());
    catalog.RegisterProvider("hdf5", () => new Hdf5TableProvider());
    catalog.RegisterProvider("parquet", () => new ParquetTableProvider());
    catalog.RegisterProvider("idx", () => new IdxTableProvider());

    // Load catalog file if specified
    if (options.CatalogPath is not null)
    {
        LoadCatalogFile(catalog, options.CatalogPath);
    }

    // Parse inline --source definitions (override same-named catalog entries)
    foreach (string source in options.Sources)
    {
        TableDescriptor descriptor = ParseSourceDefinition(source);
        catalog.Register(descriptor);
    }

    return catalog;
}

static void LoadIndexes(TableCatalog catalog, CliOptions options)
{
    IndexReader reader = new();

    foreach (string indexPath in options.IndexPaths)
    {
        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException($"Index file not found: {indexPath}");
        }

        using FileStream stream = File.OpenRead(indexPath);
        SourceIndex index = reader.Read(stream);

        // Derive table name from the index file path by stripping the .datum-index suffix.
        string fileName = Path.GetFileName(indexPath);
        string tableName = fileName.EndsWith(".datum-index", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".datum-index".Length]
            : Path.GetFileNameWithoutExtension(fileName);

        // If a table with this name is registered, attach the index.
        // Otherwise check if stripping one more extension matches (e.g. "data.parquet.datum-index" → "data").
        if (catalog.TryResolve(tableName, out _))
        {
            catalog.RegisterIndex(tableName, index);
        }
        else
        {
            string baseName = Path.GetFileNameWithoutExtension(tableName);

            if (catalog.TryResolve(baseName, out _))
            {
                catalog.RegisterIndex(baseName, index);
            }
            else
            {
                // Register with the derived name; the user may reference it later.
                catalog.RegisterIndex(tableName, index);
            }
        }
    }
}

static async Task<int> RunIndexAsync(TableCatalog catalog, CliOptions options)
{
    if (options.Sources.Count == 0)
    {
        throw new ArgumentException("The 'index' command requires at least one --source definition.");
    }

    HashSet<string>? bloomColumns = options.BloomColumns.Count > 0 ? options.BloomColumns : null;
    HashSet<string>? indexColumns = options.IndexColumns.Count > 0 ? options.IndexColumns : null;
    SourceIndexBuilder builder = new(options.ChunkSize, bloomColumns, indexColumns);

    foreach (string source in options.Sources)
    {
        TableDescriptor descriptor = ParseSourceDefinition(source);

        if (!catalog.TryResolve(descriptor.Name, out _))
        {
            catalog.Register(descriptor);
        }

        ITableProvider provider = catalog.CreateProvider(descriptor);

        Stream? sourceStream = null;

        if (File.Exists(descriptor.FilePath))
        {
            sourceStream = File.OpenRead(descriptor.FilePath);
        }

        try
        {
            SourceIndex index = await builder.BuildAsync(
                descriptor, provider, sourceStream, CancellationToken.None);

            string indexPath = descriptor.FilePath + ".datum-index";
            using FileStream outputStream = File.Create(indexPath);
            IndexWriter writer = new();
            writer.Write(index, outputStream);

            Console.WriteLine($"Index created: {indexPath}");
            Console.WriteLine($"  Schema: {index.Schema.Schema.Columns.Count} columns, {index.Schema.TotalRowCount} rows");
            Console.WriteLine($"  Chunks: {index.Chunks.Count} (chunk size: {options.ChunkSize})");

            if (index.BloomFilters is not null)
            {
                Console.WriteLine($"  Bloom filters: {string.Join(", ", index.BloomFilters.ColumnNames)}");
            }

            if (index.SortedIndexes is not null)
            {
                Console.WriteLine($"  Sorted indexes: {string.Join(", ", index.SortedIndexes.ColumnNames)}");
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

    return 0;
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

static TableDescriptor ParseSourceDefinition(string source)
{
    // Format: provider:name=path[;key=value;...]
    int colonIndex = source.IndexOf(':');
    if (colonIndex < 0)
    {
        throw new ArgumentException($"Invalid source format: '{source}'. Expected format: provider:name=path[;key=value]");
    }

    string provider = source[..colonIndex];
    string remainder = source[(colonIndex + 1)..];

    int equalsIndex = remainder.IndexOf('=');
    if (equalsIndex < 0)
    {
        throw new ArgumentException($"Invalid source format: '{source}'. Expected format: provider:name=path[;key=value]");
    }

    string name = remainder[..equalsIndex];
    string pathAndOptions = remainder[(equalsIndex + 1)..];

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

    return new TableDescriptor(provider, name, filePath, options);
}

static async Task<int> RunQueryAsync(SelectStatement statement, TableCatalog catalog, CliOptions options)
{
    FunctionRegistry functionRegistry = FunctionRegistry.CreateDefault();
    QueryPlanner planner = new(catalog, functionRegistry);
    IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);

    ExecutionContext context = new(
        CancellationToken.None,
        functionRegistry,
        catalog);

    ProgressReporter progress = new();

    // Checkpoint setup
    bool checkpointEnabled = options.Checkpoint && statement.Into?.Shard is not null;
    CheckpointManager? checkpointManager = null;
    IReadOnlyList<CheckpointFingerprint>? sourceFingerprints = null;
    int startShardIndex = 0;

    if (options.Checkpoint && statement.Into?.Shard is null)
    {
        Console.Error.WriteLine("Warning: --checkpoint requires SHARD ON; checkpointing disabled.");
    }

    if (checkpointEnabled && statement.Into is not null)
    {
        checkpointManager = new CheckpointManager(statement.Into.Path);
        sourceFingerprints = SourceFingerprintCollector.Collect(catalog);

        IReadOnlyList<CheckpointMarker> existingCheckpoints =
            await checkpointManager.ScanExistingCheckpointsAsync();

        if (existingCheckpoints.Count > 0)
        {
            // Validate source fingerprints against the first checkpoint's fingerprints
            string? mismatch = SourceFingerprintCollector.Validate(
                existingCheckpoints[0].SourceFingerprints, sourceFingerprints);

            if (mismatch is not null)
            {
                Console.Error.WriteLine($"Error: Source data has changed since last run. {mismatch}");
                Console.Error.WriteLine("Delete existing checkpoint files to start fresh.");
                return 1;
            }

            ResumeState resumeState = CheckpointManager.ComputeResumeState(existingCheckpoints);
            startShardIndex = resumeState.NextShardIndex;

            // Delete orphaned shard file at the resume point (partial write from crash)
            checkpointManager.DeleteOrphanedShard(resumeState.NextShardIndex);

            if (resumeState.RowsToSkip > 0)
            {
                Console.WriteLine(
                    $"Resuming from shard {resumeState.NextShardIndex} (skipping {resumeState.RowsToSkip:N0} rows)");
                plan = new SkipOperator(plan, resumeState.RowsToSkip);
            }
        }
    }

    if (statement.Into is not null)
    {
        IOutputWriter outputWriter = checkpointEnabled
            ? CreateCheckpointedOutputWriter(statement.Into, checkpointManager!, sourceFingerprints!, startShardIndex)
            : CreateOutputWriter(statement.Into);
        await using IOutputWriter writer = outputWriter;

        // Infer schema from first row, write all rows
        bool schemaInitialized = false;
        await foreach (Row row in plan.ExecuteAsync(context))
        {
            if (!schemaInitialized)
            {
                Schema schema = InferSchema(row);
                await writer.InitializeAsync(schema);
                schemaInitialized = true;
            }

            await writer.WriteRowAsync(row);
            progress.ReportRow();
        }

        if (!schemaInitialized)
        {
            Console.WriteLine("No rows produced by query.");
            return 0;
        }

        OutputSummary summary = await writer.FinalizeAsync();

        // Clean up checkpoint files after successful completion
        if (checkpointEnabled && writer is ShardingOutputWriter shardingWriter)
        {
            shardingWriter.CleanupCheckpoints();
        }

        progress.WriteSummary();
        Console.WriteLine($"Output: {summary.FilesCreated.Count} file(s), {summary.BytesWritten:N0} bytes");

        foreach (string file in summary.FilesCreated)
        {
            Console.WriteLine($"  {file}");
        }
    }
    else
    {
        // No INTO clause: print rows to stdout
        await foreach (Row row in plan.ExecuteAsync(context))
        {
            PrintRow(row);
            progress.ReportRow();
        }
        progress.WriteSummary();
    }

    return 0;
}

static async Task<int> RunExploreAsync(SelectStatement statement, TableCatalog catalog, int limit)
{
    FunctionRegistry functionRegistry = FunctionRegistry.CreateDefault();
    QueryPlanner planner = new(catalog, functionRegistry);
    IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);

    ExecutionContext context = new(
        CancellationToken.None,
        functionRegistry,
        catalog);

    int count = 0;
    bool headerPrinted = false;

    await foreach (Row row in plan.ExecuteAsync(context))
    {
        if (!headerPrinted)
        {
            PrintHeader(row);
            headerPrinted = true;
        }

        PrintRow(row);
        count++;

        if (count >= limit)
        {
            break;
        }
    }

    Console.WriteLine($"\n({count} row(s))");
    return 0;
}

static async Task<int> RunStatsAsync(SelectStatement statement, TableCatalog catalog)
{
    FunctionRegistry functionRegistry = FunctionRegistry.CreateDefault();
    QueryPlanner planner = new(catalog, functionRegistry);
    IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);

    ExecutionContext context = new(
        CancellationToken.None,
        functionRegistry,
        catalog);

    StatisticsCollector collector = new();
    ProgressReporter progress = new();

    await foreach (Row row in plan.ExecuteAsync(context))
    {
        collector.AddRow(row);
        progress.ReportRow();
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

static async Task<int> RunExplainAsync(SelectStatement statement, TableCatalog catalog, bool analyze)
{
    FunctionRegistry functionRegistry = FunctionRegistry.CreateDefault();
    QueryPlanner planner = new(catalog, functionRegistry);
    IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);

    // Build the static explain plan from the original operator tree.
    ExplainPlanNode explainPlan = QueryExplainer.Explain(plan);

    if (analyze)
    {
        // Wrap the tree with instrumented operators, execute, and collect metrics.
        InstrumentedOperator instrumentedRoot = InstrumentedOperator.InstrumentTree(plan);

        ExecutionContext context = new(
            CancellationToken.None,
            functionRegistry,
            catalog);

        // Consume all rows to collect timing.
        await foreach (Row _ in instrumentedRoot.ExecuteAsync(context))
        {
        }

        InstrumentedOperator.PopulateMetrics(explainPlan, instrumentedRoot);
    }

    Console.WriteLine(explainPlan.Render());
    return 0;
}

static async Task<int> RunManifestAsync(SelectStatement statement, TableCatalog catalog, string? outputPath)
{
    FunctionRegistry functionRegistry = FunctionRegistry.CreateDefault();
    QueryPlanner planner = new(catalog, functionRegistry);
    IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);

    ExecutionContext context = new(
        CancellationToken.None,
        functionRegistry,
        catalog);

    StatisticsCollector collector = new();
    ColumnInteractionCollector interactionCollector = new();
    ProgressReporter progress = new();
    Dictionary<string, DataKind> columnKinds = new();
    long rowCount = 0;

    await foreach (Row row in plan.ExecuteAsync(context))
    {
        // Capture column kinds from the first row
        if (rowCount == 0)
        {
            foreach (string columnName in row.ColumnNames)
            {
                columnKinds[columnName] = row[columnName].Kind;
            }
        }

        collector.AddRow(row);
        interactionCollector.AddRow(row);
        rowCount++;
        progress.ReportRow();
    }

    progress.WriteSummary();

    IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
    IReadOnlyList<ColumnInteractionResult> interactions = interactionCollector.GetInteractions();
    QueryResultsManifest manifest = ManifestBuilder.Build(stats, columnKinds, rowCount, interactions);
    string json = ManifestSerializer.Serialize(manifest);

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

static async Task<int> RunSchemaAsync(SelectStatement statement, TableCatalog catalog)
{
    FunctionRegistry functionRegistry = FunctionRegistry.CreateDefault();
    QuerySchemaResolver resolver = new(catalog, functionRegistry);
    ResolvedQuerySchema schema = await resolver.ResolveAsync(statement, CancellationToken.None);

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

    // Collect keywords from the SQL token enum.
    List<string> keywords = new()
    {
        "SELECT", "FROM", "WHERE", "JOIN", "LEFT", "RIGHT", "FULL", "OUTER",
        "CROSS", "INNER", "ON", "INTO", "AS", "AND", "OR", "NOT", "IN",
        "BETWEEN", "LIKE", "IS", "NULL", "ORDER", "BY", "ASC", "DESC",
        "LIMIT", "OFFSET", "SHARD", "CAST", "TRUE", "FALSE",
    };

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

static IOutputWriter CreateCheckpointedOutputWriter(
    IntoClause into,
    CheckpointManager checkpointManager,
    IReadOnlyList<CheckpointFingerprint> sourceFingerprints,
    int startShardIndex)
{
    DatumIngest.Output.ShardMode mode = into.Shard!.Mode switch
    {
        DatumIngest.Parsing.Ast.ShardMode.SampleCount => DatumIngest.Output.ShardMode.SampleCount,
        DatumIngest.Parsing.Ast.ShardMode.ByteSize => DatumIngest.Output.ShardMode.ByteSize,
        _ => throw new ArgumentException($"Unknown shard mode: {into.Shard.Mode}")
    };

    ShardStrategy strategy = new(mode, into.Shard.Value);

    return new ShardingOutputWriter(
        path => CreateBaseWriter(into.Format, path),
        strategy,
        into.Path,
        checkpointManager,
        sourceFingerprints,
        startShardIndex);
}

static IOutputWriter CreateBaseWriter(OutputFormat format, string path)
{
    return format switch
    {
        OutputFormat.Csv => new CsvOutputWriter(path),
        OutputFormat.Hdf5 => new Hdf5OutputWriter(path),
        OutputFormat.Parquet => new ParquetOutputWriter(path),
        _ => throw new ArgumentException($"Unsupported output format: {format}")
    };
}

static Schema InferSchema(Row row)
{
    ColumnInfo[] columns = new ColumnInfo[row.FieldCount];
    for (int i = 0; i < row.FieldCount; i++)
    {
        string name = row.ColumnNames[i];
        DataValue value = row[i];
        columns[i] = new ColumnInfo(name, value.Kind, value.IsNull);
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

static string FormatValue(DataValue value)
{
    return value.Kind switch
    {
        DataKind.Scalar => value.AsScalar().ToString("G"),
        DataKind.UInt8 => value.AsUInt8().ToString(),
        DataKind.String => value.AsString(),
        DataKind.Date => value.AsDate().ToString("yyyy-MM-dd"),
        DataKind.DateTime => value.AsDateTime().ToString("O"),
        DataKind.JsonValue => value.AsJsonValue(),
        DataKind.Vector => $"[{string.Join(", ", value.AsVector().Select(v => v.ToString("G")))}]",
        _ => value.ToString() ?? ""
    };
}

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
