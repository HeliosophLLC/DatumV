# True schema support — overview

## Goal

Add real, first-class schemas to DatumIngest so a table is identified by a
`(schema, name)` pair end-to-end — at the storage abstraction, the planner, the
parser, the manifest, and the language server. Today schemas are a fiction:
table names are flat strings that happen to contain dots
(`information_schema.tables`, `system_udfs`), and the planner re-flattens any
parsed `schema.table` back into a flat string before lookup. This work moves
the abstraction up so that:

1. The planner asks a catalog "give me table `(schema, name)`," not "look up
   this flat string."
2. Schemas can be enumerated, created, and dropped as their own concept.
3. A future single-file `.datumdb` backend with a WAL is a clean swap of one
   `ITableCatalog` implementation, not a re-plumbing of the planner.

## Locked decisions

These were decided up front and apply to every phase. Don't re-litigate without
revisiting this doc.

1. **Rip & replace existing dotted names.** `system_udfs` → `system.udfs`,
   `system_procedures` → `system.procedures`, and the existing virtual schemas
   (`information_schema.*`, `datum_catalog.*`) become real schemas. No aliases,
   no dual-name resolution. Anything in tests, docs, or seed code that
   references the old flat names is updated in the same PR that renames them.
2. **No backward compatibility.** No v2-manifest reader, no on-disk migration
   step, no "system_udfs still works for one release" hedge. v3 manifest only;
   older catalogs are rejected with a clear "delete and start fresh" error.
3. **Postgres-style `search_path`.** Session-scoped immutable list, default
   `['public', 'system']`. `SET search_path = a, b, c` mutates the session
   context. Unqualified `CREATE TABLE` lands in the first writable schema.
   Aligns with the [PostgreSQL anchor memory](../memory/feedback_postgresql_anchor.md).
4. **`ITableCatalog` is the boundary.** Schemas become the natural mount
   point for catalog backends. `ITableProvider` stays as the per-table handle
   returned by a catalog, but the planner stops talking to providers directly.
   This is the boundary that lets a future `DatumDbCatalog` (single-file +
   WAL) drop in without engine work.
5. **Per-table mutation lives on the provider, not the catalog.** S0 strips
   the `BeginAppend(name)` / `AddColumn(name, ...)` / etc. wrappers off
   `TableCatalog` — callers do `catalog[name].X(...)`. A pure refactor done
   first to keep S1 focused.

## Architectural shape

```
                ┌──────────────────────────────────────┐
   Planner ───► │             TableCatalog             │
                │   (top-level facade)                 │
                │   - Plan / PlanAsync                 │
                │   - Pool                             │
                │   - UDF + Procedure registries       │
                │   - parent/child hierarchy           │
                │   - schema → ITableCatalog backend   │
                └──┬───────────────┬──────────────┬────┘
                   │               │              │
         ┌─────────▼─────────┐ ┌───▼───────┐ ┌────▼──────────┐
         │  FlatFileCatalog  │ │SystemCat. │ │VirtualCatalog │
         │  (was TableCat-   │ │  system   │ │info_schema,   │
         │   alog's storage) │ │           │ │datum_catalog  │
         │  public,          │ │           │ │               │
         │  user schemas     │ │           │ │               │
         └─────────┬─────────┘ └───────────┘ └───────────────┘
                   │
            (today: scattered .datum files)
            (future: DatumDbCatalog — one .datumdb + WAL)
```

`TableCatalog` keeps its name and its public surface — every existing caller
of `Plan`, `AddFile`, `FromFile`, `Pool`, `Dispose` is unaffected. Internally
it gains a `Dictionary<string schema, ITableCatalog backend>` and delegates
all table lookup + DDL storage steps to the backend that owns the schema.

The bulk of today's `TableCatalog` (the `Tables` dict, path resolution,
file-touching DDL appliers, persistent-table tracking) is **renamed** out
into `FlatFileCatalog` — the first concrete `ITableCatalog` implementation.
`SystemCatalog` and `VirtualCatalog` are small new backends that project
over in-memory state (UDF / Procedure registries, the live schema list)
and declare `SupportsDdl = false`. Multiple schemas can map to the same
backend instance — one `FlatFileCatalog` typically owns `public` plus all
user-created schemas.

There is no separate `CatalogRouter` class. The "router" is just a private
field on `TableCatalog` — `TableCatalog` is the catalog facade, period.

Future-proofing: an `ITransactionalCatalog : ITableCatalog` interface with
`Begin/Commit/Rollback` is sketched in S1 but **not implemented** — it marks
the seam for the [transactions roadmap](../memory/project_transactions_and_incremental_backups.md).

## Phase sequence

Each phase has its own plan file in `plans/` and is written to be picked up
cold. Phases are mostly sequential; explicit pre-reqs are noted in each file.

| Phase | File | LOC | Depends on | What it does |
|-------|------|-----|------------|--------------|
| S0 | [schemas-s0-table-mutation-on-provider.md](schemas-s0-table-mutation-on-provider.md) | ~150 | — | Pure refactor: drop the per-table mutation wrappers (`BeginAppend(name)` etc.) from `TableCatalog`; callers do `catalog[name].X(...)`. Independent of the schema work; lands first to keep S1's PR focused. |
| S1 | [schemas-s1-catalog-interface.md](schemas-s1-catalog-interface.md) | ~900 | S0 | Introduce `ITableCatalog`, `QualifiedName`. Rename current `TableCatalog` → `FlatFileCatalog` (the file-backed implementation). Build a new `TableCatalog` facade that owns the schema→backend map plus `Plan` / `Pool` / UDF + Procedure registries. Add `SystemCatalog` / `VirtualCatalog`. Move all existing providers under their real schemas. |
| S2 | [schemas-s2-manifest-v3.md](schemas-s2-manifest-v3.md) | ~80 | S1 | Bump `CatalogStore` to v3 schema-aware format. Reject v2. Per-backend opaque state blob. |
| S3 | [schemas-s3-parser-ddl.md](schemas-s3-parser-ddl.md) | ~250 | S1 | Parser + AST for `CREATE/DROP/ALTER TABLE schema.t`, `CREATE/DROP SCHEMA`, `SET search_path`. |
| S4 | [schemas-s4-resolver-and-search-path.md](schemas-s4-resolver-and-search-path.md) | ~400 | S1, S3 | `SchemaResolver` + `search_path` on `ExecutionContext`. Replace every flat-string concat lookup site. |
| S5 | [schemas-s5-language-server.md](schemas-s5-language-server.md) | ~300 | S1, S3, S4 | Drop hardcoded virtual-schema list. Schema-aware completion + diagnostics. |
| S6 | [schemas-s6-three-part-column-refs.md](schemas-s6-three-part-column-refs.md) | ~200 | S4 | (Optional) Three-part `schema.table.column` references. Skip unless real ambiguity hits. |

Total: ~2080 LOC core (S0–S5), ~2280 with S6.

## Cross-cutting reminders

- **Test filtering.** Per the [GPU-cost memory](../memory/feedback_test_suite_vram_cost.md),
  every test run on this work should scope to the relevant subtree
  (`Catalog`, `Parsing`, `LanguageServer`). Never an unfiltered run.
- **Parser `.Try()` discipline.** S3 adds new `CREATE` variants. Follow the
  [parser .Try() factoring memory](../memory/feedback_parser_try_factoring.md):
  factor the prefix-protected, body-unprotected pattern so error positions
  survive across `CREATE TABLE` / `CREATE SCHEMA` / `CREATE FUNCTION`
  branches.
- **`tasks.X` is not a schema.** Per the
  [tasks namespace memory](../memory/project_tasks_namespace_and_cascade.md),
  `tasks.classify` etc. is a *capability* layer over models, not a real
  schema. The `SchemaResolver` should not try to look it up as a table.
  Keep the dispatch path for `tasks.X` outside the catalog router.
- **`ITableProvider` rename is deferred.** Once `ITableCatalog` is the
  primary surface, `ITableProvider` may shrink or rename (`ITable`,
  `ITableHandle`). Don't fold this into S1 — finish the schema work first
  and assess the residual surface as a follow-up.
