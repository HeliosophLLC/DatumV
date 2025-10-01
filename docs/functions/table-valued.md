---
title: Table-Valued Functions
category: table
---

# Table-Valued Functions

[< Back to Functions Reference](string.md) | [SQL Reference](../sql/select.md)

Table-valued functions produce multiple rows and are used in FROM, CROSS JOIN, and LATERAL JOIN clauses. When used with `CROSS JOIN LATERAL` or `CROSS APPLY`, the function arguments can reference columns from the left-hand table, enabling per-row expansion of array or nested data.

### unnest

`unnest(array_col)` -> Rows | QU: 1

Expand array-valued column into separate rows. Works with Vector, UInt8Array, JsonValue arrays.

```sql
-- Expand a vector column per row using lateral join
SELECT t.name, s.value
FROM data AS t
CROSS JOIN LATERAL UNNEST(t.scores) AS s
```

### range

`range(start, end[, step])` -> Rows | QU: 1

Generate a sequence of rows with a `Value` column from start to end (inclusive). Default step is 1.

See [SQL Reference -- LATERAL JOIN / APPLY](../sql/joins.md#lateral-join--apply) for full syntax and examples.

## See Also

- [Aggregate Functions](aggregate.md) -- grouping and reduction functions
- [Window Functions](window.md) -- per-row computations over partitions
- [SQL Reference](../sql/select.md) -- full SQL dialect documentation
