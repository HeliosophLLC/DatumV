# Programmatic API

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Language Server](language-server.md) · [Compute Backend](compute.md)

DatumIngest exposes a C# API for embedding query execution, schema resolution, manifest generation, and checkpointing into .NET applications.

## Manifest

### Generation

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

### Loading & Registration

Manifests can be deserialized from JSON and registered in the `TableCatalog` for use by the query planner and cost model:

```csharp
string json = await File.ReadAllTextAsync("data.csv.datum-manifest");
QueryResultsManifest? manifest = ManifestSerializer.Deserialize(json);
if (manifest is not null)
    catalog.RegisterManifest("data", manifest);
```

When a manifest is registered, the query planner uses it to:
- Override the scan operator's estimated row count with the manifest's authoritative `RowCount` (useful for CSV, JSON, JSONL, and ZIP sources that cannot report row counts from metadata alone).
- Attach per-column `FeatureManifest` statistics to the `ScanOperator` for downstream cardinality estimation.

### Sidecar Auto-Discovery

Both the CLI and the gRPC compute backend automatically discover `.datum-manifest` sidecar files (named `{source-file}.datum-manifest`) alongside registered sources. The `.source` interactive command also checks for a sidecar manifest when adding a source at runtime.

## EXPLAIN

```csharp
// Static explain (includes estimated row counts when provider reports them)
IQueryOperator plan = planner.Plan(statement);
ExplainPlanNode explainPlan = QueryExplainer.Explain(plan);
Console.WriteLine(explainPlan.Render());
// Output includes ~N rows annotations per operator node, e.g.:
//   Filter (predicate: x > 5)  ~3,300 rows
//     └─ Scan (table: data, provider: parquet, columns: [x])  ~10,000 rows

// EXPLAIN ANALYZE (adds actual row counts and timing)
InstrumentedOperator instrumented = InstrumentedOperator.InstrumentTree(plan);
await foreach (Row row in instrumented.ExecuteAsync(context)) { }
InstrumentedOperator.PopulateMetrics(explainPlan, instrumented);
Console.WriteLine(explainPlan.Render());
```

Estimated rows are available on any `ExplainPlanNode` via `node.EstimatedRows`. Providers that report row counts (Parquet, HDF5, IDX) produce base estimates directly; CSV, JSON, JSONL, and ZIP return `null` by default. When a `.datum-manifest` sidecar file is available, its `RowCount` overrides the provider's estimate — giving accurate base counts for all formats.

The estimates propagate through filter, join, sort, limit, and projection operators. When manifest column statistics are available, the cost model uses them for data-driven selectivity:

| Predicate | With Manifest | Default Heuristic |
|-----------|---------------|-------------------|
| `column = value` | 1 / NDV | 10% |
| `column != value` | 1 − 1/NDV | 90% |
| `column IS NULL` | actual null ratio | 10% |
| `column IS NOT NULL` | 1 − null ratio | 90% |
| `column IN (a, b, c)` | count × 1/NDV | count × 10% |
| equi-join `a.x = b.x` | left × right / max(NDV_left, NDV_right) | left × right × 10% |
| `<`, `>`, `BETWEEN`, `LIKE` | 33% (default) | 33% |
| `AND` | product of child selectivities | product of child selectivities |
| `OR` | s₁ + s₂ − s₁×s₂ | s₁ + s₂ − s₁×s₂ |

NDV is the estimated distinct count from the manifest's HyperLogLog sketch (±2% accuracy).

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

## Auto-detecting table registration

The `Register` overloads detect the file format automatically from extension, filename pattern, or magic bytes:

```csharp
TableCatalog catalog = new();
catalog.RegisterProvider("csv", () => new CsvTableProvider());
catalog.RegisterProvider("idx", () => new IdxTableProvider());
catalog.RegisterProvider("parquet", () => new ParquetTableProvider());

// Auto-detect from file extension
catalog.Register("data", "./iris.csv");

// MNIST-style IDX files detected from filename pattern
catalog.Register("images", "./train-images-idx3-ubyte");

// Auto-detect with provider-specific options
catalog.Register("data", "./data.tsv", new Dictionary<string, string> { ["delimiter"] = "\t" });

// Explicit TableDescriptor still works as an override
catalog.Register(new TableDescriptor("csv", "data", "./data.csv", new()));
```

When the format cannot be determined, `Register` throws `ArgumentException` with a message listing supported formats. See [Providers — Format auto-detection](providers.md#format-auto-detection) for the full detection rules.

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
