---
title: Common Table Expressions (WITH)
---

CTEs define named temporary result sets scoped to a single statement. They can simplify complex queries by breaking them into readable, composable stages.

```sql
WITH filtered AS (
  SELECT id, name, score FROM data WHERE score > 50
)
SELECT name, score FROM filtered ORDER BY score DESC
```

### Multiple CTEs

Comma-separate multiple CTEs. Later CTEs can reference earlier ones:

```sql
WITH
  high_scores AS (
    SELECT id, name, score FROM data WHERE score > 80
  ),
  ranked AS (
    SELECT name, score, ROW_NUMBER() OVER (ORDER BY score DESC) AS rank
    FROM high_scores
  )
SELECT name, score, rank FROM ranked WHERE rank <= 10
```

### Column renaming

An optional column list renames the CTE's output columns:

```sql
WITH summary(category, total) AS (
  SELECT department, COUNT(*) FROM employees GROUP BY department
)
SELECT category, total FROM summary
```

### Materialization hints

By default, the planner auto-materializes CTEs referenced more than once. Override this with `MATERIALIZED` or `NOT MATERIALIZED`:

```sql
-- Force materialization (compute once, buffer results)
WITH stats AS MATERIALIZED (
  SELECT AVG(score) AS mean, COUNT(*) AS n FROM data
)
SELECT * FROM stats

-- Force inlining (re-execute per reference, no buffer)
WITH latest AS NOT MATERIALIZED (
  SELECT * FROM data ORDER BY timestamp DESC LIMIT 100
)
SELECT * FROM latest
```

Materialized CTEs buffer their results in memory. When a memory budget is configured and the buffer exceeds it, rows spill to temporary files on disk. Materialization adds no Query Units (0 QU) but enforces per-query QU budgets during buffering.

### Recursive CTEs

`WITH RECURSIVE` enables iterative queries. The CTE body must contain a `UNION ALL` separating the anchor member (base case) from the recursive member (which references the CTE name):

```sql
-- Generate a sequence 1..10
WITH RECURSIVE seq AS (
  SELECT 1 AS n
  UNION ALL
  SELECT n + 1 FROM seq WHERE n < 10
)
SELECT n FROM seq
```

The recursive member executes iteratively: each iteration reads the previous iteration's output as the working table. Iteration stops when the working table is empty. A safety limit (`MaxRecursionDepth`, default 1,000) prevents infinite loops — exceeding it raises an error.

```sql
-- Hierarchical traversal
WITH RECURSIVE tree AS (
  SELECT id, parent_id, name, 0 AS depth
  FROM nodes WHERE parent_id IS NULL
  UNION ALL
  SELECT n.id, n.parent_id, n.name, t.depth + 1
  FROM nodes AS n INNER JOIN tree AS t ON n.parent_id = t.id
)
SELECT id, name, depth FROM tree ORDER BY depth, name
```

### Execution strategy

| Scenario | Behavior |
|----------|----------|
| Single reference, no hint | Inlined (streaming, no buffer) |
| Multiple references, no hint | Auto-materialized |
| `MATERIALIZED` hint | Always materialized |
| `NOT MATERIALIZED` hint | Always inlined |
| `WITH RECURSIVE` | Materialized per iteration |

## See Also

- [Subqueries](subqueries.md)
- [Set Operations](set-operations.md)
- [SELECT](select.md)
