---
title: PIVOT / UNPIVOT
---

## Why Use This

Sometimes your data is in the wrong shape. PIVOT turns row values into columns — great for creating one column per category in a report. UNPIVOT does the reverse, melting wide tables into long format — essential for reshaping data before feeding it into ML models that expect one observation per row.

## How It Works

**PIVOT**: Imagine a spreadsheet where each row is a sale with columns `product`, `region`, `amount`. PIVOT takes the distinct values of `region` (North, South, East, West) and turns each into its own column, filling in the aggregate (like SUM of amount) for each product-region combination. You go from many rows to fewer rows with more columns.

**UNPIVOT** does the reverse: you have a wide table with columns `q1, q2, q3, q4` and you want to stack them into two columns — one for the quarter name and one for the value. Each input row becomes multiple output rows. This is often called "melting" in data science.

## PIVOT

PIVOT rotates distinct values of a column into output columns, computing one aggregate per cell.

### Syntax

```sql
SELECT ...
FROM table
PIVOT (
    aggregate1 [, aggregate2 ...]
    FOR pivot_column
    [IN (value1 [, value2 ...])]
) [AS alias]
```

### Examples

**Explicit value list:**

```sql
SELECT product, North, South, East, West
FROM sales
PIVOT (
    SUM(amount)
    FOR region
    IN ('North', 'South', 'East', 'West')
)
```

This produces one column per region. The output schema is:
`product | North | South | East | West`

**Auto-discover values (no IN clause):**

```sql
SELECT *
FROM experiment_results
PIVOT (AVG(score) FOR condition)
```

Distinct values of `condition` are discovered at runtime and each becomes an output column. This mode is subject to the cardinality cap (see below).

**Multiple aggregates:**

When more than one aggregate is specified, each output column is named `<value>_<aggregate>`:

```sql
SELECT *
FROM sales
PIVOT (SUM(amount), COUNT(id) FOR region IN ('North', 'South'))
-- Output columns: product, North_SUM(amount), North_COUNT(id), South_SUM(amount), South_COUNT(id)
```

### Output schema

- All source columns **except** the pivot column become key (pass-through) columns.
- One output column is created per `(pivot value, aggregate)` pair.
- Single-aggregate queries use the pivot value as the column name directly.
- Multi-aggregate queries prefix each column with `<value>_`.
- The SELECT column list preceding `PIVOT` is ignored; PIVOT defines the output schema.

### Cardinality cap

Auto-discover mode (no `IN` clause) caps the number of distinct pivot values at **1000**. Queries that exceed this limit fail with an error:

> PIVOT auto-discover exceeded the cardinality cap of 1000 distinct values. Use an explicit IN (value1, value2, …) list to select the values to pivot.

Use an explicit `IN (…)` list to select a known subset of values and avoid the cap.

### Execution model

PIVOT is a **blocking** operator — it must buffer all input rows before emitting any output, since it must discover all distinct pivot values (in auto-discover mode) before building the output schema. When a memory budget is configured and the in-memory row buffer exceeds the budget, overflow rows are spilled to a temporary file on disk and replayed during the aggregation phase. This bounds peak memory regardless of input size.

---

## UNPIVOT

UNPIVOT rotates a set of columns into rows, emitting a (name, value) pair per column per input row.

### Syntax

```sql
SELECT ...
FROM table
UNPIVOT [INCLUDE NULLS] (
    value_column
    FOR name_column
    IN (source_col1 [, source_col2 ...])
) [AS alias]
```

### Examples

**Basic unpivot:**

```sql
SELECT id, quarter, revenue
FROM wide_sales
UNPIVOT (
    revenue
    FOR quarter
    IN (q1, q2, q3, q4)
)
```

For each input row with columns `id, q1, q2, q3, q4`, this emits four output rows:
```
id=1, quarter='q1', revenue=<q1 value>
id=1, quarter='q2', revenue=<q2 value>
id=1, quarter='q3', revenue=<q3 value>
id=1, quarter='q4', revenue=<q4 value>
```

**Include NULLs:**

By default, rows where the source column value is NULL are omitted. Use `INCLUDE NULLS` to retain them:

```sql
SELECT *
FROM sparse_data
UNPIVOT INCLUDE NULLS (
    measurement
    FOR sensor
    IN (temp, pressure, humidity)
)
```

### Output schema

- All source columns **not listed in the IN clause** become key (pass-through) columns.
- One output column carries the cell value (named by the first identifier before `FOR`).
- One output column carries the source column name as a string (named by the identifier after `FOR`).
- The SELECT column list preceding `UNPIVOT` is ignored; UNPIVOT defines the output schema.

### Execution model

UNPIVOT is a **streaming** operator — it processes each input row immediately and emits up to N output rows (where N is the number of source columns). No buffering is required.

## See Also

- [GROUP BY](group-by.md)
- [SELECT](select.md)
- [INTO](into.md)
