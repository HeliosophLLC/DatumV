# Programmatic API

DatumIngest exposes a C# API for embedding ingestion, query execution, schema resolution, manifest generation, and checkpointing into .NET applications.

## Manifest

### Generation

```csharp
StatisticsCollector collector = new();
ColumnInteractionCollector interactionCollector = new();
// ... feed rows ...

IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
IReadOnlyList<ColumnInteractionResult> interactions = interactionCollector.GetInteractions();
Dictionary<string, DataKind> kinds = new() { ["id"] = DataKind.Float32, ["name"] = DataKind.String };

QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, rowCount, interactions);
string json = ManifestSerializer.Serialize("data", manifest);
await ManifestSerializer.WriteToFileAsync("data", manifest, "manifest.json");
```

### Loading & Registration

Manifests are deserialized via `ManifestSerializer`, which returns a `SourceManifest` — a container keyed by table name that supports multi-table sources. Individual manifests are registered in the `TableCatalog` for use by the query planner and cost model:

```csharp
string json = await File.ReadAllTextAsync("data.csv.datum-manifest");
SourceManifest? sourceManifest = ManifestSerializer.Deserialize(json);

if (sourceManifest is not null
    && sourceManifest.Tables.TryGetValue("data", out QueryResultsManifest? manifest))
{
    catalog.RegisterManifest("data", manifest);
}
```

For the common single-table case, `ManifestSerializer.Serialize(tableName, manifest)` and `ManifestSerializer.WriteToFileAsync(tableName, manifest, path)` accept a table name and manifest directly.

When a manifest is registered, the query planner uses it to:
- Override the scan operator's estimated row count with the manifest's authoritative `RowCount` (useful for CSV, JSON, JSONL, and ZIP sources that cannot report row counts from metadata alone).
- Attach per-column `FeatureManifest` statistics to the `ScanOperator` for downstream cardinality estimation.

### Sidecar Auto-Discovery

After tables have been registered and expanded, call `catalog.DiscoverSidecars()` to auto-discover all three sidecar types alongside registered source files:

| Sidecar | Naming Convention | Contents |
|---------|-------------------|----------|
| `.datum-index` | `{source-file}.datum-index` | Binary source index (chunk statistics, bloom filters, B+Tree indexes, bitmap indexes) |
| `.datum-manifest` | `{source-file}.datum-manifest` | JSON feature manifest (per-column statistics, interactions) |
| `.datum-schema` | `{source-file}.datum-schema` | JSON schema cache (column names, data kinds, nullability) |
| `.datum-pkindex` | `{source-file}.datum-pkindex` | Mutable B+Tree backing a `PRIMARY KEY` constraint. Auto-managed by `DatumFileTableProviderV2` (created at `CREATE TABLE`, maintained on every `INSERT`); not consumed by `DiscoverSidecars`. |

```csharp
TableCatalog catalog = new();
catalog.Register("data", "./data.csv");
catalog.DiscoverSidecars();

// Sidecars are now registered — e.g.:
catalog.TryGetManifest("data", out QueryResultsManifest? manifest);
catalog.TryGetIndex("data", out SourceIndex? index);
catalog.TryGetSchema("data", out Schema? schema);
```

`DiscoverSidecars()` reads each sidecar file at most once per unique source file path and skips tables that already have a registered artifact. Tables sharing the same source file (e.g., multi-table JSON) all receive matching entries from a single sidecar read.

The CLI, gRPC compute backend, and `.source` interactive command all call `catalog.DiscoverSidecars()` after source registration.

## Source Analysis

`SourceAnalyzer` generates all three sidecar artifacts (schema, index, manifest) in a single pass over the source data. The result is a `SourceAnalysisResult` containing a `SourceSchema`, `SourceIndexSet`, and `SourceManifest`.

### Catalog-driven analysis

The simplest entry point analyzes all tables registered in a catalog:

```csharp
SourceAnalyzer analyzer = new(
    chunkSize: 10_000,
    bloomColumns: new HashSet<string> { "id" },
    indexColumns: new HashSet<string> { "id" },
    withInteractions: true);

SourceAnalysisResult result = await analyzer.AnalyzeAsync(catalog, CancellationToken.None);

// Write all three sidecars
string sourcePath = catalog.Resolve("data").FilePath;
await SchemaSerializer.WriteToFileAsync(result.Schema, sourcePath + ".datum-schema");
await ManifestSerializer.WriteToFileAsync(result.Manifest, sourcePath + ".datum-manifest");

using FileStream indexStream = File.Create(sourcePath + ".datum-index");
IndexWriter writer = new();
writer.Write(result.IndexSet, indexStream);
```

### Per-table analysis

For fine-grained control, pass explicit table/provider pairs:

```csharp
TableDescriptor descriptor = catalog.Resolve("data");
ITableProvider provider = catalog.CreateProvider(descriptor);

SourceAnalysisResult result = await analyzer.AnalyzeAsync(
    [(descriptor, provider)],
    sourceStream: null,
    CancellationToken.None);
```

### Result structure

```csharp
// SourceAnalysisResult is a sealed record with three fields:
SourceSchema schema = result.Schema;           // Per-table schemas
SourceIndexSet indexSet = result.IndexSet;      // Per-table indexes + source fingerprint
SourceManifest manifest = result.Manifest;      // Per-table column statistics

// Each container is keyed by table name:
Schema tableSchema = schema.Tables["data"];
SourceIndex tableIndex = indexSet.Tables["data"];
QueryResultsManifest tableManifest = manifest.Tables["data"];
```

## Ingestion

`DatumIngester` converts source files into `.datum` columnar format with statistics and a sample preview in a single streaming pass. Unlike `SourceAnalyzer` (which produces all three sidecar artifacts including indexes), `DatumIngester.IngestAsync` focuses on format conversion, statistics, and sample collection — indexing is a separate step via `BuildIndexAsync`.

### Basic usage

```csharp
await using DatumIngestionResult ingestion = await DatumIngester.IngestAsync("data.csv");

// Per-table results
DatumIngestionTableResult table = ingestion.Tables["data_csv"];
Stream datumStream = table.DatumStream;          // .datum binary, positioned at 0
Schema schema = table.Schema;                     // Discovered schema
SourceManifest manifest = table.Manifest;         // Column statistics
long rows = table.RowCount;                       // Total rows ingested
```

### Ingesting from an in-memory stream

```csharp
using MemoryStream source = new(csvBytes);
await using DatumIngestionResult ingestion = await DatumIngester.IngestAsync("upload.csv", source);
```

### Sample preview

During ingestion, 25 representative rows are collected via reservoir sampling (Algorithm R), producing a uniform random sample regardless of dataset size. The preview is available immediately after ingestion — no need to wait for indexing.

```csharp
await using DatumIngestionResult ingestion = await DatumIngester.IngestAsync("data.csv");

// Access the per-table sample preview
SamplePreview preview = ingestion.Samples["data_csv"];

// Features describe the column structure
foreach (SampleFeature feature in preview.Features)
{
    Console.WriteLine($"{feature.Name}: {feature.Kind}");
}

// Samples contains the row data as JSON-friendly primitives
foreach (object?[] row in preview.Samples)
{
    Console.WriteLine(string.Join(", ", row));
}
```

Sample values are converted to JSON-friendly representations:

| Data kind | JSON representation |
|-----------|---------------------|
| Float32, UInt8, Boolean | Number or boolean primitive |
| String, Date, DateTime, Time, Duration, Uuid | String (ISO 8601 for temporal types) |
| Vector | Flat numeric array `[1.0, 2.0, 3.0]` |
| Matrix | Nested array `[[1.0, 2.0], [3.0, 4.0]]` |
| Tensor | Recursively nested arrays following shape dimensions |
| Image | `"base64://…"` — resized to fit 64×64 max (aspect-preserving), re-encoded as PNG |
| UInt8Array | `"[binary data]"` sentinel string |
| Array | Recursively converted element array |

### Serialization

`SamplePreviewSerializer` reads and writes the preview as JSON:

```csharp
// Serialize to string
string json = SamplePreviewSerializer.Serialize(preview);

// Write to file
await SamplePreviewSerializer.WriteToFileAsync(preview, "data.csv.datum-sample");

// Deserialize
SamplePreview? loaded = SamplePreviewSerializer.Deserialize(json);
```

## Schema Serialization

`SchemaSerializer` reads and writes `.datum-schema` sidecar files. Like manifests, schemas are wrapped in a `SourceSchema` container keyed by table name:

```csharp
// Serialize a single-table schema
string json = SchemaSerializer.Serialize("data", schema);
await SchemaSerializer.WriteToFileAsync("data", schema, "data.csv.datum-schema");

// Serialize a multi-table schema
SourceSchema sourceSchema = result.Schema;
await SchemaSerializer.WriteToFileAsync(sourceSchema, "multi.json.datum-schema");

// Deserialize
SourceSchema? loaded = SchemaSerializer.Deserialize(json);
Schema? tableSchema = loaded?.Tables["data"];
```

When a `.datum-schema` sidecar is present, `GetSchemaAsync` returns the cached schema without invoking the provider — eliminating schema inference I/O (e.g., sampling the first 100 rows of a CSV).

## Executing SQL

The primary surface for running SQL against a `TableCatalog` is the ADO.NET-style trio in `DatumIngest.Data` — `InProcessDatumDbConnection`, `InProcessDatumDbCommand`, `InProcessDatumDbReader`. The connection is a thin handle over a catalog; the command holds SQL text plus a parameter collection; the reader streams rows.

```csharp
using DatumIngest.Catalog;
using DatumIngest.Data;

using InProcessDatumDbConnection connection = new(catalog);
using InProcessDatumDbCommand command = connection.CreateCommand(
    "SELECT id, name FROM users WHERE id = $id");
command.Parameters.AddInt64("id", 42);

await using InProcessDatumDbReader reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    long id = reader.GetInt64(0);
    string name = reader.GetString(1);
}
```

Three execute verbs cover the common shapes:

| Verb | Use for | Returns |
|------|---------|---------|
| `ExecuteReaderAsync()` | `SELECT`, `CALL`, `… RETURNING` DML | `InProcessDatumDbReader` |
| `ExecuteNonQueryAsync()` | DDL, no-RETURNING DML | `Task<int>` (rows-affected count, `-1` when unknown) |
| `ExecuteScalarAsync()` | first row, first column reads | `Task<DataValue?>` (`null` when result set is empty) |

The reader handles `RowBatch` lifecycle internally — consumers see row-at-a-time accessors (`GetInt32(ordinal)`, `GetString(ordinal)`, `IsDBNull(ordinal)`, etc.) and never touch a batch directly. Pool returns happen as the reader advances; disposing the reader fires the iterator's `finally` so the last batch returns too.

### Multi-statement scripts

`CommandText` accepts semicolon-separated SQL. The connection routes through `TableCatalog.PrepareAsync` which auto-detects single vs multi-statement and returns either a `StatementPlan` (single) or a `StatementBatch` (multi). The reader surfaces each child as its own result set:

```csharp
using InProcessDatumDbCommand command = connection.CreateCommand(
    "CREATE TABLE staging (id INT32 NOT NULL); " +
    "INSERT INTO staging VALUES (1), (2), (3); " +
    "SELECT id FROM staging ORDER BY id");

await using InProcessDatumDbReader reader = await command.ExecuteReaderAsync();
do
{
    while (await reader.ReadAsync())
    {
        // Rows from the current result set; trailing SELECT here yields 1/2/3.
    }
}
while (await reader.NextResultAsync());
```

Children are planned lazily — the `INSERT` plans against the `CREATE TABLE` that already ran, so state dependencies across statement boundaries work.

### Without ADO surface

For procedural batches that share a `BatchContext` across many plans, or for callers that already hold a parsed `Statement`, the catalog exposes the lower-level surface directly:

```csharp
// Single statement (throws on multi-statement SQL)
StatementPlan plan = await catalog.PlanAsync("CREATE TABLE t (id INT32 NOT NULL)");
await catalog.ExecuteAsync(plan).DrainAsync(); // applies side effect

// Auto-detect single vs multi-statement
PreparedSql prepared = await catalog.PrepareAsync(sql);
```

`PlanAsync` is the building block. The returned `StatementPlan` is lazy — reading `plan.ExplainTree` does not apply the side effect; iterating `plan.ExecuteAsync(ct, batchContext)` does.

## Parameter Binding

Parameterized queries use `$name` placeholders. Parameters are bound at plan time via an AST rewrite — `ParameterExpression` nodes are replaced with literal values before the planner runs, so all downstream optimizations apply.

The primary way to bind is through the command's `Parameters` collection:

```csharp
using InProcessDatumDbCommand command = connection.CreateCommand(
    "SELECT * FROM data WHERE score > $threshold AND category = $cat");
command.Parameters
    .AddFloat64("threshold", 0.5)
    .AddString("cat", "electronics");

await using InProcessDatumDbReader reader = await command.ExecuteReaderAsync();
```

Typed `AddInt32`/`AddInt64`/`AddString`/`AddBoolean`/`AddFloat64` helpers accept nullable values; passing `null` binds SQL NULL of the appropriate kind. For other kinds, `Add(name, DataValue)` accepts any pre-built `DataValue`.

For lower-level callers driving `TableCatalog.PlanAsync` directly, `ParameterBinder.Bind(Statement, IReadOnlyDictionary<string, ParameterValue>)` is the underlying rewrite. It validates that every referenced placeholder is supplied and every supplied parameter is referenced; either gap throws `ArgumentException` with a diagnostic message.

### CLI string parsing

`ParameterValueParser.Parse` converts a raw string value (as provided by `--param key=value`) into a `DataValue` with automatic type inference:

```csharp
DataValue value = ParameterValueParser.Parse("42");       // Float32(42)
DataValue flag = ParameterValueParser.Parse("true");      // Boolean(true)
DataValue text = ParameterValueParser.Parse("hello");     // String("hello")
DataValue nil = ParameterValueParser.Parse("null");       // Null
```

## EXPLAIN

Every plan exposes a static `ExplainTree` populated at construction. Reading it does not iterate the plan or apply any side effect:

```csharp
StatementPlan plan = await catalog.PlanAsync(
    "SELECT id FROM data WHERE x > 5");
Console.WriteLine(plan.ExplainTree.Render());
// Filter (predicate: x > 5)  ~3,300 rows
//   └─ Scan (table: data, provider: parquet, columns: [x])  ~10,000 rows
```

Per-operator row estimates use provider-reported counts when available (Parquet, HDF5, IDX) and fall back to a `.datum-manifest`'s `RowCount` when present. Estimates propagate through filter, join, sort, limit, and projection operators using cost-model selectivity.

For EXPLAIN ANALYZE — actual row counts and per-operator timing — call `AnalyzeAsync` instead. It runs the plan under instrumentation and returns the populated tree:

```csharp
ExplainPlanNode analyzed = await catalog.AnalyzeAsync(plan);
Console.WriteLine(analyzed.Render());
```

Both shapes work on `StatementPlan`s of any family (`SelectPlan`, `DmlPlan`, `RoutinePlan`, `AlterTablePlan`, etc.) and on `StatementBatch` (which renders each child as a sub-tree). DDL plans are safe to EXPLAIN — they read the structured tree without firing the registry mutation.

The cost model selectivity table:

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
    // column.Kind     → DataKind.Float32
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
        return new Schema([new ColumnInfo("result", DataKind.Float32, nullable: false)]);
    }

    public async IAsyncEnumerable<Row> ExecuteAsync(
        DataValue[] arguments, CancellationToken cancellationToken) { /* ... */ }
}
```

## Auto-detecting table registration

`TableCatalog` pre-registers all built-in provider factories (csv, json, jsonl, parquet, hdf5, zip, idx) in its constructor — no manual `RegisterProvider` calls are needed for supported formats. The `Register` overloads detect the file format automatically from extension, filename pattern, or magic bytes:

```csharp
TableCatalog catalog = new();

// Simplest form — table name derived from filename (e.g. "iris.csv")
catalog.Register("./iris.csv");

// Explicit table name when you want a custom identifier
catalog.Register("data", "./iris.csv");

// Auto-detect with provider-specific options
catalog.Register("data", "./data.tsv", new Dictionary<string, string> { ["delimiter"] = "\t" });

// Explicit TableDescriptor still works as an override
catalog.Register(new TableDescriptor("csv", "data", "./data.csv", new()));
```

### Async registration with auto-expansion

`RegisterAsync` combines registration with multi-table expansion in one call. For sources that resolve to multiple sub-tables (e.g., root-object JSON), the original registration is replaced by one entry per discovered sub-table:

```csharp
TableCatalog catalog = new();

// Table name derived from filename
await catalog.RegisterAsync("./multi-table.json", CancellationToken.None);

// Or with an explicit name
await catalog.RegisterAsync("data", "./multi-table.json", CancellationToken.None);

// If the JSON has root keys "orders" and "customers",
// the catalog now contains "multi-table.json.orders" / "data.orders"
// and "multi-table.json.customers" / "data.customers".
```

For custom providers, use `RegisterProvider` before registering tables:

```csharp
catalog.RegisterProvider("custom", () => new MyCustomProvider());
catalog.Register("data", "./data.custom");
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
