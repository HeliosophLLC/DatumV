---
title: EXPLAIN
---

## Why Use This

Your query works but it's slow. EXPLAIN shows you the execution plan — where the time goes, what operations are blocking, and whether statistics-based pruning is being used. It's the first tool to reach for when debugging performance.

The `explain` command shows the query execution plan as a tree. Two modes are supported:

### Static EXPLAIN

Shows the operator tree structure, join strategies, filter predicates, and warnings — without executing the query:

```bash
datumv explain "SELECT x, y FROM data WHERE x > 0 ORDER BY x LIMIT 100" --source csv:data=measurements.csv
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
datumv explain "SELECT x FROM data WHERE x > 0.5" --source csv:data=measurements.csv --analyze
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

## Reading the Output

Here is a walkthrough of a typical EXPLAIN ANALYZE result. Suppose you run:

```sql
SELECT c.name, SUM(o.total_amount) AS lifetime_value
FROM customers c
INNER JOIN orders o ON c.customer_id = o.customer_id
WHERE o.order_date >= '2025-01-01'
GROUP BY c.name
ORDER BY lifetime_value DESC
LIMIT 10
```

The output might look like:

```
Limit (limit: 10)                                           |  rows: 10       |  self: 0.0 ms  |  total: 42.1 ms
└─ Sort (lifetime_value DESC)                               |  rows: 1,200    |  self: 1.3 ms  |  total: 42.1 ms
    → bounded top-N sort (N=10)
    └─ HashAggregate (group: c.name, agg: SUM(o.total_amount))  |  rows: 1,200    |  self: 3.8 ms  |  total: 40.8 ms
        └─ HashJoin (INNER, on: c.customer_id = o.customer_id)  |  rows: 24,500   |  self: 5.2 ms  |  total: 37.0 ms
            ├─ Scan (table: customers, columns: [customer_id, name])  |  rows: 5,000    |  self: 2.1 ms  |  total: 2.1 ms
            └─ Filter (predicate: o.order_date >= '2025-01-01')      |  rows in: 100,000 → out: 24,500 (24.5%)  |  self: 4.7 ms  |  total: 29.7 ms
                └─ Scan (table: orders, columns: [customer_id, total_amount, order_date])  |  rows: 100,000  |  self: 25.0 ms  |  total: 25.0 ms
```

Reading bottom-up (the plan executes leaves first):

1. **Scan (orders)** reads 100,000 rows in 25.0 ms — this is the most expensive leaf. If this were a Parquet source with statistics, you might see row groups pruned here.
2. **Filter** applies the date predicate, keeping only 24.5% of rows (24,500 out of 100,000). High selectivity means this filter is doing useful work.
3. **Scan (customers)** reads 5,000 rows — fast because the table is small.
4. **HashJoin** matches customers to their filtered orders. The self time (5.2 ms) is the cost of probing the hash table.
5. **HashAggregate** groups by customer name and computes the SUM. 24,500 input rows collapse to 1,200 groups.
6. **Sort** with "bounded top-N" means it only tracks the top 10 rows — it does not sort all 1,200. This is the ORDER BY + LIMIT optimization.
7. **Limit** emits the final 10 rows.

The **total** time is cumulative (includes children); **self** time is the operator's own work. The biggest self-time nodes are your optimization targets.

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
