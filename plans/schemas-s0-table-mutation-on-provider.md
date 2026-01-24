# Schemas S0 — Move per-table mutation off `TableCatalog`

## Goal

Pure refactor, no new behavior. Drop the per-table mutation convenience
methods from `TableCatalog` so that table-level operations live where
they belong — on `ITableProvider`. Done first to keep S1's PR focused on
the catalog interface and backend extraction without also churning every
mutation callsite.

## Pre-reqs

None. Independent of every other phase.

## What's wrong today

[TableCatalog.cs:1467-1530](../src/DatumIngest/Catalog/TableCatalog.cs#L1467)
hosts five methods that take a `tableName` string, resolve it to a
provider via `ResolveForMutation`, and call the same-named method on the
provider:

```csharp
public void AddColumn(string tableName, ColumnInfo column)
{
    ITableProvider provider = ResolveForMutation(tableName, p => p.CanAlterColumns, "AddColumn");
    provider.AddColumn(column);
}
// + DropColumn, BeginAppend, AppendRowsAsync, DeleteRows
```

The implementations themselves already live on `ITableProvider` —
[ITableProvider.cs:174-240](../src/DatumIngest/Catalog/ITableProvider.cs#L174)
defines `AddColumn`, `DropColumn`, `BeginAppend`, `AppendRowsAsync`,
`DeleteRows` with default `NotSupportedException` impls and per-method
`Can*` capability flags. The catalog wrappers are pure convenience for
string-keyed callers (mostly tests).

Production paths in [InsertExecutor.cs:127](../src/DatumIngest/Catalog/InsertExecutor.cs#L127),
[InsertExecutor.cs:256](../src/DatumIngest/Catalog/InsertExecutor.cs#L256),
and [DeleteExecutor.cs:116](../src/DatumIngest/Catalog/DeleteExecutor.cs#L116)
already call `provider.X(...)` directly — they don't go through the
catalog wrappers. So the wrappers serve no production purpose.

## What gets done

### Deleted

From [src/DatumIngest/Catalog/TableCatalog.cs](../src/DatumIngest/Catalog/TableCatalog.cs):

- `AddColumn(string tableName, ColumnInfo column)` (line 1467)
- `DropColumn(string tableName, string columnName)` (line 1477)
- `BeginAppend(string tableName)` (line 1489)
- `AppendRowsAsync(string tableName, IAsyncEnumerable<RowBatch>, CT)` (line 1499)
- `DeleteRows(string tableName, IReadOnlyList<long>)` (line 1509)
- `ResolveForMutation(...)` private helper (line 1516)

The `Can*` capability flag *consultation* goes away with the wrappers.
The capability flags themselves stay on `ITableProvider` (other code
reads them for non-mutation purposes — e.g.,
`datum_catalog.indexes.is_writable`-style projections, EXPLAIN output).

### Kept on `TableCatalog`

- `Remove(string tableName)` (line 1535) — this is *catalog-level*
  (deregister the table from the catalog's table dict), not provider-
  level. Not in scope.
- Everything else.

### Updated callsites — production

None in `src/`. Verified via grep that the only `src/` callsites to
`AddColumn` / `DropColumn` / `BeginAppend` / `AppendRowsAsync` /
`DeleteRows` going through the catalog are the wrappers themselves;
`InsertExecutor`, `DeleteExecutor`, and `UpdateExecutor` already call
`provider.X(...)` directly.

### Updated callsites — tests

Per the grep audit, ~30 callsites across these files. Pattern:
`catalog.X("t", ...)` → `catalog["t"].X(...)`.

- [tests/DatumIngest.Tests/Catalog/CatalogMutationTests.cs](../tests/DatumIngest.Tests/Catalog/CatalogMutationTests.cs)
  — heaviest hit; rename + restructure assertions where they relied on
  the catalog-side `KeyNotFoundException` (now you'd get `null` from
  `TryGetTable` or the provider's own `NotSupportedException`).
  - The "table doesn't exist" tests (line 193:
    `catalog.DropColumn("nope", "x")` expecting `KeyNotFoundException`)
    rewrite as `Assert.False(catalog.TryGetTable("nope", out _))`.
  - The "read-only table rejects mutation" test (line 183:
    `catalog.AddColumn("information_schema.tables", ...)` expecting an
    error) rewrite as
    `var p = catalog["information_schema.tables"]; Assert.Throws<NotSupportedException>(() => p.AddColumn(...));`
    The error message changes from the catalog's
    `"Table 'X' is read-only for AddColumn"` to the provider's default
    `"Table 'X' does not support AddColumn (CanAlterColumns is false)."`
    — update the message-substring assertion.
- [tests/DatumIngest.Tests/Catalog/StaleIndexDetectionTests.cs](../tests/DatumIngest.Tests/Catalog/StaleIndexDetectionTests.cs)
  — 4 callsites (lines 77, 111, 131, 146). Mechanical.
- [tests/DatumIngest.Tests/Catalog/ReindexExecutorTests.cs](../tests/DatumIngest.Tests/Catalog/ReindexExecutorTests.cs)
  — 5 callsites. Mechanical.
- [tests/DatumIngest.Tests/Catalog/IndexAutoExtensionTests.cs](../tests/DatumIngest.Tests/Catalog/IndexAutoExtensionTests.cs)
  — 6 callsites. Mechanical.
- [tests/DatumIngest.Tests/Catalog/AppendSessionTests.cs](../tests/DatumIngest.Tests/Catalog/AppendSessionTests.cs)
  — 2 catalog-side callsites (lines 265, 279). The other ~10 already
  go through `provider.BeginAppend()` directly.

### Updated callsites — docs

- [docs/datum-format.md:529-552](../docs/datum-format.md#L529) — the
  programmatic-API example block uses the catalog wrappers. Update to
  the resolve-then-call pattern. (Per the
  [docs-no-internal-refs memory](../memory/feedback_docs_no_internal_refs.md),
  describe the system as it is — no "this used to be on the catalog"
  hedging.)

## Tests

No new tests. The existing mutation test suite (`CatalogMutationTests`,
`AppendSessionTests`) already covers every code path that used the
wrappers; switching them to the resolve-then-call pattern keeps the
same assertions over the same provider behavior.

If anything, this phase *removes* a layer of indirection in the test
assertions — they now exercise the provider directly, which is what
they actually want to test.

## What NOT to do

- **Don't introduce `QualifiedName` yet.** S1 owns that. S0 keeps the
  string-keyed indexer (`catalog["t"]`) exactly as it is today.
- **Don't change `ITableProvider`.** No new methods, no signature
  tweaks. The default impls and capability flags are already correct.
- **Don't touch `Remove(string tableName)`** — it's catalog-level
  (deregisters from the table dict), not provider-level.
- **Don't touch the DDL appliers** (`ApplyCreateTable`,
  `ApplyDropTable`, `ApplyAlterTable*`). Those legitimately belong on
  the catalog because they handle SQL AST + validation + on-disk file
  creation/deletion. S1 will refactor where the *file-touching part*
  lives, but the AST-handling stays put.

## Test commands

```
dotnet test --no-restore --filter "FullyQualifiedName~Catalog"
```

Per the [GPU-cost memory](../memory/feedback_test_suite_vram_cost.md),
do not run the unfiltered suite.

## Done when

- [ ] `TableCatalog.AddColumn` / `DropColumn` / `BeginAppend` /
      `AppendRowsAsync` / `DeleteRows` / `ResolveForMutation` are
      deleted.
- [ ] All test callsites updated to `catalog["t"].X(...)`.
- [ ] `docs/datum-format.md` programmatic-API example updated.
- [ ] `Catalog` test subtree green.
- [ ] Grep for `catalog\.(AddColumn|DropColumn|BeginAppend|AppendRowsAsync|DeleteRows)\(` in the repo returns zero hits outside this file.

## Why this is a separate phase

S1 already does a lot: defining `ITableCatalog`, extracting three
backends, renaming `TableCatalog` → `FlatFileCatalog`, threading
`QualifiedName` through every provider. Bundling the per-table-method
removal on top would push the PR into "huge and scary" territory and
mix two unrelated changes (extracting an interface vs. moving methods
to where they belong). Doing S0 first means S1's diff is purely about
the catalog boundary.

S0 also stands alone — if the schema work stalls or descopes, the
cleaner provider-side mutation API is still a win.
