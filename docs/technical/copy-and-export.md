# COPY and Export

Heliosoph.DatumV exports query results to external files via the SQL `COPY` statement. The on-disk shape is a universal interchange format (Parquet today; CSV / JSONL / `.datum` / HDF5 / FITS staged as follow-ups), so the resulting file is immediately consumable by Blender, MeshLab, DuckDB, pandas, Three.js, and other standard tooling — and re-importable through `open_parquet` back into the typed engine without an explicit conversion step.

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
  (FORMAT parquet, ROW_GROUP_SIZE 10000);
```

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

Three concrete encoders cover the scalar and reference-type cases:

- `ValueTypeEncoder<T>` — non-nullable value types (`int`, `long`, `double`, …). Stores in `List<T>`, flushes as `T[]`.
- `NullableValueTypeEncoder<T>` — nullable value types. Stores in `List<T?>`, flushes as `T?[]` which Parquet.Net wires up as a nullable primitive column.
- `ReferenceTypeEncoder<T>` — strings and string-like reference types. Reference nulls map to SQL NULL.

A fourth encoder handles typed media:

- `ByteArrayListEncoder` — Image / Audio / Video / Mesh / PointCloud. Writes as Parquet `LIST<UInt8>` with a flat values array and repetition levels. *Not* `DataField<byte[]>` (the BYTE_ARRAY shape) because Heliosoph.DatumV's own reader maps that to scalar `UInt8` and would mis-decode every row as a single byte. `is_array=true` on the meta surface matches the SQL intuition "image = array of bytes".

### Byte-budget flush

The sink flushes a row group when *either* the row count reaches `ROW_GROUP_SIZE` (default 50,000) *or* the aggregate buffered bytes across all columns reaches `DefaultRowGroupByteBudget` (128 MiB). The byte trigger is what actually fires for typed-media workloads — a mesh column at ~200 KB per row would otherwise need fewer than 11,000 rows to overflow `Int32.MaxValue` in the flat-bytes accumulator inside `ByteArrayListEncoder.FlushAsync`. There's a defensive `long` overflow check inside that flush that surfaces a clear `ExportRuntimeException` if a row group somehow accumulates more than `int.MaxValue` bytes despite the budget trigger.

### Typed-media transformation on export

Image / Audio / Video bytes are *passthrough* — the inline bytes already are the universal interchange format (PNG / JPEG / WAV / MP4 / WebM / etc.) and the encoder hands them through unchanged.

Mesh and PointCloud are different. Heliosoph.DatumV's internal blob format isn't a standard — it has its own 48-byte / 40-byte header plus interleaved data — so writing those bytes verbatim into a Parquet file would produce a column unreadable by any external tool. The encoder routes through:

- `GltfExporter.Export(blob, "Heliosoph.DatumV")` for Mesh, producing binary glTF 2.0 (`.glb`) bytes consumable by Blender, Three.js, Unity, the browser's built-in 3D viewer.
- `PlyExporter.Export(blob, "Heliosoph.DatumV")` for PointCloud, producing binary PLY consumable by MeshLab, CloudCompare, Open3D, Blender's PLY importer.

This trades round-trip fidelity (PLY drops attributes outside `position` + `RGB`; glTF drops anything outside the standard vertex attributes) for immediate external usability. The lost attributes are precisely the ones reserved for Phase 2 of those kinds anyway (`MeshFlags.HasUVs` / `HasTexture`, `PointCloudFlags.HasNormals`).

Drawing is rejected at plan time with a `render(drawing, point2d(w, h))` hint — Drawing is a procedural recipe (a `DrawingPayload` tree), not bytes, so implicit rasterisation would silently pick a size and surprise users.

### Nullable typed media — known sharp edge

`ByteArrayListEncoder` forces `isNullable: false` on the underlying Parquet field because Parquet.Net's nullable-LIST writer path didn't converge — every variant rejected the definition-level stream with a row-count mismatch. A SQL `NULL` in an Image / Audio / Video / Mesh / PointCloud column throws `ExportRuntimeException` at append time. Real columns from ingested datasets rarely carry NULLs, but it's a documented limitation worth lifting.

---

## The `datumv.*` Metadata Convention

[`src/Heliosoph.DatumV/Serialization/Parquet/ParquetDatumvMetadata.cs`](../../src/Heliosoph.DatumV/Serialization/Parquet/ParquetDatumvMetadata.cs)

The sink attaches three key/value pairs to each typed column's Parquet column-chunk metadata, written via Parquet.Net's 3-arg `WriteColumnAsync(column, customMetadata, ct)` on every row-group flush. External tools see opaque KV pairs and ignore them; Heliosoph.DatumV's own reader uses them to re-route each tagged column back to its typed `DataKind` on import.

| Key | Value | Set for |
|---|---|---|
| `datumv.kind` | `Mesh` / `PointCloud` / `Image` / `Audio` / `Video` / `Date` / `TimestampTz` | Every typed column the engine emits |
| `datumv.format` | `gltf` / `ply` / `passthrough` | The on-disk byte format |
| `datumv.version` | `1` | All annotated columns (lets us evolve) |

Typed-media kinds use `gltf` / `ply` / `passthrough`. Scalar kinds whose Parquet logical type lifts to a different CLR type than the writer started with (Date lifts to `DateTime` on read, not `DateOnly`; TimestampTz lifts to `DateTime` UTC, not `DateTimeOffset`) use `passthrough` — the value survives, only the kind label needs to be restored.

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

## Limitations and follow-ups

Real gaps in the current implementation:

- **Nullable typed-media arrays** can't be written (`ByteArrayListEncoder` forces `isNullable: false`). SQL `NULL` in an Image / Audio / Video / Mesh / PointCloud column throws `ExportRuntimeException` at append time.
- **SQL `Array<T>` (non-typed-media) columns** reject at plan time in the encoder factory. `LIST<T>` for general arrays is more code but the same shape.
- **`Json` (CBOR) kind** doesn't export — the CBOR-vs-JSON-text decision is open.

Deferred design choices (called out, status unchanged):

- **`COPY ... TO STDOUT`** — not implemented. The Electron model has a shared filesystem so the current path-target approach works; STDOUT is the right answer for any web-served deployment.
- **Directory targets + `MediaDisposition.Sidecar`** — interface admits them, sink doesn't implement.
- **`PARTITION_BY` and multi-file row-group splits** — same.
- **CSV / JSONL / `.datum` / HDF5 / FITS** — unwritten. Parquet is the headline.

Format / fidelity caveats users should know about:

- Mesh round-trip via glTF preserves position / normals / colors / triangle indices. UVs and embedded textures get dropped on import because `MeshFlags.HasUVs` and `MeshFlags.HasTexture` aren't in `MeshHeader.SupportedFlags` yet (Phase 2).
- PointCloud round-trip via PLY preserves position + RGB color. Normals get dropped because `PointCloudFlags.HasNormals` isn't in `PointCloudHeader.SupportedFlags` yet.
- TimestampTz round-trip preserves the instant only; the original wall-clock offset is not on disk. Parquet `TIMESTAMP isAdjustedToUTC=true` stores UTC.
