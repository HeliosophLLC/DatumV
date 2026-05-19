---
title: SELECT
---

## Why Use This

SELECT is the starting point of every query. Beyond basic column selection, DatumV extends SELECT with EXCEPT (drop columns you don't need), REPLACE (transform columns in-place), and DISTINCT (deduplicate results).

The SELECT clause specifies which columns and expressions appear in the query result.

```sql
SELECT *
SELECT col1, col2, col3
SELECT a.col1, b.col2
SELECT col1 AS alias, min_max_normalize(col2) AS norm_col
SELECT table_alias.*
SELECT * EXCEPT (col1, col2)
SELECT * REPLACE (upper(name) AS name)
SELECT t.* EXCEPT (id) REPLACE (score * 100 AS score)
```

### SELECT * EXCEPT

You want all columns from a table except the raw image bytes that would bloat your CSV export.

Exclude specific columns from wildcard expansion. Applies to both `*` and `table.*`:

```sql
-- All columns except the raw image blob
SELECT * EXCEPT (image_bytes) FROM training_data

-- Exclude from a specific table in a join
SELECT a.* EXCEPT (id), b.label
FROM features a JOIN labels b ON a.id = b.id
```

Column names in the EXCEPT list must exist in the expanded column set; unknown names produce a planner error.

### SELECT * REPLACE

You want to normalize a score column to 0-1 range without listing every other column by hand.

Replace specific columns in wildcard expansion with new expressions. The alias must match an existing column name — the replacement takes the original column's position:

```sql
-- Normalize a column in-place
SELECT * REPLACE (min_max_normalize(score, 0, 100) AS score) FROM data

-- Replace multiple columns
SELECT * REPLACE (upper(name) AS name, round(price) AS price) FROM products

-- Combine EXCEPT and REPLACE on the same wildcard
SELECT * EXCEPT (raw_data) REPLACE (trim(name) AS name) FROM records
```

EXCEPT is applied before REPLACE — you cannot replace a column that has been excluded. REPLACE aliases that do not match any column in the expansion produce a planner error.

### SELECT DISTINCT

Eliminate duplicate rows from the result set. Deduplication is based on all projected columns:

```sql
-- Unique categories
SELECT DISTINCT category FROM products

-- Unique combinations
SELECT DISTINCT department, status FROM employees
```

`SELECT DISTINCT` uses a streaming hash-based operator. When a memory budget is configured and the in-memory set exceeds the budget, the operator spills unseen rows to hash-partitioned temporary files and deduplicates them in a drain phase.

**ORDER BY constraint:** When `SELECT DISTINCT` is combined with `ORDER BY`, every `ORDER BY` expression must appear in the `SELECT` list. This avoids ambiguity because DISTINCT collapses rows before sorting.

```sql
-- Valid: ORDER BY column is in SELECT list
SELECT DISTINCT name FROM users ORDER BY name

-- Invalid: age is not projected
SELECT DISTINCT name FROM users ORDER BY age
```

## See Also

- [LET Bindings](let-bindings.md)
- [FROM](from.md)
- [WHERE](filtering.md)
