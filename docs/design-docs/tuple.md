# Tuple Destructuring Plan

## Overview

Add two destructuring syntaxes to LET bindings:

- Positional: `LET (a, b) = expr`
- Named: `LET {x, y} = expr`

Positional destructuring extracts by position from `Vector`, `Array`, or `Struct`. Named destructuring extracts by field name from `Struct` and is order-independent.

The implementation strategy is planner-level desugaring. A destructured LET binding expands into one hidden memoized binding for the right-hand side plus one ordinary binding per extracted element. That keeps the existing projection pipeline intact and avoids re-evaluating the source expression.

## Syntax

### Positional

```sql
SELECT
  LET (sin_value, cos_value) = cyclical_encode(month, 12),
  sin_value,
  cos_value
FROM orders;
```

- Applies to `Vector`, `Array`, and `Struct`
- Uses ordinal extraction
- Requires at least two names

### Named

```sql
SELECT
  LET {label, bbox} = detect_objects(image),
  label,
  bbox
FROM images;
```

- Applies to `Struct` only
- Matches by field name, not declaration order
- Requires at least two names

### Rules

- No `AS` alias on destructured bindings
- `LET (a) = expr` is rejected; use `LET a = expr`
- Duplicate names in a destructure pattern are invalid

## Core Design

The hot-path requirement is that destructuring must not re-run the right-hand side expression once per extracted name. The design therefore desugars destructuring before the planner does aggregate or window rewriting.

Example:

```sql
LET (a, b) = cyclical_encode(month, 12)
```

becomes:

```sql
LET __destructure_0 = cyclical_encode(month, 12)
LET a = __destructure_0[0]
LET b = __destructure_0[1]
```

Example:

```sql
LET {label, bbox} = detect_objects(image)
```

becomes:

```sql
LET __destructure_0 = detect_objects(image)
LET label = __destructure_0['label']
LET bbox = __destructure_0['bbox']
```

This gives three benefits:

1. The right-hand side is evaluated once and memoized in the augmented row.
2. Aggregate rewriting, window rewriting, and QUALIFY substitution continue to operate on ordinary `LetBinding` instances.
3. ProjectOperator does not need a special destructuring path.

## AST Changes

Extend the LET binding model to represent destructuring explicitly before planner expansion.

### New types

```csharp
public enum DestructureMode
{
    Positional,
    Named
}

public sealed record DestructurePattern(
    IReadOnlyList<string> Names,
    DestructureMode Mode,
    SourceSpan? Span = null);
```

### LetBinding extension

Current shape:

```csharp
LetBinding(string Name, Expression Expression, string? OutputAlias, SourceSpan? Span)
```

Proposed shape:

```csharp
LetBinding(
    string Name,
    Expression Expression,
    string? OutputAlias = null,
    SourceSpan? Span = null,
    DestructurePattern? Destructure = null)
```

For ordinary LET bindings, `Destructure` is `null`. For destructured bindings, `Name` is a placeholder and `OutputAlias` remains `null`.

## Parser Plan

Update the LET parser to accept three left-hand side forms:

```text
LET <identifier> = <expression> [AS <alias>]
LET ( <identifier> [, <identifier>]+ ) = <expression>
LET { <identifier> [, <identifier>]+ } = <expression>
```

Notes:

- `LeftParen` and `RightParen` already exist
- `LeftBrace` and `RightBrace` already exist and are already used for struct literals
- There is no ambiguity with struct literals because destructuring is parsed only on the left of `=` inside LET

## Planner Expansion

Add a new expansion pass in the planner before aggregate and window rewriting.

### Expansion algorithm

For each LET binding:

- If it is a regular binding, keep it unchanged
- If it is positional destructuring:
  - Emit one hidden binding named `__destructure_N` for the original expression
  - Emit one binding per name using integer index access
- If it is named destructuring:
  - Emit one hidden binding named `__destructure_N` for the original expression
  - Emit one binding per name using string field access

### Positional extraction targets

- `Vector` -> `IndexAccessExpression` with `0`-based integer index
- `Array` -> `IndexAccessExpression` with `0`-based integer index
- `Struct` -> positional extraction against the struct field order

### Named extraction targets

- `Struct` -> `IndexAccessExpression` with string field name

### Why planner expansion is the right place

- Aggregate rewriting already traverses `letBindings` and rewrites `binding.Expression`
- Window rewriting already does the same
- `ResolveLetBindingReferences()` already substitutes by `binding.Name`
- Grouping inference for `GROUP BY ALL` stays correct because hidden bindings have no output alias

## Runtime Changes

Only one runtime evaluator gap needs to be closed.

### Vector index access

`EvaluateIndexAccess()` already handles `Array` and `Struct`. It should also handle `Vector`:

```csharp
if (source.Kind == DataKind.Vector)
{
    float[] vector = source.AsVector();
    int position = (int)ToFloat(index);
    if (position < 0 || position >= vector.Length)
    {
        return DataValue.Null;
    }

    return DataValue.From(vector[position]);
}
```

That is sufficient for positional destructuring over vector-returning functions such as `cyclical_encode()`.

## Type Resolution

The type resolver needs to understand the extracted element type.

### Positional destructuring

- `Vector` -> each extracted name is `Float32`
- `Array` -> each extracted name is the array element kind
- `Struct` -> each extracted name resolves from `ColumnInfo.Fields` by ordinal

### Named destructuring

- `Struct` -> each extracted name resolves from `ColumnInfo.Fields` by field name

## Semantic Analysis

Add diagnostics for the following cases:

1. Duplicate names in the same destructure pattern
2. Named destructuring against a non-struct expression
3. Unknown struct field name when the schema is statically known
4. Arity mismatch when the output width is statically known
5. Name shadowing of an existing source column or prior LET binding

The language server should also surface destructured names in completion lists for subsequent LET bindings and projected expressions.

## Allocation and GC Expectations

Allocation-wise, the design is favorable.

- Per-row runtime allocation should be effectively unchanged relative to evaluating the source expression without destructuring.
- The hidden binding memoizes the source result once, so destructuring does not multiply allocations by the number of extracted names.
- Extracted scalar values are copied as `DataValue` structs into the augmented row, which avoids new heap allocations for the destructuring step itself.
- The main new allocations are planner-time AST objects created once per query, which are short-lived Gen0 allocations.

The dominant allocation cost remains whatever the source expression already allocates. For example, a function returning a new vector per row still allocates that vector once per row, but destructuring does not add a second vector allocation.

## Test Plan

### Parser tests

1. Parse positional destructuring with two names
2. Parse positional destructuring with three or more names
3. Parse named destructuring with two names
4. Parse mixed LET lists containing both simple and destructured bindings
5. Reject empty destructure patterns
6. Reject single-element destructure patterns

### Execution tests

1. Positional destructuring from a vector
2. Positional destructuring from an array
3. Positional destructuring from a struct
4. Named destructuring from a struct with reordered fields
5. Chained LET bindings consuming destructured names
6. QUALIFY using destructured names
7. Aggregate contexts using destructured names
8. Window contexts using destructured names
9. Null propagation through destructured bindings
10. Memoization proof that the source expression runs once per row

### Semantic tests

1. Duplicate-name diagnostic
2. Non-struct named-destructure diagnostic
3. Unknown-field diagnostic
4. Arity-mismatch diagnostic when width is known

## Files Expected to Change

- `src/DatumIngest.Parsing/Ast/AstNodes.cs`
- `src/DatumIngest.Parsing/SqlParser.cs`
- `src/DatumIngest/Execution/QueryPlanner.cs`
- `src/DatumIngest/Execution/ExpressionEvaluator.cs`
- `src/DatumIngest/Execution/ExpressionTypeResolver.cs`
- `src/DatumIngest.LanguageServer/SemanticAnalyzer.cs`
- `tests/DatumIngest.Tests/Execution/LetBindingTests.cs`
- `docs/sql.md`

## Verification

1. Build the solution successfully
2. Run the full test suite successfully
3. Smoke test vector destructuring
4. Smoke test named struct destructuring
5. Confirm language-server completion and diagnostics for destructured names