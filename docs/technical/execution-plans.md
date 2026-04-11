# Execution Plans

DatumIngest provides an EXPLAIN facility that produces a tree-structured execution plan describing how a SQL query will be (or was) executed. Plans are available in two modes:

- **EXPLAIN** — static plan with cost estimates, operator structure, and warnings.
- **EXPLAIN ANALYZE** — executes the query and annotates the plan with actual runtime metrics (row counts, timings, pruning statistics).

This document explains how to read and interpret execution plans, what each operator and annotation means, and how to use the information to diagnose performance.

---

## Obtaining a Plan

### CLI

```
.explain SELECT * FROM events WHERE age > 30
.explain --analyze SELECT * FROM events JOIN users ON events.user_id = users.id
```

### gRPC (Compute Service)

Send an `ExplainRequest` with the `sql` field populated. Set `analyze = true` for runtime metrics. The response contains both `plan_text` (human-readable) and `root` (structured `ExplainPlanNodeMessage` tree).

---

## Reading the Plan Tree

Plans render as indented trees. Each line represents one **operator** — a unit of work in the query pipeline. Data flows from leaf nodes (scans) up to the root (final projection or limit).

```
Project (user_id, event_type, name)  ~33,000 rows
└─ INNER Join (strategy: hash, on: e.user_id = u.id)  ~33,000 rows
   ├─ [probe] Filter (predicate: age > 30)  ~330,000 rows
   │   └─ Scan (table: events, provider: parquet, columns: [user_id, event_type, age])  ~1,000,000 rows
   └─ [build] Scan (table: users, provider: parquet, columns: [id, name])  ~100,000 rows
```

### Anatomy of a Plan Line

```
OperatorName (details)  ~EstimatedRows rows  |  rows in: N → out: M (X%)  |  self: T  |  total: T
```

| Component | Mode | Meaning |
|-----------|------|---------|
| **OperatorName** | Both | The type of operation (see operator reference below). |
| **(details)** | Both | Operator-specific configuration: tables, predicates, columns, strategies. |
| **~N rows** | Both | Estimated row count from the cost model. Preceded by `~` to indicate it is an estimate. |
| **rows in/out** | ANALYZE | Actual rows consumed from children and produced by this operator, with selectivity percentage. |
| **self** | ANALYZE | Wall-clock time spent in this operator alone (excludes children). |
| **total** | ANALYZE | Wall-clock time including all descendant operators. |

### Tree Symbols

| Symbol | Meaning |
|--------|---------|
| `└─` | Last (or only) child of parent. |
| `├─` | Non-last child of parent. |
| `│` | Continuation of a parent branch. |
| `[label]` | Edge label — names the role of this child (e.g., `[probe]`, `[build]`). |
| `→` | Static annotation (plan-time note). |
| `⚠` | Warning about potential performance concern. |

---

## Operators

### Data Access Operators

#### Scan

Reads rows from a table provider. This is the most common leaf operator.

```
Scan (table: events, provider: parquet, columns: [user_id, event_type, age])  ~1,000,000 rows
```

**Details:**
- `table` — the table name from the FROM clause.
- `provider` — the storage backend (parquet, csv, json, hdf5, etc.).
- `columns` — the columns actually read (projection pushdown). `*` means all columns.
- `statistics filter` — if present, a predicate pushed down for partition-level pruning.

**Access strategies** (shown in annotations when applicable):
- **Table Scan** — full sequential read of all rows/chunks. Default when no index or filter hint exists.
- **Statistics Scan** — the scan has a filter hint and a source index with min/max chunk statistics. Chunks whose statistics prove no rows can match are skipped entirely. Shown when `statistics filter` appears in details.
- **Bloom Filter Scan** — a join operator has supplied build-side key values, and bloom filters exist for the join column. Chunks where no build-side key could possibly exist (according to the bloom filter) are skipped. This is a runtime optimization: static EXPLAIN will show "bloom filters available on [column]" as an annotation; EXPLAIN ANALYZE will show actual pruning counts.
- **Sorted Index Scan** — similar to bloom, but uses sorted value indexes instead of bloom filters for chunk pruning during joins.
- **Index Seek** — the scan uses a sorted index to fetch only the exact matching rows rather than reading entire chunks. Occurs when the planner detects that a seekable provider with a sorted index can service point lookups.

**ANALYZE annotations:**
- `row groups: N total, M pruned (X%)` — Parquet row group pruning via predicate pushdown to the provider.
- `chunks: N total, M pruned` — chunk-level pruning via the source index.

**What to look for:**
- A Scan with no filter hint on a large table likely performs a full table scan. Consider adding WHERE predicates or building indexes.
- If bloom filters are available but pruning is low in ANALYZE, the join key may have high overlap with the scanned table (most chunks contain matching keys).

#### Index Scan

Replaces a Scan + Sort combination when a sorted index exists for the ORDER BY column and the provider supports seeking.

```
Index Scan (table: events, provider: parquet, column: timestamp, direction: DESC)  ~1,000,000 rows
```

**Details:**
- `table`, `provider`, `columns` — same as Scan.
- `column` — the indexed column used for ordering.
- `direction` — ASC or DESC.

**Key insight:** Index Scan avoids materializing the entire dataset for sorting. Rows are emitted in index order, fetched via random access. This is especially beneficial with LIMIT — only the needed rows are fetched.

**When it appears:** The planner substitutes Index Scan for Scan + Sort when:
1. ORDER BY references a single column.
2. A sorted index exists for that column in the source index.
3. The provider implements seekable random access.

If you see a Sort operator instead, no suitable index was found.

### Filtering Operators

#### Filter

Evaluates a predicate expression against each input row, discarding rows that don't match.

```
Filter (predicate: age > 30)  ~330,000 rows
```

**Cardinality estimation:** The cost model applies selectivity factors to the input row estimate:
- Equality (`col = value`): uses 1/NDV if column statistics exist, otherwise 0.10.
- AND: multiplies child selectivities.
- OR: adds child selectivities (capped at 1.0).
- LIKE/ILIKE: 0.25.
- REGEXP: 0.10.
- IS NULL: uses actual null ratio from manifest statistics, otherwise 0.10.
- IN (list): min(1.0, count × per-value selectivity).
- BETWEEN: 0.25.
- NOT: 1.0 − inner selectivity.
- Default (unknown predicate): 0.33.

**Warnings:**
- `⚠ Pattern matching predicate requires full scan of input rows.` — the predicate uses LIKE, ILIKE, or REGEXP, which cannot be optimized with indexes.

#### Limit

Caps output to a fixed number of rows, optionally skipping an offset.

```
Limit (limit: 100, offset: 50)  ~100 rows
```

The estimated row count is `min(child_estimate, limit + offset)`.

#### Distinct

Deduplicates rows, keeping only unique combinations of all output columns.

```
Distinct  ~N rows
```

**Behavior:** Uses a hash set for streaming deduplication. If the memory budget is exceeded, spills to disk.

**Warnings:**
- `⚠ Distinct materializes unique rows in memory.` — large inputs with high cardinality may consume significant memory.

### Join Operators

#### Join (INNER, LEFT, RIGHT, FULL OUTER, CROSS)

Combines rows from two input sources.

```
INNER Join (strategy: hash, on: e.user_id = u.id)  ~33,000 rows
├─ [probe] ...
└─ [build] ...
```

**Details:**
- `strategy` — the physical join algorithm:
  - `hash` — hash join. Builds a hash table from the build side (right child), then probes with the left child. Requires equi-join condition.
  - `hash+filter` — hash join with a residual non-equi predicate applied after hash match.
  - `nested-loop` — nested loop join. Used when no equi-join keys can be extracted, or for CROSS JOIN. O(N×M) complexity.
- `on` — the join condition.

**Child labels:**
- `[probe]` — the left/driving side. Streamed through the hash table.
- `[build]` — the right side. Materialized into a hash table (for hash joins).

**Cardinality estimation:**
- CROSS JOIN: left × right.
- Equi-join with statistics: left × right / max(NDV_left, NDV_right).
- Equi-join without statistics: left × right × 0.10.
- Outer joins: result ≥ preserved side's row count.

**Warnings:**
- `⚠ CROSS JOIN produces a cartesian product; output size = left × right.` — ensure this is intentional.
- `⚠ FULL OUTER JOIN materializes both sides in memory.`
- `⚠ Nested-loop join has O(n*m) complexity. Consider rewriting the ON condition as an equi-join.`

**Bloom filter interaction:** After building the hash table, the join operator inspects the probe side for ScanOperators with bloom filter indexes. If found, it extracts distinct join key values from the hash table and passes them to the scan for chunk-level pruning. This is invisible in static EXPLAIN but shows as pruning statistics in ANALYZE.

#### Lateral Join

Re-executes the right side for each row from the left side. Used for LATERAL subqueries and table-valued function calls.

```
Lateral Join (type: CROSS)  ~N rows
├─ [driving] ...
└─ [lateral] ...
```

**Warning:** The right side is re-executed for every left-side row. This is inherently O(N×M) but is the only correct strategy for correlated lateral references.

### Projection and Reshaping

#### Project

Selects and/or computes output columns.

```
Project (user_id, event_type, UPPER(name) AS upper_name)
```

Row count passes through unchanged — Project does not add or remove rows.

#### Alias

Attaches a table qualifier to downstream column references.

```
Alias (as: e)
```

Purely structural. Does not affect execution or row count.

#### Pivot

Rotates rows into columns by aggregating values for each distinct pivot key.

```
Pivot (pivot: region, aggregate: SUM(revenue), values: [US, EU, APAC])
```

**Details:**
- `pivot` — the column whose distinct values become output columns.
- `aggregate` — the aggregate function applied per group per pivot value.
- `values` — explicit pivot values (from IN clause), or auto-discovered at runtime.

#### Unpivot

Rotates columns into rows — the inverse of Pivot.

```
Unpivot (value: metric_value, name: metric_name, columns: [revenue, cost, margin], include nulls: false)
```

Each input row produces N output rows (one per source column), so estimated rows = input × column count.

### Aggregation

#### GroupBy

Groups rows by key expressions and computes aggregate functions.

```
GroupBy (keys: [region, year], aggregates: [SUM(revenue), COUNT(*)])  ~50 rows
```

**Warning:** `⚠ GroupBy materializes all groups in memory.` — with high-cardinality group keys, memory consumption can be significant.

### Sorting

#### Sort

Materializes all input rows and sorts them.

```
Sort (timestamp DESC, id ASC)  ~1,000,000 rows
    → bounded top-N sort (N=100)
```

**Annotations:**
- `→ bounded top-N sort (N=100)` — a LIMIT is present, so the sort uses a bounded heap of size N instead of sorting the entire input. Dramatically reduces memory for large inputs with small limits.

**Warning (when no top-N):** `⚠ ORDER BY materializes all input rows for sorting.`

**Absence of Sort:** If you expected sorted output but see no Sort operator, the planner may have substituted an Index Scan (see above).

### Window Functions

#### Window

Computes window functions over partitioned and/or ordered input.

```
Window (functions: [ROW_NUMBER() OVER (PARTITION BY region ORDER BY revenue DESC)])
```

Window operators materialize their partition and sort the input as needed. Multiple window functions sharing the same OVER specification are grouped into a single Window operator.

### Subqueries

#### Subquery

Wraps an inline subquery with an alias.

```
Subquery (alias: top_users)
└─ ...
```

#### Scalar Subquery

Executes a correlated or uncorrelated scalar subquery for each outer row.

```
Scalar Subquery (column: max_revenue)
├─ [outer] ...
└─ [inner] ...
```

**Warning:** Correlated scalar subqueries re-execute the inner plan for each outer row. Consider rewriting as a JOIN if performance is a concern.

#### CTE (Common Table Expression)

```
CTE (name: recent_events, mode: materialized)
└─ ...
```

**Details:**
- `mode: materialized` — the CTE result is cached and reused across multiple references.
- `mode: inlined` — the CTE is expanded inline at each reference point (like a macro).

#### Recursive CTE

```
Recursive CTE (name: hierarchy)
└─ [anchor] ...
```

Executes the anchor member, then iteratively executes the recursive member until no new rows are produced.

### Other Operators

#### Late Materialize

Defers fetching expensive columns until after filtering reduces the row set.

```
Late Materialize (source: events (parquet), key: _row_id, fetch: [payload, metadata])
```

This is a cost-based optimization. The planner identifies columns marked as `Expensive` in provider capabilities and defers their retrieval. After WHERE/JOIN filtering reduces rows, only surviving rows' expensive columns are fetched via keyed lookup.

#### Set Operation

```
SetOperation (UNION ALL)
├─ ...
└─ ...
```

Combines two result sets. Variants: UNION, UNION ALL, INTERSECT, INTERSECT ALL, EXCEPT, EXCEPT ALL.

#### Table Function

```
Table Function (function: generate_series, arguments: [1, 100])
```

A table-valued function producing rows programmatically.

#### Empty Row

```
Empty Row
```

A virtual source producing a single row with no columns. Used for queries like `SELECT 1 + 1` with no FROM clause.

---

## Cost Model

### Row Estimates

Every operator carries an estimated row count (shown as `~N rows`). These estimates propagate bottom-up:

1. **Scan** — uses `EstimatedRowCount` from the provider's capabilities (reported by the storage backend).
2. **Filter** — applies selectivity factors to the input estimate (see Filter section above).
3. **Join** — cross-product scaled by join selectivity (see Join section above).
4. **Limit** — capped at limit + offset.
5. **Project, Alias** — pass-through (same as child).
6. **GroupBy** — currently uses child estimate (conservative; in practice, output is often much smaller).

### Statistics Sources

When a table has a **query results manifest** (`.datum-manifest` sidecar file), per-column statistics are available:

- **Estimated Distinct Count (NDV)** — used for equality selectivity (1/NDV) and join cardinality.
- **Null Ratio** — used for IS NULL / IS NOT NULL selectivity.

Without manifest statistics, the cost model falls back to fixed heuristic selectivities.

### Column Cost Classification

Providers classify columns into cost tiers via `ProviderCapabilities.ColumnCosts`:

| Tier | Meaning | Example |
|------|---------|---------|
| **Cheap** | Readily available, minimal I/O. | Pre-parsed header fields, in-memory columns. |
| **Moderate** | Requires computation or modest I/O. | CSV field parsing, columnar decompression. |
| **Expensive** | Significant I/O or computation. | ZIP decompression, network fetch, large BLOBs. |

The planner uses these tiers to decide late materialization: expensive columns are deferred until after filtering reduces the row count.

---

## Interpreting ANALYZE Output

EXPLAIN ANALYZE executes the query and annotates each node with actual metrics. Compare estimates to actuals to find planning inaccuracies.

### Row Count Accuracy

```
Filter (predicate: status = 'active')  ~100,000 rows  |  rows in: 1,000,000 → out: 950,000 (95.0%)
```

Here the estimate was ~100,000 but actual output was 950,000. The selectivity assumption (0.10 for equality) was wrong — the `status` column has very few distinct values. This suggests building a manifest with `discover-features` to give the planner NDV statistics.

### Time Attribution

- **self time** is the time spent in this operator's own logic (evaluating predicates, hashing, etc.).
- **total time** includes all descendant operators.
- `self = total - sum(children's total)`.

A high self-time on a Filter means predicate evaluation is expensive. A high self-time on a Scan means I/O dominates. A high self-time on a Join means hash table build or probe is the bottleneck.

### Pruning Statistics

```
Scan (table: events, provider: parquet, columns: [user_id, timestamp])  ~10,000,000 rows
    row groups: 128 total, 96 pruned (75%)
```

This tells you 75% of Parquet row groups were skipped due to predicate pushdown. If pruning is 0%, the predicate may not be pushable (e.g., it involves a function call) or the data isn't sorted/clustered on the filter column.

---

## Warnings Reference

| Warning | Meaning | Action |
|---------|---------|--------|
| `CROSS JOIN produces a cartesian product` | Output = left × right rows. | Ensure intentional; add ON condition if not. |
| `FULL OUTER JOIN materializes both sides in memory` | Both inputs buffered entirely. | Consider if LEFT/RIGHT JOIN suffices. |
| `Nested-loop join has O(n*m) complexity` | No equi-join key found. | Rewrite ON as equality comparison if possible. |
| `Pattern matching predicate requires full scan` | LIKE/ILIKE/REGEXP cannot use indexes. | Consider pre-computing or exact-match alternatives. |
| `ORDER BY materializes all input rows for sorting` | Full sort without top-N optimization. | Add LIMIT to enable bounded heap sort, or build a sorted index. |
| `GroupBy materializes all groups in memory` | All groups held in memory. | Reduce group key cardinality or pre-aggregate. |

---

## Structured Plan (Protobuf)

The gRPC `ExplainResponse` includes a structured `ExplainPlanNodeMessage` tree for programmatic consumption. Each node contains:

| Field | Type | Description |
|-------|------|-------------|
| `operator_name` | string | Operator type name. |
| `details` | string | Operator configuration (same as text rendering). |
| `children` | repeated ExplainPlanNodeMessage | Child operator nodes. |
| `child_label` | string | Edge label (e.g., "probe", "build"). |
| `warnings` | repeated string | Performance warnings. |
| `annotations` | repeated string | Static plan notes. |
| `estimated_rows` | int64 | Estimated row count (check `has_estimated_rows`). |
| `has_estimated_rows` | bool | Whether `estimated_rows` was populated (distinguishes 0 from unknown). |
| `runtime` | ExplainRuntimeMetrics | Runtime metrics (ANALYZE only). |
| `access_method` | ExplainAccessMethod | Access method for scan operators (`TABLE_SCAN`, `INDEX_SCAN`). `UNSPECIFIED` for non-scan operators. |
| `properties` | map&lt;string, string&gt; | Structured operator properties. Same data as `details` but in key-value form for programmatic consumption. |

### ExplainRuntimeMetrics

| Field | Type | Description |
|-------|------|-------------|
| `rows_produced` | int64 | Actual rows emitted by this operator. |
| `rows_consumed` | int64 | Actual rows consumed from child operators. |
| `self_time_us` | int64 | Self time in microseconds. |
| `total_time_us` | int64 | Total time in microseconds (inclusive of children). |
| `runtime_annotations` | repeated string | Runtime notes (e.g., pruning stats). |

---

## Planned Enhancements

### Materialization and Memory Estimates

Operators that materialize data (Sort, GroupBy, Hash Join build side, Distinct, Window) will report estimated memory consumption.
