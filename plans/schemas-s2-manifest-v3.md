# Schemas S2 — Manifest v3

## Goal

Bump the catalog manifest format to v3 so it speaks `(schema, name)` natively
and gives each `ITableCatalog` backend an opaque slot for its own state.
Remove the temporary flat-name-splitting shim that S1 introduced. No
backward compatibility with v2 — older catalogs are rejected with a clear
"delete and start fresh" error.

This is a small phase by design.

## Pre-reqs

- S1 (`ITableCatalog` + `CatalogRouter` + three backends are in place).

## Locked decisions in scope

- v3 only; no v2 reader.
- Per-backend opaque `backend_state` blob — `FlatFileCatalog` puts its
  flat-name → file-path map there; `SystemCatalog` / `VirtualCatalog` write
  nothing.
- Disk layout under `FlatFileCatalog` stays today's flat
  `<catalogDir>/<flat_name>.datum` because the backend is doomed in favor of
  `DatumDbCatalog`. No physical file move.

## What gets built

### Modified files

- [src/DatumIngest/Catalog/CatalogStore.cs](../src/DatumIngest/Catalog/CatalogStore.cs)
  — new on-disk JSON shape:
  ```json
  {
    "version": 3,
    "schemas": ["public", "myapp"],
    "tables": [
      { "schema": "public", "name": "users" },
      { "schema": "myapp",  "name": "orders" }
    ],
    "udfs":       [{ "schema": "public", "name": "...", /* existing fields */ }],
    "procedures": [{ "schema": "public", "name": "...", /* existing fields */ }],
    "backends": {
      "FlatFile": {
        "files": [
          { "schema": "public", "name": "users",  "path": "users.datum" },
          { "schema": "myapp",  "name": "orders", "path": "orders.datum" }
        ]
      }
    }
  }
  ```
  Notes:
  - Top-level `tables` is the authoritative `(schema, name)` registry, schema-side. Backend
    state (file paths) lives under `backends.<key>` and is opaque to the
    router.
  - `udfs` and `procedures` gain a `schema` field. They previously sat
    flat; default migration is impossible (no v2 reader), so existing
    catalogs won't carry them across — fresh start.
  - `schemas` lists *user-created* schemas only. Built-in schemas
    (`public`, `system`, `information_schema`, `datum_catalog`) are
    re-mounted by the router on construction and don't need persisting.
  - `backends` is a `Dictionary<string backendKey, JsonElement>`. Each
    backend writes/reads its own slot; the store doesn't introspect.

- `CatalogStore.Save(...)` signature changes:
  ```csharp
  void Save(
      IReadOnlyList<string> userSchemas,
      IReadOnlyList<QualifiedName> tables,
      IReadOnlyList<UdfDescriptor> udfs,
      IReadOnlyList<ProcedureDescriptor> procedures,
      IReadOnlyDictionary<string, JsonElement> backendState);
  ```
- `CatalogStore.Load()` returns the same shape. On `version != 3`:
  ```csharp
  throw new CatalogFormatException(
      "Catalog manifest version " + version + " is not supported. " +
      "Schema support requires v3. Delete the catalog directory to start fresh.");
  ```
- [src/DatumIngest/Catalog/Backends/FlatFileCatalog.cs](../src/DatumIngest/Catalog/Backends/FlatFileCatalog.cs)
  — gains `SerializeBackendState() → JsonElement` and
  `LoadBackendState(JsonElement)`. Writes the flat-name → relative-path map.
- `TableCatalog` (the router shell from S1) — wires backend-state
  serialization into save/load. Removes the temporary "split on `.`"
  legacy shim added in S1.
- `CatalogFileTableEntry` and any other v2-shaped DTOs in
  [CatalogStore.cs:632](../src/DatumIngest/Catalog/CatalogStore.cs#L632) —
  deleted.

### Tests

- `src/DatumIngest.Tests/Catalog/CatalogStoreV3Tests.cs` (new):
  - Save/load round-trip preserves schemas, tables (schema+name),
    udfs/procs (with schema), and backend state.
  - Loading a v2 manifest throws `CatalogFormatException` with the
    delete-directory hint.
  - Loading a v4-or-newer manifest also throws (forward-rejection;
    important once T0 phase ships its own version bump).
- Update existing `CatalogStore` tests to use the v3 shape; delete any
  v2 fixtures.

### What NOT to do

- **No v2 reader**, even guarded behind a flag. Forward-only.
- **No physical file move.** `FlatFileCatalog` continues writing to
  `<catalogDir>/<flat_name>.datum`.
- **No new schema-aware DDL.** S3 owns `CREATE SCHEMA` parsing; S2 only
  persists schemas that already got created via the S1 backend API.

## Test commands

```
dotnet test --no-restore --filter "FullyQualifiedName~CatalogStore"
```

## Done when

- [ ] `CatalogStore` reads/writes v3 only.
- [ ] v2 manifests fail-fast with a clear actionable error.
- [ ] `FlatFileCatalog` round-trips its backend state.
- [ ] S1's temporary flat-string-splitting shim in `TableCatalog` is
      removed.
- [ ] `Catalog` test subtree green.
