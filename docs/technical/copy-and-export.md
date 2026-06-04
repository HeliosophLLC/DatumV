# COPY and Export

Heliosoph.DatumV exports query results to external files via the SQL `COPY` statement. The on-disk shape is a universal interchange format (Parquet and CSV today; JSONL / `.datum` / HDF5 / FITS staged as follow-ups), so the resulting file is immediately consumable by Blender, MeshLab, DuckDB, pandas, Excel, Three.js, and other standard tooling — and re-importable through `open_parquet` / `open_csv_typed` back into the typed engine without an explicit conversion step.

This document covers the SQL surface, the plan / sink layering, the typed-media encoding strategy, the `datumv.*` metadata convention that closes the round-trip loop, and the front-end Electron UI that drives it.

---

## SQL Surface

### Grammar

```
COPY (<query>) TO '<path>' [ [WITH] (<option>, ...) ]
<option> ::= <identifier> <value>
<value>  ::= <string-literal> | <number-literal> | <identifier>
```

The source must be a parenthesised query expression. The target is a single-quoted path. The trailing `(...)` option block is optional; when present it must contain at least one option (an empty `()` is rejected — there's exactly one canonical form for "no options").

```sql
-- minimal
COPY (SELECT id, pic FROM samples) TO 'samples.parquet';

-- explicit format
COPY (SELECT id, pic FROM samples) TO 'samples.parquet' (FORMAT parquet);

-- with options
COPY (SELECT id, pic FROM samples) TO 'samples.parquet'
  (FORMAT parquet, ROW_GROUP_SIZE 10000, COMPRESSION 'zstd');
```

Format options recognised today (CSV options follow further down):

| Key | Type | Default | Notes |
|---|---|---|---|
| `FORMAT` | identifier / string | inferred from extension | `parquet` |
| `ROW_GROUP_SIZE` | integer | 50,000 | Rows per row group |
| `ROW_GROUP_BYTE_BUDGET` | integer | 128 MiB | Aggregate buffered-byte trigger for early flush |
| `COMPRESSION` | identifier / string | `snappy` | `none` / `snappy` / `gzip` / `zstd` / `brotli` / `lz4` |
| `COMPRESSION_LEVEL` | integer (0–3) | codec default | Honoured by gzip / zstd / brotli; ignored by snappy / lz4 |

`ROW_GROUP_SIZE`, `ROW_GROUP_BYTE_BUDGET`, and `COMPRESSION` are validated at plan time — typos and non-positive values throw `ExportPlanException` before any file handle opens.

### Format resolution

Reserved keys are case-insensitive. Today only `FORMAT` is special-cased; everything else is interpreted per-format at plan time.

- **Explicit**: `(FORMAT parquet)` resolves through `ExportFormatRegistry.Default.ResolveByName`.
- **Inferred from extension**: when `FORMAT` is absent, the target path's extension (`.parquet`) routes through `ResolveByExtension`.
- **Failure**: unknown name or un-inferable extension throws `ExportPlanException` at plan time.

### Output

A `COPY` statement yields a single-row summary cell with `(rows_written Int64, bytes_written Int64)`. The columns surface through the same cell-event plumbing as a `SELECT` result, so the Electron results pane renders a two-column, one-row table after the export completes. Matches DuckDB's `COPY (…) TO …` shape.

---

## Plan and Sink Layering

### ExportPlan

[`src/Heliosoph.DatumV/Catalog/Plans/ExportPlan.cs`](../../src/Heliosoph.DatumV/Catalog/Plans/ExportPlan.cs)

The `CopyStatement` AST is planned as an `ExportPlan` — a `StatementPlan` subclass that owns:

- A child `SelectPlan` for the source query
- The resolved `IExportFormat`
- The `ExportTarget` (`File` today; `Directory` reserved for sidecar / partitioned sinks)
- The parsed `ExportOptions` bag
- Per-column `MediaDisposition` decisions

Plan-time work in `ExportPlan.PlanAsync`:

1. Resolve the format from the `FORMAT` option, falling back to extension inference.
2. Build the projected `Schema` from the source query via `QuerySchemaResolver.ResolveProjectionAsync` — same path CTAS uses.
3. Call `format.ResolveDisposition(column, options)` per column. Typed-media columns the format can't represent throw a column-specific `ExportPlanException` *here*, before any file handle opens.
4. Plan the source as a child `SelectPlan` so EXPLAIN walks the full SELECT subtree under the COPY node.

Execute-time work in `ExportPlan.ExecuteImplAsync`:

1. Drain the source plan against a `WithoutStreaming` execution context so the inner `SelectPlan` doesn't open its own cell bracket. The user clicked Export — they want to see the summary cell, not the source's full row stream.
2. Lazily construct the sink on the first non-empty batch. This is where the schema reconciliation runs: when the static `QuerySchemaResolver` returned `DataKind.String` as a fallback for an unclassifiable expression (notably model invocations), `ReconcileSchemaWithRuntime` walks the first batch and overrides each column's `Kind` from the observed `DataValue.Kind`. Without this, a query like `LET m = models.depth_anything_v2_base(file) AS m` would build a String encoder that then tries `AsString` on a Mesh value at runtime.
3. Per batch: `sink.WriteAsync(batch, ct)`.
4. After completion: capture `RowsWritten` / `BytesWritten` *before* sink disposal (the sink doesn't promise the properties remain readable after `DisposeAsync`).
5. Yield the summary `RowBatch` with two `Int64` columns.

On any mid-stream failure the partial single-file target is best-effort deleted so the catalog never surfaces a half-written file as a successful export.

### IExportFormat, IExportSink, ExportFormatRegistry

[`src/Heliosoph.DatumV/Export/`](../../src/Heliosoph.DatumV/Export/)

Two interfaces. `IExportFormat` is the per-format capability surface; `IExportSink` is the per-export runtime sink.

```csharp
public interface IExportFormat
{
    string Name { get; }
    IReadOnlyList<string> Extensions { get; }
    bool RequiresDirectorySink { get; }
    MediaDisposition ResolveDisposition(ColumnInfo column, ExportOptions options);
    IExportSink CreateSink(
        ExportTarget target,
        Schema schema,
        IReadOnlyList<MediaDisposition> columnDispositions,
        ExportOptions options,
        SidecarRegistry? sidecarRegistry);
}

public interface IExportSink : IAsyncDisposable
{
    ValueTask WriteAsync(RowBatch batch, CancellationToken cancellationToken);
    ValueTask FinishAsync(CancellationToken cancellationToken);
    long RowsWritten { get; }
    long BytesWritten { get; }
}
```

`ExportFormatRegistry.Default` is a static singleton populated with every built-in format the engine ships. Hosts that want to enumerate formats through DI register `IExportFormatRegistry` and resolve the same singleton — the planner reads `Default` directly inline rather than threading the registry through `TableCatalog`. Export formats aren't catalog state; they're engine capabilities.

`MediaDisposition` declares how a sink handles typed-media columns: `Inline` (bytes go directly into the column) or `Sidecar` (bytes go to a sibling file, table cell holds a relative path — reserved for a future Directory target). The planner resolves this per column before constructing the sink so unsupported combinations fail with clear errors at plan time.

The `SidecarRegistry` parameter on `CreateSink` is what lets the sink resolve sidecar-backed `DataValue`s (`Image` / `Audio` / `Video` / `Mesh` / `PointCloud` / `String` payloads that live in `.datum-blob` files rather than the row arena). The execution context's `SidecarRegistry` flows through; encoders close over it in their extractor lambdas.

---

## The Parquet Sink

[`src/Heliosoph.DatumV/Export/Parquet/`](../../src/Heliosoph.DatumV/Export/Parquet/)

### Encoder model

`ParquetColumnEncoder` is the per-column accumulator. Each encoder owns a typed buffer, computes `BufferedBytes` so the sink can flush row groups before per-column buffers exceed Parquet.Net's 2 GB writer ceiling, and produces a `DataColumn` on flush.

Scalar / reference-type encoders:

- `ValueTypeEncoder<T>` — non-nullable value types (`int`, `long`, `double`, …). Stores in `List<T>`, flushes as `T[]`.
- `NullableValueTypeEncoder<T>` — nullable value types. Stores in `List<T?>`, flushes as `T?[]` which Parquet.Net wires up as a nullable primitive column.
- `ReferenceTypeEncoder<T>` — strings and string-like reference types. Reference nulls map to SQL NULL. Also the carrier for `DataKind.Json` (decoded CBOR → canonical JSON text) with the matching `datumv.*` tag.

Typed-media and array encoders:

- `ByteArrayListEncoder` — Image / Audio / Video / Mesh / PointCloud. Writes as Parquet `LIST<UInt8>` with a flat values array, repetition levels, and (when nullable) a definition-level stream. *Not* `DataField<byte[]>` (the BYTE_ARRAY shape) because Heliosoph.DatumV's own reader maps that to scalar `UInt8` and would mis-decode every row as a single byte. `is_array=true` on the meta surface matches the SQL intuition "image = array of bytes". Honours the source column's nullability — SQL NULL rows survive as on-disk `(rep=0, def=0)` markers and read back as typed `NULL`.
- `PrimitiveArrayListEncoder<T>` / `StringArrayListEncoder` — `Array<T>` columns for `Boolean` / `Int8/16/32/64` / `UInt8/16/32/64` / `Float32/64` / `String` elements. Same `LIST<T>` shape as the typed-media encoder but with the element-type CLR span.
- `StructColumnEncoder` — top-level `STRUCT` columns. Owns a `StructChildHandler[]` (scalar primitives via `ScalarStructChild`, list children via `ListStructChild<T>` / `StringListStructChild`). The list-child handler wires a real Parquet `ListField` *inside* the wrapping `StructField` so nested `LIST<T>` round-trips cleanly through external tools — without it Parquet.Net mis-serialises the `DataField(isArray:true)` shortcut and the column reads back as raw bytes.
- `StructArrayListEncoder` — top-level `Array<Struct>` columns (e.g. `[{ field1: 'a', field2: 1 }]`). Produces `LIST<STRUCT<…>>`: per-row arrays flatten into per-leaf flat buffers + shared rep levels, per-leaf def levels at each leaf's `MaxDefinitionLevel`. Children must be primitives in v1.

### Byte-budget flush

The sink flushes a row group when *either* the row count reaches `ROW_GROUP_SIZE` (default 50,000) *or* the aggregate buffered bytes across all columns reaches `DefaultRowGroupByteBudget` (128 MiB). The byte trigger is what actually fires for typed-media workloads — a mesh column at ~200 KB per row would otherwise need fewer than 11,000 rows to overflow `Int32.MaxValue` in the flat-bytes accumulator inside `ByteArrayListEncoder.FlushAsync`. There's a defensive `long` overflow check inside that flush that surfaces a clear `ExportRuntimeException` if a row group somehow accumulates more than `int.MaxValue` bytes despite the budget trigger.

### Typed-media transformation on export

Image / Audio / Video bytes are *passthrough* — the inline bytes already are the universal interchange format (PNG / JPEG / WAV / MP4 / WebM / etc.) and the encoder hands them through unchanged.

Mesh and PointCloud are different. Heliosoph.DatumV's internal blob format isn't a standard — it has its own 48-byte / 40-byte header plus interleaved data — so writing those bytes verbatim into a Parquet file would produce a column unreadable by any external tool. The encoder routes through:

- `GltfExporter.Export(blob, "Heliosoph.DatumV")` for Mesh, producing binary glTF 2.0 (`.glb`) bytes consumable by Blender, Three.js, Unity, the browser's built-in 3D viewer.
- `PlyExporter.Export(blob, "Heliosoph.DatumV")` for PointCloud, producing binary PLY consumable by MeshLab, CloudCompare, Open3D, Blender's PLY importer.

This trades round-trip fidelity (PLY drops attributes outside `position` + `RGB`; glTF drops anything outside the standard vertex attributes) for immediate external usability. The lost attributes are precisely the ones reserved for Phase 2 of those kinds anyway (`MeshFlags.HasUVs` / `HasTexture`, `PointCloudFlags.HasNormals`).

Json is decoded from canonical CBOR to JSON text via `CborJsonCodec.DecodeToJsonText` on the way out — the column on disk is a plain UTF-8 string that pandas / DuckDB / Spark / Polars all read directly. The `datumv.kind=Json` + `datumv.format=text` tag tells `open_parquet` to re-encode the text back to CBOR via `CborJsonCodec.EncodeFromJsonText` on read so the engine's `DataKind.Json` contract (bytes are canonical CBOR) survives the round trip.

Drawing is rejected at plan time with a `render(drawing, point2d(w, h))` hint — Drawing is a procedural recipe (a `DrawingPayload` tree), not bytes, so implicit rasterisation would silently pick a size and surprise users.

### The `DataColumn` argument-order trap

Parquet.Net's `DataColumn(field, data, definitionLevels, repetitionLevels)` 4-arg ctor takes definition levels *before* repetition levels in positional order — opposite to what most level-stream APIs expect. Every encoder that writes level streams in this codebase uses named arguments (`definitionLevels:` / `repetitionLevels:`) so a future drive-by edit can't silently swap them. The byte-for-byte symptom of a positional mistake is a file that *writes* successfully but produces `IndexOutOfRangeException` deep in `DeltaBinaryPackedEncoder.DecodeInt` on read — easy to misdiagnose as a level-arithmetic bug.

---

## The CSV Sink

[`src/Heliosoph.DatumV/Export/Csv/`](../../src/Heliosoph.DatumV/Export/Csv/)

CSV is the second built-in format, sitting alongside Parquet in `ExportFormatRegistry.Default`. The contract is the same `IExportFormat` / `IExportSink` pair — what differs is the on-disk shape and the typed-media policy.

### Options

| Key | Type | Default | Notes |
|---|---|---|---|
| `HEADER` | boolean | `true` | First row is the comma-separated column names |
| `DELIMITER` | string | `,` | Single character; `tab` or `\t` shorthand for the literal tab |
| `QUOTE` | string | `"` | Single non-newline character; doubled inside quoted bodies per RFC 4180 |
| `LINE_ENDING` | identifier / string | `lf` | `lf` (default) or `crlf` |
| `NULL_STRING` | string | `''` (empty) | The text written for SQL NULL |

All options are validated at plan time. Unknown `LINE_ENDING`, multi-character `DELIMITER` / `QUOTE`, and a delimiter that would collide with quoting (`"`, `\r`, `\n`) each throw `ExportPlanException` before the file is opened.

The boolean option passes as a quoted string today (`HEADER 'false'`) because the COPY grammar's `<value>` rule covers string-literal / number-literal / identifier — bareword `false` is the reserved keyword, not an option literal. `ExportOptions.TryGetBool` parses the string.

### Scalar text formats

The scalar formats are picked to round-trip cleanly through [`open_csv_typed`](../functions/table-valued.md) — the table-valued function that runs `CsvTypeScanner` at plan time and surfaces real per-column types. The contract:

- **Booleans**: lowercase `true` / `false` — `IsBooleanLiteral` accepts both casings, and lowercase matches Postgres / Polars convention.
- **Integers**: `InvariantCulture.ToString()` — no thousands separators, no sign except `-`.
- **Floats**: `"R"` round-trippable form. `NaN` / `Infinity` survive as text; the scanner re-classifies the column as `String` (no JSON `null`-style elision needed because CSV doesn't have a JSON-number constraint).
- **Decimals**: `InvariantCulture.ToString()` — preserves trailing zeros that came in via the SQL `DECIMAL(p, s)` declaration (`12.50m` writes as `12.50`, not `12.5`).
- **Dates**: `yyyy-MM-dd`.
- **Times**: `HH:mm:ss.FFFFFFF` (trailing fractional zeros suppressed).
- **Timestamp**: `yyyy-MM-ddTHH:mm:ss.FFFFFFF` (naive — no offset).
- **TimestampTz**: ISO 8601 `"O"` form. Internal storage is UTC ticks, so the on-disk offset is always `+00:00` — that's accurate, not a fidelity loss; the original wall-clock offset is already gone by the time the value reaches the sink.
- **Uuid**: `D` form (`8-4-4-4-12` hex without braces).
- **String**: passes through unchanged except for RFC 4180 quoting.
- **NULL**: empty field by default. `NULL_STRING 'NULL'` (or any literal) opts in to a sentinel.

### Composite kinds → JSON text

`DataKind.Struct` and `Array<T>` columns serialise as JSON text inside a single CSV field via `DataValueJsonWriter` (a `Utf8JsonWriter` over a pooled `ArrayBufferWriter<byte>`). The scanner re-reads them as `String` columns on import — that's the honest answer; CSV has no carrier for "this column is structured." Users who need a lossless composite round trip should export to Parquet.

Struct field names *aren't* on the wire (the engine carries them on the enclosing `TypeDescriptor`, not the per-value `DataValue`). The JSON writer falls back to positional `f0`, `f1`, … names. Json-kind values (CBOR on the wire) decode through `CborJsonCodec.DecodeToJsonText` before embedding so a Json-typed struct field surfaces as a real nested JSON object, not a stringified blob.

Array element support matches the Parquet sink's `PrimitiveArrayListEncoder` set in v1: `Boolean`, `Int8/16/32/64`, `UInt16/32/64`, `Float32/64`, `String`, and `Struct`. `Array<UInt8>` is rejected at plan time (see below); `Array<Date>` and friends fall through to a `<Array<Kind> not encodable in CSV>` placeholder. Broader array element support is paired across both formats when added.

### Typed media: plan-time rejection

CSV is a flat-text format; inlining base64 megabytes per row defeats the point. `CsvExportFormat.ResolveDisposition` rejects every typed-media kind at plan time with a clear column-named message that points the user at Parquet:

- `Image` / `Audio` / `Video` / `Mesh` / `PointCloud` — "export this column to parquet instead — Parquet preserves the bytes losslessly and round-trips through `open_parquet`."
- `Drawing` — same hint as the Parquet sink: rasterise via `render(drawing, point2d(w, h))` first.
- `Array<UInt8>` — "byte array which CSV cannot represent without base64-inlining megabytes per row. Export to parquet instead, or project it out of the SELECT."
- `VideoFrame` / `AudioSlice` / `VideoSlice` — runtime-only lazy handles; materialise to `Image` / `Audio` first.

The check recurses into `ColumnInfo.Fields` so a typed-media field inside an otherwise-representable struct fails at plan time with a `parent.field` dotted path in the message, not mid-stream.

### RFC 4180 quoting

A field is quoted when it contains the delimiter, the quote character, or any newline (`\n` or `\r`). The quote character inside a quoted body is doubled (`"` → `""`). Header names are quoted by the same rule, so a column called `weird,name` survives unambiguously.

There's no separate `ESCAPE` option — RFC 4180 quote-doubling is the only escape mechanism. The DuckDB-style backslash-escape dialect isn't supported because the scanner doesn't read it back.

### Round-trip story

CSV is a one-way export by default: kinds the scanner can narrow re-import cleanly through `open_csv_typed`, composite columns and inline visual / spatial scalars (`Color`, `Point2D`, `Point3D` — written as JSON objects via the same path as structs) come back as `String`. There's no `datumv.*` metadata convention for CSV because the format has no place to put it; that round-trip closure remains a Parquet-only feature.

---

## The `datumv.*` Metadata Convention

[`src/Heliosoph.DatumV/Serialization/Parquet/ParquetDatumvMetadata.cs`](../../src/Heliosoph.DatumV/Serialization/Parquet/ParquetDatumvMetadata.cs)

The sink attaches three key/value pairs to each typed column's Parquet column-chunk metadata, written via Parquet.Net's 3-arg `WriteColumnAsync(column, customMetadata, ct)` on every row-group flush. External tools see opaque KV pairs and ignore them; Heliosoph.DatumV's own reader uses them to re-route each tagged column back to its typed `DataKind` on import.

| Key | Value | Set for |
|---|---|---|
| `datumv.kind` | `Mesh` / `PointCloud` / `Image` / `Audio` / `Video` / `Json` / `Date` / `TimestampTz` | Every typed column the engine emits |
| `datumv.format` | `gltf` / `ply` / `text` / `passthrough` | The on-disk byte format |
| `datumv.version` | `1` | All annotated columns (lets us evolve) |

Typed-media kinds use `gltf` / `ply` / `passthrough`. `Json` uses `text` (UTF-8 JSON text on disk; `open_parquet` re-encodes to CBOR on read). Scalar kinds whose Parquet logical type lifts to a different CLR type than the writer started with (Date lifts to `DateTime` on read, not `DateOnly`; TimestampTz lifts to `DateTime` UTC, not `DateTimeOffset`) use `passthrough` — the value survives, only the kind label needs to be restored.

Other scalar kinds (Decimal, Time, Uuid, Int*, Float*, Boolean, String) round-trip cleanly through Parquet.Net's CLR type mapping without metadata.

### Read side: `open_parquet`

[`src/Heliosoph.DatumV/Functions/TableValued/OpenParquetFunction.cs`](../../src/Heliosoph.DatumV/Functions/TableValued/OpenParquetFunction.cs)

`open_parquet(path)` is the default; `open_parquet(path, typed)` is the explicit opt-out form. When `typed` is true (the default), the function:

1. **Plan-time** (`ValidateArguments`): probes the first row group's per-column metadata, and when it sees a recognised `datumv.kind` overrides the projected `ColumnInfo.Kind` to the typed kind (drops `IsArray` for scalar surfaces).
2. **Execute-time** (`StreamRowsAsync`): builds a `TypedMediaRoute[]` once from the first row group's metadata, then per-row:
   - Pipes Mesh bytes through `GltfImporter.Import(bytes)`.
   - Pipes PointCloud bytes through `PlyImporter.Import(bytes)`.
   - Retags Image / Audio / Video bytes (no decode — they're already in the universal shape).
   - For Date / TimestampTz, narrows the lifted `DateTime` back to `DateOnly` / UTC-offset `DateTimeOffset`.

Unknown / future kind tags fall back to the raw read-side value rather than throwing — an older build reading a file produced by a later build degrades gracefully.

When `typed = false`, the metadata routing is bypassed and every column reads as the raw Parquet type. Useful when piping bytes straight to another export without paying for a round-trip parse.

### Importers (the scalar functions)

For the round-trip story to work even on files without `datumv.*` metadata (third-party glTF / PLY files), `open_parquet`-tagged columns are equivalent to wrapping the column manually:

```sql
-- Tagged-file route (the typical case):
SELECT id, pic, model_mesh
FROM open_parquet('depth.parquet');
-- → pic is typed Image, model_mesh is typed Mesh

-- Untagged-file route (manual wrap):
SELECT id, mesh_from_gltf(bytes_col) AS mesh
FROM open_parquet('foreign.parquet');
```

The matching scalar functions both follow `ImageDecodeFunction`'s dual-overload pattern:

- `pointcloud_from_ply(bytes Array<UInt8>) → PointCloud`
- `pointcloud_from_ply(path String) → PointCloud` (reads file)
- `mesh_from_gltf(bytes Array<UInt8>) → Mesh`
- `mesh_from_gltf(path String) → Mesh` (reads file)

### `open_parquet_meta` exposes the metadata

[`src/Heliosoph.DatumV/Functions/TableValued/OpenParquetMetaFunction.cs`](../../src/Heliosoph.DatumV/Functions/TableValued/OpenParquetMetaFunction.cs)

The introspection TVF surfaces the metadata as three trailing nullable columns: `datumv_kind`, `datumv_format`, `datumv_version`. Third-party Parquet files read these as `NULL`; Heliosoph.DatumV-exported files surface the recorded tag. Useful for confirming "what does this file actually carry" without firing a trial `open_parquet`.

---

## Web / Electron UI

### LeafToolbar export button

[`src/Heliosoph.DatumV.Web/ClientApp/src/components/query/LeafToolbar.tsx`](../../src/Heliosoph.DatumV.Web/ClientApp/src/components/query/LeafToolbar.tsx)

A download-icon button sits below the Lightbulb in the per-leaf vertical toolbar. Enabled only for `tab.kind === 'sql'` — function tabs synthesise their script from form state and don't have a single SQL surface to wrap in a COPY.

The Run and Export buttons share the underlying execution stream (a COPY is just a SQL statement), so only one can be streaming at a time. The toolbar reads `exec.origin` to decide which button shows the Stop affordance:

- During a Run stream: Run button is `Square`, Export is disabled.
- During an Export stream: Export button is `Square`, Run is disabled.

### state/export.ts

[`src/Heliosoph.DatumV.Web/ClientApp/src/state/export.ts`](../../src/Heliosoph.DatumV.Web/ClientApp/src/state/export.ts)

`beginExport(leafId)` is the entry point. The flow:

1. Look up the active SQL tab.
2. Resolve the catalog root for the save-dialog default path via `api.files.getRoot()`.
3. Open the native save dialog through `window.electronHost.showSaveDialog(...)`, with the file-type filter built from `EXPORT_FORMATS` (Parquet today, hardcoded; will become a `/api/export/formats` fetch when a second format ships).
4. Build a `COPY (<tab.sql>) TO '<path>'` SQL string, escaping `'` characters in the path.
5. Hand it to `runTab(tab.id, sql, { origin: 'export' })` — same NDJSON streaming run path every other statement uses.

### Menu entry

[`src/Heliosoph.DatumV.Web/ClientApp/src/commands/menuDefinition.ts`](../../src/Heliosoph.DatumV.Web/ClientApp/src/commands/menuDefinition.ts)

`menu.run.export` lives under the **Run** menu with accelerator `CmdOrCtrl+Shift+E`. The command id is `query.export`; the registry handler in `commands/registry.ts` calls `beginExport(panesState.focusedLeafId)`.

### Wire shape and results-pane rendering

The COPY statement runs through the standard `/api/query/stream` NDJSON endpoint. `QueryStreamService` translates engine `CellRowBatchEvent`s into wire `row` events; the COPY's summary row arrives like any other cell row. The front-end's `applyEvent` switch in `state/execution.ts` buffers the row, then the results pane in `ResultsPane.tsx` renders the cell when `cell.rowCount > 0` per `isVisibleCell`.

Two important nuances:

- `ExportPlan` invokes the source `SelectPlan` against a `WithoutStreaming` context, so the source's full row stream doesn't generate a separate "select" cell ahead of the summary. Without this, exporting a 100k-row query would dump 100k rows into the UI as a noisy intermediate cell that pushes the summary off-screen.
- `WebCellFormatter` no longer assumes every `UInt8[]` column is an image. It sniffs PNG / JPEG / GIF / WebP / BMP / WAV / MP3 / FLAC / OGG / MP4 / WebM magic and only emits a `media` cell on a match. Bytes that don't match a known media format fall through to a text summary that names the format (`glTF` / `PLY` / generic binary) and hints at the matching `_from_X` importer.

---

## Language Server integration

[`src/Heliosoph.DatumV.LanguageServer/CompletionContext.cs`](../../src/Heliosoph.DatumV.LanguageServer/CompletionContext.cs)

The completion provider recognises four COPY-specific zones — `AfterCopy` (`COPY ⌷` → suggest `(`), `AfterCopySource` (`COPY (q) ⌷` → suggest `TO`), `AfterCopyTo` (`COPY (q) TO 'path' ⌷` → suggest option-block `(`), and `InCopyOptions` (cursor inside the option parens → surface format-specific option keys). The walk-back classifier detects the option-block paren by the `STRING → TO` token pair (with an optional `WITH` in between) and stops at the first `LeftParen` whose preceding tokens match that shape.

Per-format option keys live in `CopyFormatOptions.OptionKeysByFormat`. CSV registers `HEADER`, `DELIMITER`, `QUOTE`, `LINE_ENDING`, `NULL_STRING` against the `csv` key; the completion provider picks the set up automatically with no edits in `CompletionContext` or `CompletionProvider`. Future formats follow the same drop-in pattern.

---

## Limitations and follow-ups

Real gaps in the current implementation:

- **`STRUCT` inside `STRUCT`** is not supported at any nesting depth — the encoder rejects nested struct children at plan time with a column-named error. The natural fix is recursive child handlers; defer until a real consumer needs it.
- **Per-element NULL inside `LIST<T>`** is not surfaced — list-level NULL is honoured (`def == 0` marker) but individual elements within a present list are emitted at `MaxDefinitionLevel`. Heliosoph.DatumV typed arrays don't carry per-element nullability today.
- **Empty per-row lists** inside a top-level `LIST<STRUCT>` or struct list child throw at append time. The intermediate def level for "list present, no element" works on paper but hasn't been exercised; defer until a real consumer needs it.

Deferred design choices (called out, status unchanged):

- **`COPY ... TO STDOUT`** — not implemented. The Electron model has a shared filesystem so the current path-target approach works; STDOUT is the right answer for any web-served deployment.
- **Directory targets + `MediaDisposition.Sidecar`** — interface admits them, sink doesn't implement.
- **`PARTITION_BY` and multi-file row-group splits** — same.
- **JSONL / `.datum` / HDF5 / FITS** — unwritten. Parquet and CSV are the only built-in formats today.

Format / fidelity caveats users should know about:

- Mesh round-trip via glTF preserves position / normals / colors / triangle indices. UVs and embedded textures get dropped on import because `MeshFlags.HasUVs` and `MeshFlags.HasTexture` aren't in `MeshHeader.SupportedFlags` yet (Phase 2).
- PointCloud round-trip via PLY preserves position + RGB color. Normals get dropped because `PointCloudFlags.HasNormals` isn't in `PointCloudHeader.SupportedFlags` yet.
- TimestampTz round-trip preserves the instant only; the original wall-clock offset is not on disk. Parquet `TIMESTAMP isAdjustedToUTC=true` stores UTC.
