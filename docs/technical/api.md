# Programmatic API

DatumV exposes a C# API for embedding ingestion, query execution, schema resolution, and manifest generation into .NET applications.

## Executing SQL

The primary surface for running SQL against a `TableCatalog` is the ADO.NET-style trio in `DatumV.Data` — `InProcessDatumDbConnection`, `InProcessDatumDbCommand`, `InProcessDatumDbReader`. The connection is a thin handle over a catalog; the command holds SQL text plus a parameter collection; the reader streams rows.

```csharp
using DatumV.Catalog;
using DatumV.Data;

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

For procedural batches that share an `ExecutionContext` across many plans, or for callers that already hold a parsed `Statement`, the catalog exposes the lower-level surface directly:

```csharp
// Single statement (throws on multi-statement SQL)
StatementPlan plan = await catalog.PlanAsync("CREATE TABLE t (id INT32 NOT NULL)");
await catalog.ExecuteAsync(plan).DrainAsync(); // applies side effect

// Auto-detect single vs multi-statement
PreparedSql prepared = await catalog.PrepareAsync(sql);
```

`PlanAsync` is the building block. The returned `StatementPlan` is lazy — reading `plan.ExplainTree` does not apply the side effect; iterating `plan.ExecuteAsync(ct, context)` does.

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

NDV is the estimated distinct count from the manifest's HyperLogLog sketch (±2% accuracy). Per-node estimates are exposed on `node.EstimatedRows`.

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

### Serialization

`ManifestSerializer` reads and writes the JSON form. The container shape is `SourceManifest` — a dictionary keyed by table name that supports multi-table sources — and there are overloads for the common single-table case that accept `(tableName, QueryResultsManifest)` directly.

```csharp
// Single-table form
string json = ManifestSerializer.Serialize("data", manifest);
await ManifestSerializer.WriteToFileAsync("data", manifest, "data.datum-manifest");

// Multi-table form
await ManifestSerializer.WriteToFileAsync(sourceManifest, "multi.datum-manifest");

// Deserialize
SourceManifest? loaded = ManifestSerializer.Deserialize(json);
if (loaded is not null
    && loaded.Tables.TryGetValue("data", out QueryResultsManifest? perTable))
{
    // ...
}
```

When the query planner has a manifest available for a source, it uses it to:
- Override the scan operator's estimated row count with the manifest's authoritative `RowCount`.
- Attach per-column `FeatureManifest` statistics for downstream cardinality estimation.

## Ingestion

`Ingester` converts a source file into a single `.datum` v2 columnar file (plus an optional `.datum-blob` sidecar for non-inline payloads), collecting schema, statistics, and a sample preview along the way. Each call processes one source file → one `.datum` file.

### Basic usage

```csharp
Ingester ingester = new(formatRegistry, pool);

FileFormatDescriptor source = new("data.csv");
OutputDescriptor destination = new("data.datum");

IngestionResult result = await ingester.IngestAsync(source, destination);

string outputPath = result.OutputPath;
long rows = result.RowCount;
long bytes = result.BytesWritten;
Schema schema = result.Schema;
QueryResultsManifest manifest = result.Manifest;
SamplePreview? preview = result.Sample;
```

`FileFormatDescriptor` and `OutputDescriptor` both accept an optional `IReadOnlyDictionary<string, string>` of format-specific options (CSV delimiter, header policy, etc.). Subclass `OutputDescriptor` and override `OpenAsync` to redirect the write to a custom stream (in-memory testing, compression wrappers, cloud storage).

For memory-constrained / multi-tenant hosts, pass `IngestionOptions.MultiTenantServer`:

```csharp
IngestionResult result = await ingester.IngestAsync(
    source, destination, IngestionOptions.MultiTenantServer);
```

### Sample preview

During ingestion, 25 representative rows are collected via reservoir sampling (Algorithm R), producing a uniform random sample regardless of dataset size. The preview is on `IngestionResult.Sample`:

```csharp
SamplePreview? preview = result.Sample;
if (preview is null) return;

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

## Schema Introspection

`QuerySchemaResolver` walks the FROM / JOIN / subquery / TVF sources of a parsed `SelectStatement` and returns the combined `ResolvedQuerySchema` — the substrate for editor autocomplete and pre-execution validation.

```csharp
// 1. Parse to a Statement, then pull the SelectStatement out.
Statement parsed = SqlParser.ParseStatement(
    "SELECT * FROM images AS img JOIN captions AS cap ON img.id = cap.image_id");
SelectStatement statement = parsed switch
{
    QueryStatement { Query: SelectQueryExpression sq } => sq.Statement,
    _ => throw new InvalidOperationException("Expected a SELECT statement."),
};

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
    // column.IsArray, column.IsMultiDim
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

For just one table's schema, ask the catalog for its provider and call `GetSchema()`:

```csharp
if (catalog.TryGetTable("tableName", out ITableProvider? provider))
{
    Schema schema = provider.GetSchema();
    foreach (ColumnInfo column in schema.Columns)
    {
        // column.Name, column.Kind, column.Nullable
    }
}
```

## Registering tables

`TableCatalog` is constructed with a `Pool`; once you have one, `AddFile` registers a `.datum` file as a queryable table. The table name defaults to the filename stem when omitted.

```csharp
using TableCatalog catalog = new(pool);

// Filename-derived name (e.g. "./iris.datum" → "iris")
catalog.AddFile("./iris.datum");

// Explicit table name
catalog.AddFile("./iris.datum", "data");

// Or register a fully-formed descriptor for finer control
catalog.Add(new TableDescriptor("data", "./iris.datum"));
```

For sources that aren't yet in `.datum` form, run them through the `Ingester` first (see [Ingestion](#ingestion)) and then register the produced `.datum` file. Auto-detection of arbitrary source formats and multi-table expansion now live on the ingestion side, not the catalog.
