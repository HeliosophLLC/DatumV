---
title: ORDER BY / LIMIT / OFFSET
---

## Why Use This

Query results have no guaranteed order unless you specify one. ORDER BY gives you control — sort by score to find the best, by date to see the latest, or combine with LIMIT for top-N queries and pagination.

## Common Patterns

### Top-N: best-rated products

```sql
SELECT * FROM products ORDER BY rating DESC LIMIT 10
```

### Pagination: page 3 of orders (20 per page)

```sql
SELECT * FROM orders ORDER BY order_date DESC LIMIT 20 OFFSET 40
```

### Multi-column sort: employees by department then salary

```sql
SELECT * FROM employees ORDER BY department ASC, salary DESC
```

## Gotchas

- **Without ORDER BY, row order is undefined** — don't rely on insertion order or assume consistency between runs.
- **ORDER BY + LIMIT uses a bounded priority queue (efficient)** — no need to sort the entire dataset.
- **OFFSET can be slow on large datasets** — it still scans all skipped rows. For deep pagination, consider keyset pagination (WHERE id > last_seen_id) instead.

## Syntax

```sql
SELECT * FROM data ORDER BY score DESC
SELECT * FROM data ORDER BY category ASC, score DESC
SELECT * FROM data LIMIT 100
SELECT * FROM data LIMIT 100 OFFSET 50
SELECT * FROM data ORDER BY score DESC LIMIT 10
```

When ORDER BY + LIMIT are combined, a bounded priority queue (top-N sort) avoids materializing the full result set.

When a memory budget is configured, ORDER BY uses an external sort strategy: in-memory rows are sorted in runs, and when the budget is exceeded each run is flushed to a temporary file on disk. The final output is produced by a k-way merge of all sorted runs, ensuring arbitrarily large sorts complete without out-of-memory failures.

## See Also

- [SELECT](select.md)
- [INTO](into.md)
- [Window Functions](window-functions.md)
