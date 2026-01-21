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

## Architectural shape

```
                ┌─────────────────────────────┐
   Planner ───► │       CatalogRouter         │
                │ schema → ITableCatalog map  │
                └──┬──────────┬───────────┬───┘
                   │          │           │
            ┌──────▼─────┐ ┌──▼──────┐ ┌──▼────────────┐
            │FlatFileCat.│ │SystemCat│ │VirtualCatalog │
            │ public,    │ │ system  │ │info_schema,   │
            │ user schms │ │         │ │datum_catalog  │
            └────────────┘ └─────────┘ └───────────────┘
                  │
            (today: scattered .datum files)
            (future: DatumDbCatalog — one .datumdb + WAL)
```

A `CatalogRouter` (what `TableCatalog` becomes) owns a `Dictionary<string
schema, ITableCatalog backend>`. Multiple schemas can map to the same backend
instance (one `FlatFileCatalog` typically owns `public` plus all
user-created schemas). System and virtual schemas are separate backends so
they can declare themselves read-only and project over in-memory state
without pretending to be physical tables.

Future-proofing: an `ITransactionalCatalog : ITableCatalog` interface with
`Begin/Commit/Rollback` is sketched in S1 but **not implemented** — it marks
the seam for the [transactions roadmap](../memory/project_transactions_and_incremental_backups.md).

## Phase sequence

Each phase has its own plan file in `plans/` and is written to be picked up
cold. Phases are mostly sequential; explicit pre-reqs are noted in each file.

| Phase | File | LOC | Depends on | What it does |
|-------|------|-----|------------|--------------|
| S1 | [schemas-s1-catalog-interface.md](schemas-s1-catalog-interface.md) | ~700 | — | Introduce `ITableCatalog`, `CatalogRouter`, `QualifiedName`. Extract `FlatFileCatalog` / `SystemCatalog` / `VirtualCatalog`. Move all existing providers under their real schemas. |
| S2 | [schemas-s2-manifest-v3.md](schemas-s2-manifest-v3.md) | ~80 | S1 | Bump `CatalogStore` to v3 schema-aware format. Reject v2. Per-backend opaque state blob. |
| S3 | [schemas-s3-parser-ddl.md](schemas-s3-parser-ddl.md) | ~250 | S1 | Parser + AST for `CREATE/DROP/ALTER TABLE schema.t`, `CREATE/DROP SCHEMA`, `SET search_path`. |
| S4 | [schemas-s4-resolver-and-search-path.md](schemas-s4-resolver-and-search-path.md) | ~400 | S1, S3 | `SchemaResolver` + `search_path` on `ExecutionContext`. Replace every flat-string concat lookup site. |
| S5 | [schemas-s5-language-server.md](schemas-s5-language-server.md) | ~300 | S1, S3, S4 | Drop hardcoded virtual-schema list. Schema-aware completion + diagnostics. |
| S6 | [schemas-s6-three-part-column-refs.md](schemas-s6-three-part-column-refs.md) | ~200 | S4 | (Optional) Three-part `schema.table.column` references. Skip unless real ambiguity hits. |

Total: ~1700 LOC core (S1–S5), ~1900 with S6.

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
