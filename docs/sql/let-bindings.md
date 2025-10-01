---
title: LET Bindings
---

`LET` declares named, memoized intermediate expressions in the SELECT list. Each binding is evaluated once per row and its value is cached for all subsequent references. LET bindings are **not included in the output** unless explicitly aliased with `AS`.

```sql
SELECT
  LET tensor = image_to_tensor_hwc(image),
  LET features = reshape(tensor, 16, 16),
  rank(tensor) AS ndim,
  vec_mean(features) AS average
FROM data
-- Result columns: ndim, average
```

#### Syntax

```
LET <identifier> = <expression> [AS <alias>]
LET (<name1>, <name2>, ...) = <expression>
LET {<field1>, <field2>, ...} = <expression>
```

- **`<identifier>`** — the binding name, used to reference the cached value in later LET bindings and SELECT columns.
- **`AS <alias>`** — when present, the LET value appears in the output with this column name.
- All LET bindings must precede regular output columns in the SELECT list.
- Later LET bindings can reference earlier ones (left-to-right chaining).
- `SELECT *` expands source table columns only — never includes LET bindings (aliased or not).

#### Output visibility

By default, LET bindings are hidden from the result set. To include a binding's value in the output, add `AS <alias>`:

```sql
SELECT
  LET total = price * quantity AS "line_total",  -- output as "line_total"
  LET tax = total * 0.08,                        -- hidden from output
  total + tax AS final_price
FROM orders
-- Result columns: line_total, final_price
```

#### Tuple Destructuring

A LET binding can unpack a multi-valued result into several named variables in one step.

**Positional** — extracts by zero-based index. Supported on Array, Vector, and Struct:

```sql
-- Unpack a 2-element Vector (e.g. cyclical_encode output)
SELECT LET (sin_v, cos_v) = cyclical_encode(month, 12),
       sin_v AS s, cos_v AS c
FROM events

-- Unpack a float array column
SELECT LET (r, g, b) = pixel, r, g, b FROM images
```

**Named** — extracts by field name. Supported on Struct only; field order in the pattern is independent of the struct's declaration order:

```sql
-- Extract named fields from a struct literal
SELECT LET {alpha, beta} = {beta: 8.0, alpha: 7.0},
       alpha AS av, beta AS bv
FROM data

-- Named destructure of a scalar LET alias
SELECT LET s = {score: 0.9, label: 'cat'},
       LET {score, label} = s,
       score, label
FROM predictions
```

The source expression is evaluated **once per row** regardless of how many names are extracted. Destructured names are plain LET bindings and can be used in subsequent LET expressions. Named destructuring on a Vector or Array is a runtime error — use positional destructuring instead.

#### Memoization

LET expressions are computed once per row. This is value caching, not textual macro expansion. Functions with side-effects like `uuidv4()` produce a single value that is reused for all references within the same row:

```sql
SELECT
  LET id = uuidv4(),
  uuid_str(id) AS first,
  uuid_str(id) AS second
FROM data
-- first and second are always identical for each row
```

#### Clause interactions

| Clause | Can reference LET bindings? | Notes |
|---|---|---|
| WHERE | No | Evaluated before SELECT |
| JOIN ON | No | Evaluated before SELECT |
| GROUP BY | — | LET expressions follow the same rules as SELECT expressions: must be aggregates or grouping keys |
| HAVING | No | Evaluated before SELECT |
| QUALIFY | Yes | LET references are resolved via expression substitution |
| ASSERT | Yes | Evaluated after SELECT projection against the projected row |
| ORDER BY | Yes | Can reference aliased LET output column names |

## See Also

- [SELECT](select.md)
- [GROUP BY](group-by.md)
- [Window Functions](window-functions.md)
