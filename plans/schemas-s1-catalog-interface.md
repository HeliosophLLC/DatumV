# Schemas S1 — `ITableCatalog` + backend extraction

## Goal

Introduce the catalog-backend abstraction. Today's `TableCatalog` is a
1588-line class doing roughly ten jobs (storage map, DDL appliers, file
loaders, planning entry, validation helpers, path resolution, parent/child
hierarchy, resource ownership, manifest persistence, mutation facade).
After this phase:

- The **storage** half (the `Tables` dict, path resolution, file-touching
  DDL implementation, persistent-table tracking) is **renamed** out into
  a new `FlatFileCatalog` — the first concrete `ITableCatalog`
  implementation.
- The **facade** half (`Plan` / `PlanAsync`, `Pool`, UDF + Procedure
  registries, `parent`/child hierarchy, manifest persistence,
  AST-handling/validation in DDL appliers) **stays on `TableCatalog`**,
  whose public surface is unchanged.
- `TableCatalog` gains a private `Dictionary<string schema, ITableCatalog
  backend>` and routes table lookup + DDL storage steps through it.
- Two additional backends are added: `SystemCatalog` (owns the `system`
  schema, projecting over the live UDF / Procedure registries) and
  `VirtualCatalog` (owns `information_schema` + `datum_catalog`).
- All ten existing providers expose their canonical `(schema, name)` and
  live inside the appropriate backend.

This is the load-bearing phase. Everything else (manifest format, parser,
resolver, LSP) builds on the boundary defined here.

## Pre-reqs

- S0 (per-table mutation methods removed from `TableCatalog` so this phase
  doesn't have to also churn every mutation callsite).

## Locked decisions in scope

- `(schema, name)` is the canonical table identity.
- Three initial backends: `FlatFileCatalog`, `SystemCatalog`, `VirtualCatalog`.
- Existing dotted names rip-and-replace into real schemas (no aliases).
- `ITableProvider` survives unchanged-ish as the per-table handle returned by
  `ITableCatalog.TryGetTable`. Renaming/shrinking it is a follow-up, not this.
- No backward compatibility — providers and tests are updated in the same PR.
- **`TableCatalog` keeps its name and public API surface.** It is *not*
  renamed to `CatalogRouter`. The storage half is renamed *out of it* into
  `FlatFileCatalog`. This preserves every existing
  `TableCatalog.Plan(...)` / `.AddFile(...)` / `.Pool` / `.FromFile(...)`
  callsite across the engine, DevWeb, tools, and tests.
- **No separate `CatalogRouter` class.** The "router" is just a private
  `Dictionary<string, ITableCatalog>` field on `TableCatalog`. Fewer
  types; honest about responsibilities.

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

      /// <summary>
      /// True if this backend accepts CREATE TABLE / DROP TABLE /
      /// CREATE SCHEMA / DROP SCHEMA. False for projection backends
      /// (SystemCatalog / VirtualCatalog).
      ///
      /// IMPORTANT: This is about the *DDL surface*, not data
      /// mutability. Per-table DML/ALTER capability lives on
      /// ITableProvider (e.g., CanAlterColumns) and is unchanged by
      /// this work.
      /// </summary>
      bool SupportsDdl { get; }

      bool TryGetTable(QualifiedName name, out ITableProvider provider);
      IEnumerable<QualifiedName> ListTables(string schema);

      // DDL — throws when SupportsDdl is false.
      ITableProvider CreateTable(QualifiedName name, IReadOnlyList<ColumnDescriptor> cols, /* options */);
      void DropTable(QualifiedName name, bool ifExists);
      void CreateSchema(string name, bool ifNotExists);
      void DropSchema(string name, bool ifExists, bool cascade);
  }
  ```

  **Catalog-level vs. table-level mutability — keep these distinct.**
  - `ITableCatalog.SupportsDdl` answers "can I run CREATE/DROP against
    this backend?" Drives `ResolveForCreate` (S4) and LSP completion
    after `CREATE TABLE ` (S5).
  - Per-table flags on `ITableProvider` (e.g., the existing
    `CanAlterColumns` consulted by `TableCatalog.ResolveForMutation`)
    answer "can I INSERT/UPDATE/DELETE/ALTER *this specific table*?"
    Stays where it is — S1 doesn't touch this surface.

  This distinction matters once views or federated/external tables
  appear: a single backend may host a mix of mutable and immutable
  tables, but the DDL question (does this backend create/drop
  things at all?) remains catalog-wide.
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
- `Backends/FlatFileCatalog.cs` — **the rename**. Most of today's
  `TableCatalog` storage half moves here:
  - The `Tables` `ConcurrentDictionary<QualifiedName, ITableProvider>`.
  - `_persistentTableEntries`.
  - `ResolveCreateTablePath`, `ResolveTablePath`, `ToPersistedPath`.
  - The file-touching parts of `ApplyCreateTable` (path resolution +
    `DatumFileWriterV2` invocation) and `ApplyDropTable` (file deletion +
    sidecar cleanup).
  - `Add(TableDescriptor)` and `Add(ITableProvider)` (the storage-side
    register; the public-facade variants stay on `TableCatalog`).

  Owns `public` plus any user-created schemas. `SupportsDdl == true`.
  **Layout note:** the internal flat filename for `(schema, name)` stays
  `"<schema>.<name>"` — same disk shape as today, deliberately, because
  this backend is doomed in favor of `DatumDbCatalog` and a layout
  migration would be wasted. ~700 lines.
- `Backends/SystemCatalog.cs` — `SupportsDdl == false`. Owns `system`
  schema. Wraps today's `UdfsTableProvider` (now exposing `system.udfs`)
  and `ProceduresTableProvider` (now `system.procedures`). Constructed
  with references to the live UDF / Procedure stores on `TableCatalog`.
  ~100 lines.
- `Backends/VirtualCatalog.cs` — `SupportsDdl == false`. Owns
  `information_schema` and `datum_catalog`. Wraps the existing
  `InformationSchema*Provider` and `DatumCatalog*Provider` classes.
  Constructed with a back-reference to the `TableCatalog` so
  `information_schema.schemata` enumerates the live mounted-schemas list
  instead of the hardcoded `public`/`system`. ~150 lines.

### Modified files

- [src/DatumIngest/Catalog/TableCatalog.cs](../src/DatumIngest/Catalog/TableCatalog.cs)
  — shrinks from ~1588 to ~1100 lines as the storage half moves to
  `FlatFileCatalog`. **Public surface is unchanged.** Internal changes:
  - Gains private `Dictionary<string, ITableCatalog> _backends` plus
    constructor wiring that mounts `FlatFileCatalog` on `public`,
    `SystemCatalog` on `system`, `VirtualCatalog` on `information_schema`
    and `datum_catalog`.
  - **Stays unchanged:** `Plan` / `PlanAsync` / `PlanQuery` / `PlanExec`
    (all six overloads), `Pool`, `Parent`, `Dispose`, UDF + Procedure
    registries and their `Add`/`Remove` methods,
    `BuildSchemaFromColumnDefinitions`, `ResolvePrimaryKeyColumnIndices`,
    `ValidatePrimaryKeySize`, `ValidateDefaultLiteral`,
    `IsAcceptedDefaultLiteral`, `Remove(string tableName)`,
    `GetEnumerator`, `CatalogLoadReport`.
  - **Becomes a thin wrapper around the backend dict:**
    `TryGetTable(string)`, `HasTable(string)`, indexer — split the string
    on `.` (S1 shim that defaults the schema to `public` when no dot
    present) → `_backends[schema].TryGetTable(QualifiedName)`. The shim
    is killed in S4 once callers move to `QualifiedName` directly.
    `AddFile`, `FromFile`, `FromDirectory` keep their public signatures
    but internally call into `FlatFileCatalog`.
  - **Refactored DDL appliers:** `ApplyCreateTable`, `ApplyDropTable`,
    `ApplyAlterTableAddColumn`, `ApplyAlterTableDropColumn`,
    `ApplyReindexTable`, `ApplyAnalyzeTable` keep their AST-handling +
    validation, then delegate the *storage step* to
    `_backends["public"].CreateTable(qn, cols)` etc. Until S3 lands,
    qualified names in DDL aren't supported, so the schema is always
    `public` for now.
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
    enumerate the `TableCatalog`'s live mounted-schemas list instead of
    hardcoded `public`/`system`.
  - All five `DatumCatalog*Provider` classes: same pattern.
- `DatumFileTableProviderV2` — its `Name` becomes a `QualifiedName`. The
  flat-name still appears in the `.datum` file path; that's the
  `FlatFileCatalog`'s internal concern.
- [src/DatumIngest/Execution/Operators/ScanOperator.cs](../src/DatumIngest/Execution/Operators/ScanOperator.cs)
  — about 10 sites read `provider.Name` for tracer / EXPLAIN output
  (lines 158, 213-388). When `provider.Name` flips to `QualifiedName`,
  add `.ToString()` at each callsite. Mechanical. Verify any sibling
  operators that also read `provider.Name`.

### Tests

- `src/DatumIngest.Tests/Catalog/TableCatalogRoutingTests.cs` (new):
  - Mount two backends on different schemas; route correctly via the
    `TableCatalog`'s internal backend dict.
  - Mount one backend on multiple schemas; both resolve.
  - `TryGetTable` for a `(schema, name)` whose schema is unmounted returns
    false (planner / resolver decides what error to raise).
  - `Schemas` enumeration is the union of mounted schemas.
  - The string-indexer shim splits on `.` and defaults to `public`
    (verified end-to-end against a `system.udfs` lookup).
- `src/DatumIngest.Tests/Catalog/SystemCatalogTests.cs` (new):
  - `system.udfs` resolves and returns the UDFs provider.
  - `SupportsDdl` is false; `CreateTable` / `CreateSchema` throw.
- `src/DatumIngest.Tests/Catalog/VirtualCatalogTests.cs` (new):
  - `information_schema.schemata` enumerates the live mounted-schemas
    list on `TableCatalog`, not a hardcoded list. Test by mounting an
    extra backend on a custom schema and asserting it appears.
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
  `TableCatalog` that splits on `.` and delegates to the appropriate
  backend. The migration of every lookup site to `QualifiedName` happens
  in S4.
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

- [ ] `ITableCatalog` and `QualifiedName` exist and have unit coverage.
- [ ] `FlatFileCatalog` exists, owns the storage half formerly inside
      `TableCatalog`, and `SupportsDdl == true`.
- [ ] `SystemCatalog` and `VirtualCatalog` exist; old providers live
      inside them; `SupportsDdl == false`.
- [ ] `TableCatalog` keeps its name and public surface. Internally it
      mounts the three backends and delegates lookup + DDL storage steps.
- [ ] All ten system providers expose their canonical `(schema, name)`.
- [ ] `information_schema.schemata` enumerates live mounted-schema state.
- [ ] `ITransactionalCatalog` interface exists, with a comment pointing at
      the transactions roadmap; no implementation.
- [ ] All existing tests pass after rename — `Catalog` and `LanguageServer`
      subtrees both green.
- [ ] No new `.GetAwaiter().GetResult()` calls added (per the
      [async-bridge cleanup memory](../memory/project_async_sync_bridge_cleanup.md)).
