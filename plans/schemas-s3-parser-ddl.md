# Schemas S3 — Parser & AST for qualified DDL

## Goal

Teach the SQL surface about schemas:

- `CREATE TABLE schema.t (...)` / `DROP TABLE schema.t` /
  `ALTER TABLE schema.t ...` accept qualified names.
- `CREATE SCHEMA name [IF NOT EXISTS]` and
  `DROP SCHEMA name [IF EXISTS] [CASCADE | RESTRICT]` exist.
- `SET search_path = a, b, c` parses into a session-mutation statement.

The parser already handles `FROM schema.table` (S1's planner still
relies on that path via the temporary string indexer on `TableCatalog`).
This phase generalizes the same shape across DDL and adds the new
schema-lifecycle statements.

## Pre-reqs

- S1 (`ITableCatalog` + `QualifiedName` + `TableCatalog` facade with
  mounted backends). DDL appliers in `TableCatalog` need somewhere to
  call `CreateSchema` / `DropSchema`.

## Locked decisions in scope

- Postgres-flavored syntax — see [PG-anchor memory](../memory/feedback_postgresql_anchor.md).
  No T-SQL drift.
- `SET search_path` is the canonical name (Postgres). Comma-separated
  identifiers, no quoting required for simple names.
- Default search_path is `['public', 'system']` — defined in S4, but S3's
  `SET search_path` parser must accept any identifier list including
  built-in schema names.

## What gets built

### AST changes (`src/DatumIngest.Parsing/Ast/AstNodes.cs`)

Existing records gain `SchemaName: string?` (positional, nullable, default
`null`):

```csharp
public sealed record CreateTableStatement(
    string TableName,
    IReadOnlyList<ColumnDefinition> Columns,
    bool IsTemp = false,
    bool IfNotExists = false,
    string? StoragePath = null,
    string? SchemaName = null) : Statement;

public sealed record DropTableStatement(
    string TableName,
    bool IfExists = false,
    string? SchemaName = null) : Statement;

public sealed record AlterTableAddColumnStatement(
    string TableName,
    ColumnDefinition Column,
    string? SchemaName = null) : Statement;

public sealed record AlterTableDropColumnStatement(
    string TableName,
    string ColumnName,
    string? SchemaName = null) : Statement;
```

`TableReference` already has `SchemaName` — no change.

New records:

```csharp
public sealed record CreateSchemaStatement(
    string SchemaName,
    bool IfNotExists = false) : Statement;

public sealed record DropSchemaStatement(
    string SchemaName,
    bool IfExists = false,
    bool Cascade = false) : Statement;

public sealed record SetSearchPathStatement(
    IReadOnlyList<string> Schemas) : Statement;
```

### Parser changes (`src/DatumIngest.Parsing/SqlParser.cs`)

- **Factor a reusable combinator** for qualified table names. Today's
  inline pattern from `TableReferenceParser` (lines 1604-1622) gets pulled
  out:
  ```csharp
  private static readonly TokenListParser<SqlToken, (string? Schema, string Name, Position Span)>
      QualifiedTableNameParser =
          from first in IdentifierLikeToken
          from rest in (
              from dot in Token.EqualTo(SqlToken.Dot)
              from second in IdentifierLikeToken
              select second
          ).OptionalOrDefault()
          select rest.HasValue
              ? ((string?)GetTokenText(first), GetTokenText(rest), Span(first, rest))
              : (null, GetTokenText(first), Span(first));
  ```
  Reused in: `CreateTableParser`, `DropTableParser`, `AlterTableParser`,
  `TableReferenceParser` (replacing the existing inline logic).
- **`CREATE SCHEMA` parser.** New. Branches off `CREATE` after the keyword.
  Watch the [parser .Try() factoring memory](../memory/feedback_parser_try_factoring.md):
  the existing `CREATE` parser branches to TABLE / FUNCTION / PROCEDURE /
  TEMP TABLE. Adding SCHEMA needs the same prefix-protected, body-unprotected
  pattern — protect the `CREATE <branch-keyword>` prefix with `.Try()`,
  leave the body unprotected so deep errors surface accurately.
- **`DROP SCHEMA` parser.** Same factoring discipline — `DROP` already
  branches across TABLE/FUNCTION/PROCEDURE; add SCHEMA.
- **`SET search_path` parser.** New top-level statement. Pattern:
  `SET search_path = identifier (, identifier)*`. Permissive: allow `TO`
  in place of `=` (Postgres accepts both).
- **CREATE TABLE / DROP TABLE / ALTER TABLE** — switch their table-name
  parsers to `QualifiedTableNameParser` and pass `SchemaName` into the AST.

### TableCatalog DDL appliers

These already exist; just need to thread through schemas. The actual
*execution* of `CREATE SCHEMA` etc. against the backends happens here,
since the backends already exposed `CreateSchema`/`DropSchema` in S1.

- `ApplyCreateTable` — resolves `(create.SchemaName ?? "public", create.TableName)`
  to a `QualifiedName`, looks up the writable backend at that schema in
  `TableCatalog`'s backend dict, calls `CreateTable`. (Search-path-based
  resolution for unqualified names lands in S4. For S3, unqualified
  means `public`.)
- `ApplyDropTable` — same shape.
- `ApplyAlterTable*` — same shape.
- New `ApplyCreateSchema` — calls `_backends["public"].CreateSchema(name)`
  (the new schema lands in `FlatFileCatalog`) and updates `TableCatalog`'s
  mounted-schema dict so the new schema also resolves through that
  backend.
- New `ApplyDropSchema` — `TableCatalog`-level, dispatches to the owning
  backend. CASCADE deletes all tables in the schema first; RESTRICT
  (default) errors if non-empty.
- New `ApplySetSearchPath` — for S3, parse-and-apply lives on the
  `ExecutionContext`, but the actual context plumbing lands in S4. For S3,
  it can be a no-op stub that throws `NotImplementedException("S4")` so
  tests don't false-pass.

### Tests

- `src/DatumIngest.Parsing.Tests/QualifiedNameParserTests.cs` (new):
  `CREATE TABLE myapp.users (...)` round-trips; `DROP TABLE myapp.users`,
  `ALTER TABLE myapp.users ADD COLUMN c INT`, etc.
- `src/DatumIngest.Parsing.Tests/SchemaDdlParserTests.cs` (new):
  - `CREATE SCHEMA foo` / `CREATE SCHEMA IF NOT EXISTS foo`.
  - `DROP SCHEMA foo` / `DROP SCHEMA IF EXISTS foo CASCADE`.
  - `SET search_path = a, b, c` and `SET search_path TO a, b, c`.
- `src/DatumIngest.Tests/Catalog/SchemaDdlExecutionTests.cs` (new):
  - `CREATE SCHEMA myapp; CREATE TABLE myapp.t (id INT)` succeeds and
    `TableCatalog` lists `myapp` in its mounted-schema enumeration and
    `myapp.t` in `ListTables("myapp")`.
  - `DROP SCHEMA myapp` on non-empty schema fails (RESTRICT).
  - `DROP SCHEMA myapp CASCADE` succeeds and removes the table.
  - `CREATE TABLE system.foo (id INT)` fails (read-only backend).

### What NOT to do

- **No `search_path` resolution.** S4 owns the resolver and the
  context-state plumbing. S3 just parses `SET search_path`.
- **No three-part column references** (`schema.table.column`). S6.
- **No LSP changes.** S5.
- **No removal of the temporary string-indexer shim on `TableCatalog`** if
  S2 didn't already get to it — wait until S4 churns those call sites
  anyway.

## Test commands

```
dotnet test --no-restore --filter "FullyQualifiedName~Parsing|FullyQualifiedName~Catalog"
```

## Done when

- [ ] `QualifiedTableNameParser` exists and is used by all four DDL
      statements + `TableReference`.
- [ ] `CreateSchemaStatement`, `DropSchemaStatement`,
      `SetSearchPathStatement` round-trip through parse/print tests.
- [ ] DDL appliers in `TableCatalog` call the appropriate backend's
      schema APIs.
- [ ] CASCADE / RESTRICT behavior on `DROP SCHEMA` is tested.
- [ ] `Parsing` and `Catalog` test subtrees green.
- [ ] No `.Try()` regressions per the parser-factoring memory — verify by
      checking error positions on a deliberately-malformed
      `CREATE SCHEMA foo BAD_TOKEN` query.
