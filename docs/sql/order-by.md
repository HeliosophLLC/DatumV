---
title: ORDER BY / LIMIT / OFFSET
---

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
