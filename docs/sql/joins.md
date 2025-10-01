---
title: JOIN
---

All five standard join types are supported:

```sql
-- INNER JOIN: only matching rows
SELECT * FROM a INNER JOIN b ON a.id = b.id

-- LEFT JOIN: all rows from left, matching from right
SELECT * FROM a LEFT JOIN b ON a.id = b.id

-- RIGHT JOIN: all rows from right, matching from left
SELECT * FROM a RIGHT JOIN b ON a.id = b.id

-- FULL OUTER JOIN: all rows from both sides
SELECT * FROM a FULL OUTER JOIN b ON a.id = b.id

-- CROSS JOIN: cartesian product
SELECT * FROM a CROSS JOIN b
```

NULL keys never match (SQL three-valued logic). Hash join is used for INNER/LEFT/RIGHT/FULL OUTER; nested loop for CROSS.

### LATERAL JOIN / APPLY

A **lateral join** re-executes the right-hand source for every row from the left side, allowing the right side to reference columns from the left. The explicit `LATERAL` keyword is required after `CROSS JOIN` or `LEFT [OUTER] JOIN`. The T-SQL `CROSS APPLY` and `OUTER APPLY` syntax is also supported.

```sql
-- CROSS JOIN LATERAL: expand array column per row (no match → row excluded)
SELECT t.name, s.value
FROM data AS t
CROSS JOIN LATERAL UNNEST(t.scores) AS s

-- LEFT JOIN LATERAL: preserve rows with empty arrays (NULL-padded)
SELECT t.name, s.value
FROM data AS t
LEFT JOIN LATERAL UNNEST(t.scores) AS s

-- Lateral subquery: correlated derived table referencing outer columns
SELECT o.customer, sub.product
FROM orders AS o
LEFT JOIN LATERAL (
    SELECT i.product FROM items AS i WHERE i.order_id = o.id
) AS sub ON 1 = 1

-- T-SQL CROSS APPLY (equivalent to CROSS JOIN LATERAL)
SELECT t.name, s.value
FROM data AS t
CROSS APPLY UNNEST(t.scores) AS s

-- T-SQL OUTER APPLY (equivalent to LEFT JOIN LATERAL)
SELECT t.name, s.value
FROM data AS t
OUTER APPLY UNNEST(t.scores) AS s
```

LATERAL is supported with `CROSS JOIN` and `LEFT [OUTER] JOIN` only. The right-hand source can be a table-valued function or a subquery.

> **Performance:** Lateral joins use O(N × M) nested-loop execution — the right side is re-executed for each left row. No hash acceleration is possible. For large left-side tables, consider filtering the left side before the lateral join.

## See Also

- [FROM](from.md)
- [SELECT](select.md)
- [WHERE](filtering.md)
