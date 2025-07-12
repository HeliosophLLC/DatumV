# Programmatic API

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Language Server](language-server.md)

DatumIngest exposes a C# API for embedding query execution, schema resolution, manifest generation, and checkpointing into .NET applications.

## Manifest

```csharp
StatisticsCollector collector = new();
ColumnInteractionCollector interactionCollector = new();
// ... feed rows ...

IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
IReadOnlyList<ColumnInteractionResult> interactions = interactionCollector.GetInteractions();
Dictionary<string, DataKind> kinds = new() { ["id"] = DataKind.Scalar, ["name"] = DataKind.String };

QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, rowCount, interactions);
string json = ManifestSerializer.Serialize(manifest);
await ManifestSerializer.WriteToFileAsync(manifest, "manifest.json");
```

## EXPLAIN

```csharp
// Static explain
IQueryOperator plan = planner.Plan(statement);
ExplainPlanNode explainPlan = QueryExplainer.Explain(plan);
Console.WriteLine(explainPlan.Render());

// EXPLAIN ANALYZE
InstrumentedOperator instrumented = InstrumentedOperator.InstrumentTree(plan);
await foreach (Row row in instrumented.ExecuteAsync(context)) { }
InstrumentedOperator.PopulateMetrics(explainPlan, instrumented);
Console.WriteLine(explainPlan.Render());
```

## Schema Introspection

```csharp
// 1. Parse the query.
SelectStatement statement = SqlParser.Parse(
    "SELECT * FROM images AS img JOIN captions AS cap ON img.id = cap.image_id");

// 2. Resolve the combined schema of all FROM/JOIN sources.
QuerySchemaResolver resolver = new(catalog, FunctionRegistry.CreateDefault());
ResolvedQuerySchema schema = await resolver.ResolveAsync(statement, cancellationToken);

// 3. Use for autocomplete.
foreach (ResolvedColumn column in schema.Columns)
{
    // column.ColumnName         → "file_name"
    // column.Kind               → DataKind.String
    // column.Nullable           → false
    // column.SourceTableOrAlias → "img"
}

// Qualified lookup (alias.column):
ResolvedColumn? col = schema.FindColumn("img.file_name");

// Unqualified lookup:
ResolvedColumn? col2 = schema.FindColumn("file_name");

// All columns from a specific table/alias:
IReadOnlyList<ResolvedColumn> imgColumns = schema.FindColumns("img");

// All contributing table names/aliases:
IEnumerable<string> tables = schema.TableNames;  // ["img", "cap"]
```

### Single-table convenience

For simple scenarios where you just need one table's schema:

```csharp
Schema schema = await catalog.GetSchemaAsync("tableName", cancellationToken);

foreach (ColumnInfo column in schema.Columns)
{
    // column.Name     → "score"
    // column.Kind     → DataKind.Scalar
    // column.Nullable → true
}
```

### Schema-aware table-valued functions

Custom table-valued functions can implement `ISchemaAwareTableFunction` to expose their output schema for introspection without execution:

```csharp
public class MyTableFunction : ISchemaAwareTableFunction
{
    public string Name => "my_func";

    public Schema GetOutputSchema(ReadOnlySpan<DataKind> argumentKinds)
    {
        return new Schema([new ColumnInfo("result", DataKind.Scalar, nullable: false)]);
    }

    public async IAsyncEnumerable<Row> ExecuteAsync(
        DataValue[] arguments, CancellationToken cancellationToken) { /* ... */ }
}
```

## Stream-based output

All three output writers accept a `Stream` instead of a file path, enabling purely in-memory pipelines:

```csharp
using MemoryStream stream = new();
await using CsvOutputWriter writer = new(stream);
await writer.InitializeAsync(plan.Schema);

await foreach (Row row in plan.ExecuteAsync(context))
{
    await writer.WriteRowAsync(row);
}

OutputSummary summary = await writer.FinalizeAsync();

// Stream now contains the CSV data.
stream.Position = 0;
```

Works with all formats — `CsvOutputWriter(stream)`, `ParquetOutputWriter(stream)`, `Hdf5OutputWriter(stream)`. The caller owns the stream; the writer leaves it open on dispose. In stream mode, `ParquetOutputWriter` embeds binary columns as `byte[]` directly instead of externalizing to an `images/` folder.

## Checkpointing

```csharp
CheckpointManager checkpointManager = new(outputPath);
IReadOnlyList<SourceFingerprint> fingerprints = SourceFingerprintCollector.Collect(catalog);

// Scan for existing checkpoints and compute resume state
IReadOnlyList<CheckpointMarker> checkpoints = await checkpointManager.ScanExistingCheckpointsAsync();
ResumeState resume = CheckpointManager.ComputeResumeState(checkpoints);

// Delete orphaned partial shard from a previous crash
checkpointManager.DeleteOrphanedShard(resume.NextShardIndex);

// Create a checkpoint-aware sharding writer
await using ShardingOutputWriter writer = new(
    path => new CsvOutputWriter(path),
    strategy,
    outputPath,
    checkpointManager,
    fingerprints,
    startShardIndex: resume.NextShardIndex);

// Wrap the plan with SkipOperator to fast-forward past completed rows
IQueryOperator resumedPlan = new SkipOperator(plan, resume.RowsToSkip);
```
