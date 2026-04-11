---
title: Common Table Expressions (WITH)
---

## Why Use This

A complex query with nested subqueries quickly becomes unreadable. CTEs let you name each step and write them top-to-bottom, like a pipeline — each step feeds into the next.

## When to Use CTE vs Subquery

Use a CTE when you reference the same intermediate result more than once, or when the nesting gets more than two levels deep. Use a subquery for simple one-off filters. CTEs are also the only way to write recursive queries (hierarchies, sequences).

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

Materialized CTEs buffer their results in memory. When a memory budget is configured and the buffer exceeds it, rows spill to temporary files on disk.

### Data-modifying CTEs

A CTE body can be an `INSERT`, `UPDATE`, or `DELETE` statement with a `RETURNING` clause. The mutation runs as part of the surrounding query plan and the CTE projects its `RETURNING` rows.

```sql
-- INSERT: insert a conversation, then insert its first message
-- referencing the new id.
WITH new_conv AS (
  INSERT INTO conversations (workspace, title) VALUES ('default', 'Chat')
  RETURNING id
)
INSERT INTO messages (conversation_id, body)
SELECT id, 'Hello' FROM new_conv

-- UPDATE: bump scores and log the affected rows to an audit table.
WITH bumped AS (
  UPDATE features SET score = score + 0.1 WHERE risk_tier = 'high'
  RETURNING id, score
)
INSERT INTO score_audit (feature_id, new_score)
SELECT id, score FROM bumped

-- DELETE: move-and-archive — capture rows being tombstoned so the
-- archive insert can use them.
WITH removed AS (
  DELETE FROM staging WHERE processed = true
  RETURNING id, payload
)
INSERT INTO archive (id, payload)
SELECT id, payload FROM removed
```

The data-modifying CTE body **must** include a `RETURNING` clause — a CTE has no row source otherwise. The mutation runs **exactly once per surrounding query execution**, regardless of how many times the CTE name is referenced. See the per-statement RETURNING sections for projection-list rules: [INSERT](ddl-dml.md#returning), [UPDATE](ddl-dml.md#returning-1), [DELETE](ddl-dml.md#returning-2).

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

### Generating a date sequence for time-series gap-filling

When your sensor data has missing days, a recursive CTE can generate the full date range so you can LEFT JOIN and find the gaps:

```sql
WITH RECURSIVE date_range AS (
  -- Anchor: start of the period
  SELECT CAST('2026-01-01' AS DATE) AS report_date
  UNION ALL
  -- Recursive: add one day until end of period
  SELECT report_date + INTERVAL '1' DAY
  FROM date_range
  WHERE report_date < '2026-03-31'
)
SELECT
    d.report_date,
    COALESCE(r.avg_temperature, 0) AS avg_temperature
FROM date_range d
LEFT JOIN (
    SELECT reading_date, AVG(temperature) AS avg_temperature
    FROM sensor_data
    GROUP BY reading_date
) r ON d.report_date = r.reading_date
ORDER BY d.report_date
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
