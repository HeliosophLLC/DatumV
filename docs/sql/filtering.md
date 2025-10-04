---
title: WHERE
---

## Why Use This

WHERE filters rows before any grouping, aggregation, or window functions run. It's your first line of defense against noisy data — remove nulls, exclude outliers, focus on a date range.

The WHERE clause filters rows before grouping and aggregation.

```sql
WHERE col > 10
WHERE col1 = 'value' AND col2 < 100
WHERE col IN ('a', 'b', 'c')
WHERE col BETWEEN 10 AND 50
WHERE col LIKE 'prefix_%'
WHERE col ILIKE '%pattern%'
WHERE col REGEXP '^\d{3}-\d{4}$'
WHERE col IS NULL
WHERE col IS NOT NULL
WHERE col IS Int32
WHERE col IS NOT String
WHERE NOT (col1 > 10 OR col2 < 5)
```

Supported operators: `=`, `!=`, `<`, `>`, `<=`, `>=`, `AND`, `OR`, `NOT`, `LIKE`, `ILIKE`, `REGEXP`, `IN`, `BETWEEN`, `IS NULL`, `IS NOT NULL`, `IS Type`, `IS NOT Type`.

### Pattern matching

`LIKE` performs case-sensitive pattern matching with `%` (zero or more characters) and `_` (exactly one character) wildcards. `ILIKE` is the case-insensitive variant.

`REGEXP` matches against a .NET regular expression. The match is unanchored (substring match) — use `^` and `$` anchors for full-string matching. Case-sensitive by default; use inline `(?i)` for case-insensitive matching.

```sql
-- Case-sensitive wildcard matching
SELECT * FROM logs WHERE message LIKE 'ERROR:%'

-- Case-insensitive wildcard matching
SELECT * FROM users WHERE name ILIKE '%smith%'

-- Regular expression matching
SELECT * FROM data WHERE phone REGEXP '^\d{3}-\d{4}$'
SELECT * FROM logs WHERE line REGEXP '(?i)warning|error'
```

All three operators support negation via `NOT`: `NOT LIKE`, `NOT ILIKE`, `NOT REGEXP`.

### ESCAPE clause

By default `%` and `_` are wildcards in `LIKE` / `ILIKE` patterns. Use the `ESCAPE` clause to designate a character that causes the next `%` or `_` to be treated as a literal:

```sql
-- Match strings containing a literal percent sign
SELECT * FROM data WHERE value LIKE '%100\%' ESCAPE '\'

-- Match strings starting with an underscore (case-insensitive)
SELECT * FROM users WHERE name ILIKE '\_%' ESCAPE '\'
```

The escape character must be a single character. It only affects the immediately following `%` or `_`.

## Gotchas

- `NULL = NULL` is not true — use `IS NULL` instead of `= NULL`.
- `NOT IN` with a subquery that contains NULLs returns no rows (SQL three-valued logic) — use `NOT EXISTS` instead.
- `LIKE` is case-sensitive; use `ILIKE` for case-insensitive matching.

## See Also

- [SELECT](select.md)
- [GROUP BY](group-by.md)
- [JOIN](joins.md)
