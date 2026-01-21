# Schemas S6 — Three-part column references (optional)

## Goal

Support `schema.table.column` in expressions, not just `table.column`.
Useful when joining across schemas and an alias collides:

```sql
SELECT public.users.id, system.udfs.id
FROM public.users JOIN system.udfs ON ...
```

This phase is **optional**. Skip unless you hit real ambiguity in actual
queries. The cost is non-trivial because `ColumnReference` is touched in
many places.

## Pre-reqs

- S4 (`SchemaResolver` + `search_path` exist).

## Locked decisions in scope

- `schema.table.column` is parsed eagerly when three identifiers are
  separated by dots. Two-identifier forms remain `(table, column)` —
  the lexer/parser can't disambiguate `schema.table` vs `alias.column`
  without context.
- Resolution: schema must exist; table must exist in schema; column must
  exist on table. No `search_path` walk for three-part — explicit means
  explicit.

## What gets built

### AST changes (`src/DatumIngest.Parsing/Ast/AstNodes.cs`)

```csharp
public sealed record ColumnReference(
    string? SchemaName,   // new, nullable
    string? TableName,
    string ColumnName,
    SourceSpan? Span = null) : Expression;
```

Two-arg call sites become three-arg with `SchemaName: null`.

### Parser changes (`src/DatumIngest.Parsing/SqlParser.cs`)

The current column-ref parser (lines 125-136) reads
`identifier (. identifier)?`. Extend to
`identifier (. identifier (. identifier)?)?`:

```csharp
from first in IdentifierLikeToken
from rest in (
    from dot in Token.EqualTo(SqlToken.Dot)
    from second in IdentifierLikeToken.Or(StarToken)
    from third in (
        from dot2 in Token.EqualTo(SqlToken.Dot)
        from t in IdentifierLikeToken.Or(StarToken)
        select t
    ).OptionalOrDefault()
    select (Second: second, Third: third)
).OptionalOrDefault()
select rest switch
{
    { HasValue: false }              => new ColumnReference(null, null,                   GetTokenText(first)),
    { Third.HasValue: false }        => new ColumnReference(null, GetTokenText(first),    GetTokenText(rest.Second)),
    _                                => new ColumnReference(GetTokenText(first), GetTokenText(rest.Second), GetTokenText(rest.Third))
};
```

Handle `schema.table.*` (Star token in third position) for SELECT lists.

### Resolution changes

- `ResolvedQuerySchema.FindColumn` (`src/DatumIngest/Catalog/ResolvedQuerySchema.cs`,
  lines 87-100): extend lookup to consider schema. The qualified-name key
  format becomes `"schema.table.column"` for three-part refs and stays
  `"table.column"` for two-part — keep both forms in the index.
- `ExpressionTypeResolver`
  ([line 141](../src/DatumIngest/Execution/ExpressionTypeResolver.cs#L141)):
  three-part path goes through the resolver to confirm schema+table exist
  before column lookup.

### LSP changes

- Completion: after `schema.table.` suggest column names. After `schema.`
  suggest tables (already in S5). After `schema.table.` suggest columns +
  `*`.
- Diagnostics: distinguish "schema X.Y has no column Z" from
  "no table Y in schema X."

### Tests

- `src/DatumIngest.Parsing.Tests/ThreePartColumnRefTests.cs`:
  parse `public.users.id`, `myapp.orders.*`, mixed two/three-part.
- `src/DatumIngest.Tests/Execution/ThreePartColumnResolutionTests.cs`:
  cross-schema join with collision; resolves correctly.
- `src/DatumIngest.LanguageServer.Tests/ThreePartColumnCompletionTests.cs`:
  completion contexts for all three depths.

### What NOT to do

- **Don't change `TableReference`.** That's two-part max (`schema.table`).
- **Don't add four-part references** (`db.schema.table.column`). DatumIngest
  has no concept of multiple databases — and per the
  [transactions roadmap](../memory/project_transactions_and_incremental_backups.md)
  the catalog is the unit of atomicity; a "database" layer would need to
  come from somewhere else first.

## Test commands

```
dotnet test --no-restore --filter "FullyQualifiedName~Parsing|FullyQualifiedName~Execution|FullyQualifiedName~LanguageServer"
```

## Done when

- [ ] `ColumnReference` has `SchemaName`.
- [ ] Parser accepts three-part column refs including `schema.table.*`.
- [ ] Resolution distinguishes the three failure modes (no schema / no
      table in schema / no column on table).
- [ ] Completion supports the three depths.
- [ ] All three test subtrees green.

## Reasons to skip

- No real-world query has triggered alias-collision pain yet.
- Three-part adds parser ambiguity surface area (especially around `*`).
- Once `DatumDbCatalog` lands, you may want to revisit the whole expression
  resolver anyway for transaction-aware reads — better to defer this until
  that surface stabilizes.
