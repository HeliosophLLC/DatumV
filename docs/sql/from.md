---
title: FROM
---

The FROM clause specifies the data sources for a query.

```sql
FROM table_name
FROM table_name AS alias
FROM (SELECT ... FROM ...) AS subquery
FROM RANGE(0, 360) AS r
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

Table-valued functions can be used as data sources in FROM and JOIN clauses:

```sql
-- Generate rows with Value 0 through 360 (inclusive)
SELECT r."Value" FROM RANGE(0, 360) AS r

-- With a custom step
SELECT r."Value" FROM RANGE(0, 1, 0.1) AS r

-- Compute a sine wave
SELECT r."Value" AS x, ((SIN(2.0 * PI() * r."Value") + 1.0) / 2.0) AS y
FROM RANGE(0, 360) AS r

-- Use in a CROSS JOIN
SELECT t.name, r."Value" AS angle
FROM data AS t
CROSS JOIN RANGE(0, 360) AS r
```

## See Also

- [SELECT](select.md)
- [JOIN](joins.md)
- [WHERE](filtering.md)
