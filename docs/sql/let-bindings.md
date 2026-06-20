---
title: LET Bindings
---

## Common Patterns

### 1. Image pipeline: compute a tensor once, derive multiple features

```sql
SELECT
  LET tensor = image_to_tensor_hwc(image),       -- expensive: runs only once per row
  LET resized = resize_tensor(tensor, 224, 224),
  rank(tensor) AS ndim,                           -- reuse tensor
  vec_mean(resized) AS avg_pixel,                 -- reuse resized
  shape(resized) AS dimensions                    -- reuse resized again
FROM training_images
```

### 2. Price calculation: compute a subtotal once, use for tax and discount

```sql
SELECT
  LET subtotal = unit_price * quantity,
  subtotal * 0.08 AS tax,
  CASE WHEN subtotal > 100 THEN subtotal * 0.10 ELSE 0 END AS discount,
  subtotal AS line_total
FROM orders
```

### 3. Feature engineering: compute a derived metric, use in multiple output columns

```sql
SELECT
  LET avg_order = total_spent / order_count,
  avg_order AS avg_order_value,
  avg_order / category_avg * 100 AS pct_of_category,
  CASE WHEN avg_order > 500 THEN 'high' ELSE 'standard' END AS tier
FROM customers
```

## Filtering on a LET binding from WHERE

Scalar and `models.*` LET bindings are referenceable from `WHERE`. The planner lifts each referenced binding into a hidden upstream rung so its value is on the row by the time the predicate evaluates — and that single evaluation is shared with the rest of the SELECT (no double-evaluation).

```sql
SELECT LET label = models.classifier(image), id, label
FROM uploads
WHERE label = 'cat'
```

Mixed bodies (a scalar function wrapping a model call) work too:

```sql
SELECT LET tagged = concat('USER:', upper(models.echo(name))), name, tagged
FROM users
WHERE tagged LIKE 'USER:A%'
```

Aggregate- or window-derived LET bindings cannot be referenced from `WHERE` — `WHERE` runs before grouping. Use `HAVING` for aggregates and `QUALIFY` for window functions; the planner emits a diagnostic naming the offending binding.

## Gotchas

- **SELECT * does NOT include LET bindings** -- even aliased ones. You must name each output column explicitly.
- **Later LET bindings can reference earlier ones, but not the reverse** -- bindings are evaluated left to right. Referencing a binding that appears later is a parse error.

`LET` declares named, reusable computed values in the SELECT list. Each binding is evaluated once per row and its value is cached for all subsequent references. LET bindings are **not included in the output** unless explicitly aliased with `AS`.

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
| WHERE | Yes (scalar / `models.*` only) | Aggregate- or window-derived bindings rejected with a diagnostic; use HAVING / QUALIFY |
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
