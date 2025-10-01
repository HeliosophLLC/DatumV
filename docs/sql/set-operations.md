---
title: Set Operations
---

Set operations combine the results of two or more SELECT statements. All six variants are supported:

```sql
-- UNION ALL: concatenate both result sets, keeping duplicates
SELECT name, category FROM train
UNION ALL
SELECT name, category FROM test

-- UNION (distinct): concatenate and deduplicate
SELECT category FROM train
UNION
SELECT category FROM test

-- INTERSECT: rows that appear in both
SELECT id FROM train
INTERSECT
SELECT id FROM test

-- INTERSECT ALL: rows that appear in both, preserving minimum occurrence count
SELECT category FROM train
INTERSECT ALL
SELECT category FROM test

-- EXCEPT: rows in the left that do not appear in the right
SELECT id FROM train
EXCEPT
SELECT id FROM test

-- EXCEPT ALL: subtract right-side counts from left-side counts
SELECT category FROM train
EXCEPT ALL
SELECT category FROM test
```

### Chaining

Multiple set operations can be chained. INTERSECT binds tighter than UNION and EXCEPT, following SQL standard precedence:

```sql
-- INTERSECT is evaluated first, then UNION
SELECT id FROM a
UNION
SELECT id FROM b
INTERSECT
SELECT id FROM c

-- Equivalent to:
SELECT id FROM a
UNION
(SELECT id FROM b INTERSECT SELECT id FROM c)
```

UNION and EXCEPT have equal precedence and associate left to right.

### ORDER BY, LIMIT, and OFFSET

Trailing ORDER BY, LIMIT, and OFFSET clauses apply to the entire compound result, not to individual branches:

```sql
-- Sort the combined result
SELECT name, score FROM train
UNION ALL
SELECT name, score FROM test
ORDER BY score DESC
LIMIT 100
```

To apply ORDER BY or LIMIT to an individual branch, use a Common Table Expression or subquery:

```sql
WITH top_train AS (
    SELECT name, score FROM train ORDER BY score DESC LIMIT 50
)
SELECT * FROM top_train
UNION ALL
SELECT name, score FROM test ORDER BY score DESC LIMIT 50
```

### Execution model

| Operation | Strategy |
|-----------|----------|
| UNION ALL | Zero-overhead stream concatenation |
| UNION (distinct) | Streaming hash deduplication with spill-to-disk |
| INTERSECT | Materialise right branch into hash set, probe with left; spill-to-disk via grace hash partitioning |
| INTERSECT ALL | Materialise right branch into counted multiset, emit up to count; spill-to-disk via grace hash partitioning |
| EXCEPT | Materialise right branch into hash set, exclude from left; spill-to-disk via grace hash partitioning |
| EXCEPT ALL | Materialise right branch into counted multiset, subtract counts; spill-to-disk via grace hash partitioning |

For single-column results, `HashSet<DataValue>` is used directly. For multi-column results, a `CompositeKey` wrapper provides structural equality and hashing.

UNION DISTINCT supports **spill-to-disk** when a memory budget is configured: when the in-memory hash set exceeds the budget (tracked by `MemoryEstimator`), unseen rows are spilled to 64 hash-partitioned temporary files and deduplicated in a drain phase. INTERSECT and EXCEPT (all four variants) also support **spill-to-disk**: when the right-branch materialisation exceeds the memory budget, remaining right rows are hash-partitioned to spill files; left rows whose partitions were spilled are buffered to corresponding left-side spill files and processed partition-by-partition in a drain phase. This ensures arbitrarily large set operations complete without out-of-memory failures.

Set operations add no Query Units (0 QU).

## See Also

- [Common Table Expressions (WITH)](cte.md)
- [Subqueries](subqueries.md)
- [SELECT](select.md)
