# Schemas S1 — `ITableCatalog` + `CatalogRouter`

## Goal

Introduce the catalog-backend abstraction. Today's `TableCatalog` is a flat
`ConcurrentDictionary<string, ITableProvider>` plus a pile of DDL methods
that touch files directly. After this phase, `TableCatalog` becomes a
**router** over schema-mounted `ITableCatalog` backends, and existing
providers move inside one of three concrete backends. The planner stops
talking to providers directly — it asks the router for a table by
`(schema, name)`.

This is the load-bearing phase. Everything else (manifest format, parser,
resolver, LSP) builds on the boundary defined here.

## Pre-reqs

None. This is the first phase.

## Locked decisions in scope

- `(schema, name)` is the canonical table identity.
- Three initial backends: `FlatFileCatalog`, `SystemCatalog`, `VirtualCatalog`.
- Existing dotted names rip-and-replace into real schemas (no aliases).
- `ITableProvider` survives unchanged-ish as the per-table handle returned by
  `ITableCatalog.TryGetTable`. Renaming/shrinking it is a follow-up, not this.
- No backward compatibility — providers and tests are updated in the same PR.

## What gets built

### New files (under `src/DatumIngest/Catalog/`)

- `QualifiedName.cs` — readonly record struct `(string Schema, string Name)`.
  - `IEquatable` with case-insensitive comparison on both fields.
  - `ToString() => $"{Schema}.{Name}"` for diagnostics only — never used as a
    lookup key inside the engine.
  - Static `Parse(string flat, string defaultSchema)` for catalog-load paths
    only; not for query-time lookup.
- `ITableCatalog.cs`:
  ```csharp
  public interface ITableCatalog
  {
      IReadOnlyCollection<string> Schemas { get; }
      bool IsReadOnly { get; }   // SystemCatalog/VirtualCatalog return true

      bool TryGetTable(QualifiedName name, out ITableProvider provider);
      IEnumerable<QualifiedName> ListTables(string schema);

      // Mutation — throws on read-only backends.
      ITableProvider CreateTable(QualifiedName name, IReadOnlyList<ColumnDescriptor> cols, /* options */);
      void DropTable(QualifiedName name, bool ifExists);

      // Schema lifecycle — throws on read-only backends.
      void CreateSchema(string name, bool ifNotExists);
      void DropSchema(string name, bool ifExists, bool cascade);
  }
  ```
- `ITransactionalCatalog.cs` — interface only, **no implementation**:
  ```csharp
  public interface ITransactionalCatalog : ITableCatalog
  {
      ITransactionHandle Begin();
      // Commit/Rollback live on ITransactionHandle.
  }
  public interface ITransactionHandle : IDisposable { void Commit(); void Rollback(); }
  ```
  Mark with a comment pointing at the [transactions roadmap memory](../memory/project_transactions_and_incremental_backups.md).
- `CatalogRouter.cs` — owns `Dictionary<string schema, ITableCatalog backend>`
  (case-insensitive). Methods:
  - `Mount(string schema, ITableCatalog backend)` — registers; multiple
    schemas can map to the same instance.
  - `TryGetTable(QualifiedName) → ITableProvider?` — routes.
  - `ListSchemas()` — union of mounted schemas.
  - `ResolveBackend(string schema) → ITableCatalog` — used by the planner /
    DDL appliers when they need to call mutation methods.
- `Backends/FlatFileCatalog.cs` — wraps today's `DatumFileTableProviderV2`
  instances and the on-disk `<catalogDir>/<flat_name>.datum` convention.
  Owns `public` plus any user-created schemas. **Layout note:** the
  internal flat filename for `(schema, name)` is `"<schema>.<name>"` —
  same disk shape as today, deliberately, because this backend is doomed
  in favor of `DatumDbCatalog` and a layout migration would be wasted.
- `Backends/SystemCatalog.cs` — read-only. Owns `system` schema. Wraps
  today's `UdfsTableProvider` (now exposing `system.udfs`) and
  `ProceduresTableProvider` (now `system.procedures`). Constructed with
  references to the live UDF / procedure stores.
- `Backends/VirtualCatalog.cs` — read-only. Owns `information_schema` and
  `datum_catalog`. Wraps the existing `InformationSchema*Provider` and
  `DatumCatalog*Provider` classes. Constructed with a reference back to the
  `CatalogRouter` so `information_schema.schemata` enumerates the live
  router state instead of the hardcoded `public`/`system`.

### Modified files

- [src/DatumIngest/Catalog/TableCatalog.cs](../src/DatumIngest/Catalog/TableCatalog.cs)
  — becomes a thin shell that constructs a `CatalogRouter`, mounts the
  three backends, and delegates `TryGetTable` / DDL appliers / catalog-load
  / catalog-save to it. Indexer `_catalog[name]` becomes
  `_router.TryGetTable(QualifiedName.Parse(name, defaultSchema))` — but
  callers should be migrated off the string indexer in S4. For S1, the
  string indexer can stay as a temporary shim that splits on `.` and
  defaults to `public`.
- [src/DatumIngest/Catalog/ITableProvider.cs](../src/DatumIngest/Catalog/ITableProvider.cs)
  — `Name` property → `QualifiedName Name { get; }`. Every implementation
  updated.
- All ten provider implementations under `src/DatumIngest/Catalog/Providers/`
  — their constants change:
  - `UdfsTableProvider`: `"system_udfs"` → `("system", "udfs")`
  - `ProceduresTableProvider`: `"system_procedures"` → `("system", "procedures")`
  - `InformationSchemaTablesProvider`: `"information_schema.tables"` → `("information_schema", "tables")`
  - `InformationSchemaColumnsProvider`: same pattern
  - `InformationSchemaSchemataProvider`: same pattern; **also** rewritten to
    enumerate the `CatalogRouter`'s live schemas instead of hardcoded
    `public`/`system`.
  - All five `DatumCatalog*Provider` classes: same pattern.
- `DatumFileTableProviderV2` — its `Name` becomes a `QualifiedName`. The
  flat-name still appears in the `.datum` file path; that's the
  `FlatFileCatalog`'s internal concern.

### Tests

- `src/DatumIngest.Tests/Catalog/CatalogRouterTests.cs` (new):
  - Mount two backends on different schemas; route correctly.
  - Mount one backend on multiple schemas; both resolve.
  - `TryGetTable` for a `(schema, name)` whose schema is unmounted returns
    false (planner / resolver decides what error to raise — not the router).
  - `ListSchemas()` is the union.
- `src/DatumIngest.Tests/Catalog/SystemCatalogTests.cs` (new):
  - `system.udfs` resolves and returns the UDFs provider.
  - `CreateTable` throws (read-only).
- `src/DatumIngest.Tests/Catalog/VirtualCatalogTests.cs` (new):
  - `information_schema.schemata` enumerates the live router schemas, not
    a hardcoded list. Test by mounting an extra backend on a custom schema
    and asserting it appears.
- Existing tests that reference `system_udfs` / `information_schema.tables`
  etc. — updated in this PR to the new names. Spot-check
  [InformationSchemaProvidersTests.cs](../src/DatumIngest.Tests/Catalog/InformationSchemaProvidersTests.cs),
  [SemanticAnalyzerTests.cs:691-795](../src/DatumIngest.LanguageServer.Tests/SemanticAnalyzerTests.cs#L691),
  [IndexAutoExtensionTests.cs](../src/DatumIngest.Tests/Catalog/IndexAutoExtensionTests.cs),
  [AppendSessionTests.cs:279](../src/DatumIngest.Tests/Catalog/AppendSessionTests.cs#L279).

### What NOT to do in this phase

- **Don't change the parser.** S3 owns AST and parser changes. For S1, the
  parser still emits `TableReference(Name, SchemaName)` as today, and the
  planner still uses its current `$"{schema}.{name}"` flat-string lookup —
  but that lookup now goes through the temporary string indexer on
  `TableCatalog` that delegates to the router. The migration of every
  lookup site to `QualifiedName` happens in S4.
- **Don't change the manifest format.** S2 owns that. The `CatalogStore`
  reader can keep producing flat names; they get split on `.` against the
  built-in schema names (`system`, `information_schema`, `datum_catalog`)
  with everything else going to `public`. This is a temporary shim
  removed in S2.
- **Don't bump `CREATE TABLE` to accept qualified names.** That's S3.
- **Don't introduce `search_path`.** That's S4.
- **Don't rename `ITableProvider`.** Follow-up after S5.

## Test commands

Per the [GPU-cost memory](../memory/feedback_test_suite_vram_cost.md), scope
the run:

```
dotnet test --no-restore --filter "FullyQualifiedName~Catalog"
```

## Done when

- [ ] `ITableCatalog`, `CatalogRouter`, `QualifiedName` exist and have unit
      coverage.
- [ ] `FlatFileCatalog`, `SystemCatalog`, `VirtualCatalog` exist; old
      providers live inside them.
- [ ] `TableCatalog` constructs a router with three mounted backends and
      delegates everything.
- [ ] All ten system providers expose their canonical `(schema, name)`.
- [ ] `information_schema.schemata` enumerates live router state.
- [ ] `ITransactionalCatalog` interface exists, with a comment pointing at
      the transactions roadmap; no implementation.
- [ ] All existing tests pass after rename — `Catalog` and `LanguageServer`
      subtrees both green.
- [ ] No new `.GetAwaiter().GetResult()` calls added (per the
      [async-bridge cleanup memory](../memory/project_async_sync_bridge_cleanup.md)).
