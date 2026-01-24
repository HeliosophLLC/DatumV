# Schemas S4 — `SchemaResolver` and `search_path`

## Goal

Replace every flat-string concat lookup site (`$"{schema}.{name}"`) with a
single resolution path through a `SchemaResolver`. Add Postgres-style
`search_path` to `ExecutionContext`. After this phase, the planner and DDL
appliers no longer construct flat-name strings — they resolve a
`TableReference` (with optional `SchemaName`) to a `QualifiedName` exactly
once, at the boundary, and pass that around.

This is the phase that closes the loop opened in S1 (catalog boundary) and
S3 (parser surface).

## Pre-reqs

- S1 (`TableCatalog` facade with mounted backends + `QualifiedName`).
- S3 (`SetSearchPathStatement` parses; AST DDL records have `SchemaName`).
- S2 helps but isn't strictly required — the resolver doesn't touch
  manifest format.

## Locked decisions in scope

- `search_path` is an **immutable** ordered list on `ExecutionContext`,
  copy-on-mutate. Default `['public', 'system']`. No mutable AsyncLocal
  (would bleed across async scopes — see
  [one-arena-per-query memory](../memory/project_one_arena_per_query.md)
  for the analogous lesson).
- `SET search_path = ...` produces a *new* `ExecutionContext` with the
  updated path. Sessions hold the most recent context.
- Resolution rules:
  - Explicit schema (`schema.table`): exact lookup; no fallback.
    Resolution failure → `SchemaResolutionException` listing the schema
    that wasn't found vs. the table that wasn't in the schema.
  - Unqualified (`table`): walk `search_path` in order; first hit wins.
    Failure lists every schema attempted.
- `ResolveForCreate` for `CREATE TABLE` without explicit schema picks the
  **first writable** schema in `search_path` (skips read-only backends like
  `system`).
- `tasks.X` (per [tasks namespace memory](../memory/project_tasks_namespace_and_cascade.md))
  is a *function/capability* dispatch path, not a table — the resolver
  must not see it.

## What gets built

### New files

- `src/DatumIngest/Catalog/SchemaResolver.cs`:
  ```csharp
  public sealed class SchemaResolver
  {
      private readonly TableCatalog _catalog;
      private readonly IReadOnlyList<string> _searchPath;

      public SchemaResolver(TableCatalog catalog, IReadOnlyList<string> searchPath) { ... }

      public QualifiedName Resolve(TableReference tableRef)
      {
          if (tableRef.SchemaName is not null)
          {
              var qn = new QualifiedName(tableRef.SchemaName, tableRef.Name);
              if (!_catalog.TryGetTable(qn, out _))
                  throw new SchemaResolutionException(/* schema-or-table-missing */);
              return qn;
          }
          foreach (var schema in _searchPath)
          {
              var qn = new QualifiedName(schema, tableRef.Name);
              if (_catalog.TryGetTable(qn, out _)) return qn;
          }
          throw new SchemaResolutionException(/* not found in [search_path] */);
      }

      public QualifiedName ResolveForCreate(string? explicitSchema, string name)
      {
          if (explicitSchema is not null)
          {
              var backend = _catalog.ResolveBackend(explicitSchema);
              if (!backend.SupportsDdl)
                  throw new SchemaResolutionException(/* schema does not accept DDL */);
              return new QualifiedName(explicitSchema, name);
          }
          foreach (var schema in _searchPath)
          {
              if (_catalog.TryResolveBackend(schema, out var backend) && backend.SupportsDdl)
                  return new QualifiedName(schema, name);
          }
          throw new SchemaResolutionException(/* no DDL-capable schema in search_path */);
      }
  }
  ```

  Note: `TableCatalog` exposes `TryGetTable(QualifiedName)`,
  `ResolveBackend(string)`, and `TryResolveBackend(string, out)` as
  internal-or-public methods on the facade. These are added in S1 alongside
  the private backend dict.
- `src/DatumIngest/Execution/SchemaResolutionException.cs` — sealed; carries
  the original `TableReference` and the search-path snapshot for diagnostics.

### Modified files

- `ExecutionContext` (locate via `src/DatumIngest/Execution/ExecutionContext.cs`):
  - New `IReadOnlyList<string> SearchPath { get; }` property, defaults to
    `["public", "system"]`.
  - New `SchemaResolver SchemaResolver { get; }` — lazy-built from the
    catalog + search path.
  - New `WithSearchPath(IReadOnlyList<string> path) → ExecutionContext`
    immutable copy-mutator. Copy-on-write so prior contexts captured by
    in-flight queries are unaffected.
- `TableCatalog`:
  - Add `ApplySetSearchPath(SetSearchPathStatement, ExecutionContext)`
    returning the new context. (Or hand off to the session manager —
    depends on where S3 left the stub.)
  - Validate every schema in the path exists; reject otherwise. Postgres
    accepts unknown schemas silently with a warning, but for our scale
    erroring is friendlier — no silent typos.
- **Replace every flat-string lookup site.** Audit grep target:
  `\$"\{[^}]*SchemaName[^}]*\}\.\{`. Concrete known sites (verify in source
  before editing — files have churned):
  - [src/DatumIngest/Execution/QueryPlanner.cs:4639](../src/DatumIngest/Execution/QueryPlanner.cs#L4639)
    — `tableLookupKey = ...` becomes `var qn = ctx.SchemaResolver.Resolve(tableRef);`
  - [src/DatumIngest/Catalog/InsertExecutor.cs](../src/DatumIngest/Catalog/InsertExecutor.cs)
    — table resolution at INSERT entry.
  - [src/DatumIngest/Catalog/DeleteExecutor.cs](../src/DatumIngest/Catalog/DeleteExecutor.cs)
    — table resolution at DELETE entry.
  - [src/DatumIngest/Catalog/UpdateExecutor.cs](../src/DatumIngest/Catalog/UpdateExecutor.cs)
    — table resolution at UPDATE entry.
  - `TableCatalog.ApplyCreateTable` / `ApplyDropTable` / `ApplyAlterTable*`
    — call `ResolveForCreate` (CREATE) or `Resolve` (DROP/ALTER).
  - The temporary string indexer on `TableCatalog` from S1 — **delete it
    now**. Audit any remaining callers and route them through the
    resolver.
- `ColumnReference` resolution in `ExpressionTypeResolver`
  ([line 141](../src/DatumIngest/Execution/ExpressionTypeResolver.cs#L141))
  — unchanged. Three-part `schema.table.column` is S6.

### Tests

- `src/DatumIngest.Tests/Catalog/SchemaResolverTests.cs` (new):
  - Explicit-schema lookup hits, misses (schema-missing vs table-missing
    diagnostics).
  - Unqualified lookup walks search_path in order; first hit wins.
  - Unqualified miss lists every schema attempted in the exception.
  - `ResolveForCreate` skips backends with `SupportsDdl == false`.
  - `ResolveForCreate` errors when no DDL-capable schema in search_path.
- `src/DatumIngest.Tests/Execution/SearchPathTests.cs` (new):
  - Default search_path resolves `udfs` to `system.udfs` (because `system`
    is on the default path) — verify the rip & replace from S1 is
    reachable via unqualified lookup.
  - `SET search_path = myapp, public` puts `myapp` first.
  - `SET search_path` to a non-existent schema errors.
  - In-flight query holding an old context isn't affected by a `SET`
    that runs concurrently.
- Update existing planner/DDL tests that constructed flat lookup keys by
  hand. Audit needed — there shouldn't be many, since most go through SQL
  parsing.

### What NOT to do

- **Don't add three-part column references.** S6.
- **Don't change LSP behavior.** S5.
- **Don't change the manifest format.** S2 owns that.
- **Don't try to make `tasks.X` flow through the resolver.** It's a
  function-dispatch path that lives in `FunctionRegistry`. Resolver should
  reject it (or simply never see it because the parser routes `tasks.X(...)`
  to the function path, not `TableReference`). Verify no accidental path
  exists where a user could write `FROM tasks.classify` and confuse the
  resolver.

## Test commands

```
dotnet test --no-restore --filter "FullyQualifiedName~Catalog|FullyQualifiedName~Execution"
```

## Done when

- [ ] `SchemaResolver` exists with full unit coverage.
- [ ] `ExecutionContext.SearchPath` exists; default `["public", "system"]`;
      `WithSearchPath` is copy-on-write.
- [ ] `SET search_path = ...` updates the session context.
- [ ] Every `$"{schema}.{name}"` flat-string lookup site is replaced with
      a `SchemaResolver.Resolve` call. Grep confirms zero remaining.
- [ ] The temporary string indexer added to `TableCatalog` in S1 is
      deleted.
- [ ] Unqualified `system.*` table refs (e.g., `SELECT * FROM udfs` while
      `system` is on the search path) work end-to-end.
- [ ] Default search_path can be overridden per-session without leaking
      across concurrent queries.
- [ ] `Catalog` and `Execution` test subtrees green.
