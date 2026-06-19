---
title: FROM
---

The FROM clause specifies the data sources for a query.

```sql
FROM table_name
FROM table_name AS alias
FROM (SELECT ... FROM ...) AS subquery
FROM generate_series(0, 360) AS g
```

### Quoted table names

Table names that contain spaces, hyphens, start with a digit, or collide with SQL keywords must be quoted. Two quoting styles are supported:

```sql
-- Double-quoted (recommended)
SELECT * FROM "my table"

-- Single-quoted
SELECT * FROM 'my table'
```

Most table names derived from filenames are valid bare identifiers (e.g. `orders_csv`, `data_json`) and do not need quoting. The `.tables` command and tab completion automatically double-quote names that need it, so you can copy them directly into your SQL.

Table-valued functions can be used as data sources in FROM and JOIN clauses. Both `range` (half-open `[start, stop)`) and `generate_series` (inclusive `[start, stop]`) produce rows with a single `value` column:

```sql
-- Inclusive sequence with both endpoints — 0 through 360
SELECT g.value FROM generate_series(0, 360) AS g

-- Half-open sequence — 0 through 359
SELECT r.value FROM range(0, 360) AS r

-- With a custom step
SELECT g.value FROM generate_series(0, 1, 0.1) AS g

-- Compute a sine wave
SELECT g.value AS x, ((SIN(2.0 * PI() * g.value) + 1.0) / 2.0) AS y
FROM generate_series(0, 360) AS g

-- Use in a CROSS JOIN
SELECT t.name, g.value AS angle
FROM data AS t
CROSS JOIN generate_series(0, 360) AS g

-- Or written as a comma-separated FROM list (SQL-89 / PostgreSQL style)
SELECT t.name, g.value AS angle
FROM data AS t, generate_series(0, 360) AS g
```

Comma-separated FROM lists are equivalent to `CROSS JOIN`. See [JOIN](joins.md) for the full semantics, including the implicit-lateral rule for function sources in comma position.

## See Also

- [SELECT](select.md)
- [JOIN](joins.md)
- [WHERE](filtering.md)
