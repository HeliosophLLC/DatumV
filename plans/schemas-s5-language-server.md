# Schemas S5 — Language server: schema-aware completion + diagnostics

## Goal

Move the language server off its hardcoded virtual-schema list and onto the
live catalog. After this phase:

- Completion offers schema names and per-schema table lists from the actual
  catalog state, not a baked-in dictionary.
- Diagnostics distinguish "schema does not exist" from "table not in
  schema" from "table not in any schema on `search_path`."
- The schema manifest sent to the LSP is schema-grouped, not flat.

## Pre-reqs

- S1 (`TableCatalog` facade + `QualifiedName` — the LSP needs to walk live
  schemas).
- S3 (parser emits `SchemaName` on DDL records — completion needs to
  trigger after `CREATE TABLE schema.`).
- S4 (`search_path` exists — completion uses it for ranking unqualified
  suggestions).

## Locked decisions in scope

- LSP gets a snapshot of the catalog state, not a live reference. Snapshot
  is the existing schema-manifest channel — bump its shape.
- Hardcoded `KnownVirtualSchemas` dict in
  [SemanticAnalyzer.cs:919](../src/DatumIngest.LanguageServer/SemanticAnalyzer.cs#L919)
  goes away. Same for hardcoded `VirtualSchemaTables` in
  [CompletionProvider.cs:310](../src/DatumIngest.LanguageServer/CompletionProvider.cs#L310).
- After this phase, the LSP knows nothing about `information_schema` /
  `datum_catalog` specifically — they're just schemas the catalog happens
  to mount.

## What gets built

### Schema manifest format change

Today the LSP receives a flat tables-with-columns map. New shape:

```json
{
  "version": 2,
  "search_path": ["public", "system"],
  "schemas": [
    {
      "name": "public",
      "supports_ddl": true,
      "tables": [
        { "name": "users", "columns": [{"name": "id", "kind": "Int32"}, ...] }
      ]
    },
    {
      "name": "system",
      "supports_ddl": false,
      "tables": [
        { "name": "udfs", "columns": [...] },
        { "name": "procedures", "columns": [...] }
      ]
    },
    { "name": "information_schema", "supports_ddl": false, "tables": [...] },
    { "name": "datum_catalog", "supports_ddl": false, "tables": [...] }
  ]
}
```

`supports_ddl` mirrors `ITableCatalog.SupportsDdl` from S1 — drives
"don't suggest this schema after `CREATE TABLE `" in completion.
Per-table DML capability (can-INSERT, can-UPDATE) is a separate
question that lives on the provider; the LSP doesn't need it for any
diagnostic in this phase.

Locate the manifest producer (likely in `src/DatumIngest.LanguageServer/`
or wherever DevWeb assembles the manifest) and update both producer and
consumer. Bump `version: 2` so any cached older manifest is rejected.

### Modified files

- `src/DatumIngest.LanguageServer/SemanticAnalyzer.cs`:
  - Delete `KnownVirtualSchemas` (line 919).
  - Delete `IsKnownVirtualSchemaTable` helper.
  - Rewrite `TableReference` analysis (lines 274-305). Replace the
    `tableLookupName = $"{schema}.{name}"` flat-string check with:
    1. If `tableReference.SchemaName != null`:
       - Schema not in manifest → "Schema 'foo' does not exist."
       - Schema exists but table not in it → "Table 'foo' does not exist
         in schema 'bar'."
    2. Else (unqualified):
       - Walk `manifest.search_path`; first hit wins.
       - No hits → "Table 'foo' not found in any schema on search_path
         (public, system)." Include the search_path in the message.
  - `aliasToTable` registration uses the resolved `(schema, name)` pair as
    the key string `"schema.name"` for downstream column resolution.
    Comment explains this is a flat key for the alias map only — not a
    catalog lookup key.
- `src/DatumIngest.LanguageServer/CompletionProvider.cs`:
  - Delete `VirtualSchemaTables` constant (line 310).
  - New completion contexts:
    - **After `FROM ` / `JOIN ` / `INTO `** with no dot yet → suggest
      schema names (with trailing `.`) **and** unqualified table names
      from schemas on `search_path` (ranked by search_path order, so a
      `users` in `public` outranks one in `system`).
    - **After `FROM schema.`** → suggest tables in that schema only.
    - **After `CREATE TABLE `** / `DROP TABLE ` / `ALTER TABLE ` →
      schema-prefix completion (suggest schemas with `supports_ddl: true`
      only).
    - **After `CREATE TABLE schema.`** → suggest table names already in
      that schema (for `IF NOT EXISTS` discoverability) — low priority,
      can defer if completion infra is awkward.
  - New keyword completions: `SCHEMA` (as a follow-up to `CREATE` /
    `DROP`), `search_path` (as a follow-up to `SET`).
- LSP hover / signature help — verify nothing else hardcodes the virtual
  schema list. Grep for `information_schema` and `datum_catalog` strings
  across `src/DatumIngest.LanguageServer/`.

### Tests

- `src/DatumIngest.LanguageServer.Tests/SchemaAwareSemanticAnalyzerTests.cs`
  (new):
  - `SELECT * FROM information_schema.tables` — no warning (manifest now
    drives this, not a hardcoded dict).
  - `SELECT * FROM information_schema.nonexistent` — "Table 'nonexistent'
    does not exist in schema 'information_schema'."
  - `SELECT * FROM nonexistent_schema.foo` — "Schema 'nonexistent_schema'
    does not exist."
  - `SELECT * FROM udfs` with default search_path — no warning (resolves
    via `system` on search_path).
  - `SELECT * FROM unknown_table` — message includes the search_path
    listing.
- `src/DatumIngest.LanguageServer.Tests/SchemaAwareCompletionTests.cs`
  (new):
  - After `FROM ` — completion offers schemas (with `.`) and unqualified
    tables.
  - After `FROM information_schema.` — completion offers `tables`,
    `columns`, `schemata` only.
  - After `CREATE TABLE ` — `system` not in suggestions (`supports_ddl: false`).
- Update existing tests in
  [SemanticAnalyzerTests.cs:691-795](../src/DatumIngest.LanguageServer.Tests/SemanticAnalyzerTests.cs#L691)
  to match new diagnostic message text.

### What NOT to do

- **Don't change DevWeb table-listing UI.** It's already flat; schema
  grouping in the UI is a follow-up the user can decide on later. (No
  schema-grouped UI exists today per the survey.)
- **Don't add three-part column completion.** S6.
- **Don't try to make the LSP track live catalog mutations within a
  session.** Snapshot per request is the model.

## Test commands

```
dotnet test --no-restore --filter "FullyQualifiedName~LanguageServer"
```

## Done when

- [ ] `KnownVirtualSchemas` and `VirtualSchemaTables` constants deleted.
- [ ] Schema manifest is schema-grouped with `is_read_only` flag and
      `search_path`.
- [ ] Diagnostics differentiate the three failure modes (schema missing /
      table missing in schema / unqualified miss with search_path).
- [ ] Completion is schema-aware in all six contexts above.
- [ ] No remaining hardcoded mentions of `information_schema` or
      `datum_catalog` in LSP source (catalog drives the list).
- [ ] `LanguageServer` test subtree green.
