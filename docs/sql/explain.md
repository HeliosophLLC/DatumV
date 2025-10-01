---
title: EXPLAIN
---

The `explain` command shows the query execution plan as a tree. Two modes are supported:

### Static EXPLAIN

Shows the operator tree structure, join strategies, filter predicates, and warnings — without executing the query:

```bash
datum-ingest explain "SELECT x, y FROM data WHERE x > 0 ORDER BY x LIMIT 100" --source csv:data=measurements.csv
```

```
Limit (limit: 100)
└─ Sort (x ASC)
    → bounded top-N sort (N=100)
    └─ Project (x, y)
        └─ Filter (predicate: x > 0)
            └─ Scan (table: data, provider: csv, columns: [*])
```

When a WHERE predicate is pushed down to a Parquet scan, the plan shows the advisory filter hint:

```
Filter (predicate: id > 1000)
└─ Scan (table: events, provider: parquet, columns: [*], statistics filter: id > 1000)
```

### EXPLAIN ANALYZE

Add `--analyze` to actually execute the query and report runtime metrics — row counts, filter selectivity, self time, and total time per operator:

```bash
datum-ingest explain "SELECT x FROM data WHERE x > 0.5" --source csv:data=measurements.csv --analyze
```

```
Filter (predicate: x > 0.5)  |  rows in: 10,000 → out: 4,987 (49.9%)  |  self: 1.2 ms  |  total: 8.7 ms
└─ Scan (table: data, provider: csv, columns: [*])  |  rows: 10,000  |  self: 7.5 ms  |  total: 7.5 ms
```

For Parquet scans with statistics-based pruning, EXPLAIN ANALYZE reports how many row groups were skipped:

```
Filter (predicate: id > 1000)  |  rows in: 50,000 → out: 12,345 (24.7%)  |  self: 0.8 ms  |  total: 15.2 ms
└─ Scan (table: events, provider: parquet, columns: [*], statistics filter: id > 1000)  |  rows: 50,000  |  row groups: 10 total, 7 pruned (70%)  |  self: 14.4 ms  |  total: 14.4 ms
```

### Warnings

The explain plan emits warnings about potential performance issues:

| Warning | Trigger |
|---------|---------|
| ORDER BY materializes all input rows | ORDER BY without LIMIT |
| CROSS JOIN produces a cartesian product | CROSS JOIN |
| FULL OUTER JOIN materializes both sides | FULL OUTER JOIN |
| Pattern matching predicate requires full scan | LIKE / ILIKE / REGEXP in WHERE |
| GroupBy materializes all groups in memory | GROUP BY |

## See Also

- [Parameterized Queries](parameters.md)
- [Schema Introspection](schema-introspection.md)
- [SELECT](select.md)
