# Operators

Quick reference for the execution operators in DatumIngest. The query planner turns SQL (and programmatic query trees) into a DAG of these operators; runtime execution is a pure pull-based iteration through the DAG.

## Execution model

Every operator implements `IQueryOperator`:

```csharp
IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context);
OperatorPlanDescription DescribeForExplain();
IQueryOperator RewriteExpressions(Func<Expression, Expression> rewriter) => this;
```

- **Pull-based.** Each operator pulls from its child(ren) via `ExecuteAsync`. No central scheduler; the downstream `await foreach` drives the upstream.
- **Batched.** Rows flow in `RowBatch` instances, typically 1024 rows per batch. Operators don't usually emit one row at a time.
- **Streaming unless noted.** Most operators are one-row-in, one-row-out with constant memory. A few are **blocking** — they must consume all input before emitting output (noted below).
- **Arena-backed values.** Each `RowBatch` carries an `Arena` where non-inline reference payloads (long strings, vectors, images, etc.) live. Stabilization across operators copies these into the downstream batch's arena.

---

## Sources

Operators that produce rows without pulling from another operator.

### `ScanOperator`

Reads rows from a table provider with aggressive pushdown optimization.

- **Projection pushdown.** Only `_requiredColumns` are decoded from the source.
- **Filter hint.** An advisory `Expression` passed down; providers may skip partitions based on column statistics.
- **Chunk-level index pruning.** When a `SourceIndex` is available:
  - **Zone maps** (min/max per column per chunk) — skip chunks outside the predicate's range.
  - **Bloom filters** — skip chunks that definitely can't contain the literal or a build-side key.
  - **Sorted / B+Tree indexes** — skip chunks not in `FindChunksContaining(literal)`.
  - **Bitmap indexes** — skip chunks with zero set bits for the literal.
- **Row-level bitmap filtering.** Within surviving chunks, if the predicate's column has a bitmap index, build a per-row mask and only decode matching rows.
- **Exact-seek path.** For equality predicates on sorted/B+Tree-indexed columns, skip the full-chunk decode and use `index.FindExact(value)` directly.

FilterOperator upstream still re-evaluates the predicate for correctness — idempotent on rows that already survived index prefiltering.

### `IndexScanOperator`

Walks the entries of an `IColumnIndex` in index order, fetching each row by random access. Produces sorted output without materializing-and-sorting the entire dataset. Primary feeder for `MergeJoinOperator`.

### `FunctionSourceOperator`

Executes a table-valued function and streams its rows. Arguments are evaluated once at execution start.

### `SingleEmptyRowOperator`

Produces exactly one row with no columns. Used as the source for `SELECT` statements without a `FROM` clause (e.g., `SELECT 1 AS n`), and as the initial input for recursive CTE anchors.

---

## Filtering

### `FilterOperator`

General-purpose `WHERE` evaluation. Evaluates a predicate expression per row via `ExpressionEvaluator`, emits rows where it's truthy. Handles anything the scan can't push down: function calls, arithmetic, cross-column comparisons, LIKE/ILIKE, CASE, etc.

### `DistinctOperator`

Streaming duplicate elimination. Yields the first occurrence of each distinct row. Uses a `HashSet<DataValue>` (single-column) or `CompositeKey` (multi-column) to track seen rows. Memory is unbounded — scales with the number of distinct values.

### `LimitOperator`

`LIMIT` / `OFFSET`. Skips `Offset` rows then emits up to `Limit` rows. Propagates cancellation upstream once the limit is reached, so scans short-circuit cleanly. Fast-paths entire batches through unchanged when the batch fits within the remaining limit.

### `SampleScanOperator`

`TABLESAMPLE` clause. Two strategies:
- **Bernoulli** — each row independently included with probability `percentage / 100`.
- **System** — chunk-level inclusion (lower overhead, coarser).

---

## Projection

### `ProjectOperator`

Evaluates `SELECT` column expressions against each row, producing the projected column set. Also handles:
- **`LET` bindings** — named intermediate values computed once per row, available to subsequent LET bindings and to the SELECT list.
- **`ASSERT` clauses** — runtime checks with abort / warn / skip failure modes.

Builds a `ProjectionSchema` once from the first row; subsequent rows only allocate a `DataValue[]` per row.

### `AliasOperator`

Prefixes column names with a table alias (e.g. `t.col`) so qualified references resolve correctly. Retains unqualified names too, so either form works.

### `RowEnricherOperator`

Evaluates a fixed set of pure scalar expressions per row and appends them as hidden columns on the output batch. Inserted by [common-subexpression elimination](common-subexpression-elimination.md) to share work between repeated subexpressions; the columns it adds (named `__cse_*`) ride on the batch's `ColumnLookup` so downstream operators reference them like any source column. Earlier enrichments in the same operator are **not** visible to later ones — when subsumed subtrees need dependent evaluation, the planner stacks multiple `RowEnricherOperator`s in dependency order.

### `ModelInvocationOperator`

Hoisted target for `models.<name>(...)` calls. Evaluates the model's input expressions per row, dispatches the model in batches (one batch per upstream `RowBatch`), and scatters outputs back as a hidden column (named `__model_*`). One operator per distinct hoisted call site; nested model calls become a stack of operators with the inner one closer to the source. See [common-subexpression elimination](common-subexpression-elimination.md) for the planner pass that inserts these operators.

---

## Aggregation

### `GroupByOperator`

`GROUP BY` + aggregates. Two modes:
- **Hash mode (default)** — blocking. Consumes all input, builds a hash table of groups keyed by the GROUP BY expressions, accumulates per-group state, emits one row per group.
- **Streaming sorted mode** — non-blocking. Assumes input is already sorted on the grouping keys (e.g., from `MergeJoinOperator`); emits each group as soon as the key changes.

### `WindowOperator`

Window function evaluation (`OVER (PARTITION BY ... ORDER BY ...)`). **Blocking** — all input rows must be materialized before output is emitted, because window functions need visibility of the entire partition.

### `PivotOperator`

`PIVOT` clause. **Blocking.** Reshapes tabular data by rotating distinct values of a pivot column into new output columns, with one aggregate per cell.

### `UnpivotOperator`

`UNPIVOT` clause. Streaming. Rotates source columns into `(name, value)` pairs — emits one output row per source column per input row.

### `FoldScanOperator`

`SCAN` expression (DatumIngest extension — prefix-scan / fold over ordered partitions). Each row's output feeds back as the accumulator for the next row: `output[i] = f(output[i-1], input[i])`. **Blocking within a partition** — materializes the partition first because the fold is sequential.

---

## Joins

### `JoinOperator`

The workhorse join operator. Supports `INNER`, `LEFT`, `RIGHT`, `FULL OUTER`, `CROSS`, `LEFT SEMI`, `LEFT ANTI-SEMI`.

Uses expression-based hash join for any `ON` condition containing equality conjuncts (including function calls and compound keys), with an optional residual filter for non-equi parts. Falls back to nested-loop only when no hash key can be extracted.

For large builds, `GraceHashJoinExecutor` handles out-of-core partition spills.

### `MergeJoinOperator`

Sort-merge join over two index-ordered input streams. Each input is an `IndexScanOperator` over a `SortedIndex` or `BPlusTreeColumnIndex`. Two-pointer algorithm — `O(n + m)` with `O(k)` memory (buffer for matching-key runs).

### `LateralJoinOperator`

Correlated (lateral) join. Re-executes the right-hand operator for every left-side row, with the left row set as `ExecutionContext.OuterRow` so the right side can reference left-side columns. Supports `CROSS` (skip left rows with no right matches) and `LEFT` (emit left + NULLs when no right rows match). Also the rewrite target for correlated subqueries.

---

## Set operations

### `SetOperationOperator`

`UNION`, `INTERSECT`, `EXCEPT`, each in `ALL` and distinct variants.

| Operation | Behavior |
|---|---|
| `UNION ALL` | Concatenate both streams, no dedup |
| `UNION` | Concatenate + dedup via hash set |
| `INTERSECT` | Hash set of left side, emit right rows found in it (dedup) |
| `INTERSECT ALL` | Hash multiset (counts) |
| `EXCEPT` | Hash set of right side, emit left rows not in it (dedup) |
| `EXCEPT ALL` | Hash multiset |

---

## Ordering

### `OrderByOperator`

`ORDER BY`. Two code paths:
- **Full sort** — materialize all rows, sort by the ordering expressions, emit.
- **Top-N (bounded max-heap)** — when paired with an upstream `LimitOperator` that can push `TopNRows` down, keep only the top N rows in `O(n log N)` time and `O(N)` memory instead of `O(n log n)` / `O(n)`.

---

## CTEs

### `CommonTableExpressionOperator`

Non-recursive `WITH` clause. Two modes:
- **Inlined** — the CTE body is spliced into each reference site; executed once per reference.
- **Materialized** — the CTE body runs once, results are cached in memory and replayed for each reference.

Mode selection is driven by a cost heuristic in the planner.

### `RecursiveCommonTableExpressionOperator`

`WITH RECURSIVE`. Executes the anchor member once, then iterates the recursive member using the previous iteration's output as the working table, stopping when no new rows are produced or `ExecutionContext.MaxRecursionDepth` is hit. Emits all accumulated rows (anchor + every iteration).

---

## Subqueries

All three SQL subquery forms are **rewritten at plan time** by `QueryPlanner` into concrete operators. If a raw `SubqueryExpression`, `InSubqueryExpression`, or `ExistsExpression` AST node reaches `ExpressionEvaluator.Evaluate`, it throws — the contract is that subquery rewriting must complete before execution.

| SQL form | Planner rewrite | Runtime |
|---|---|---|
| `WHERE col IN (SELECT …)` | Semi-join on the IN key | `JoinOperator` with `JoinKind.LeftSemi` |
| `WHERE NOT col IN (SELECT …)` | Anti-semi-join | `JoinOperator` with `JoinKind.LeftAntiSemi` |
| `WHERE EXISTS (SELECT …)` | Semi-join | Same |
| `WHERE NOT EXISTS (SELECT …)` | Anti-semi-join | Same |
| Uncorrelated scalar subquery (`… = (SELECT max(x) FROM t)`) | Constant-folded at plan time; runs once, binds result as a literal | `LiteralValueExpression` in the predicate |
| Correlated scalar subquery (`… > (SELECT avg(x) FROM t WHERE t.fk = outer.id)`) | Lateral scalar subquery | `ScalarSubqueryOperator` or `LateralJoinOperator` |

### `ScalarSubqueryOperator`

Executes a correlated scalar subquery per outer row and augments each outer row with the scalar result in a synthetic column. The inner query sees the outer row via `ExecutionContext.OuterRow`, enabling column references to resolve up the scope chain.

### `SubqueryOperator`

Wraps an inner `SelectStatement`'s operator tree and applies the subquery's alias, producing a derived table for `FROM (SELECT …) AS alias`.

---

## Sampling

### `BalancedSampleOperator`

Per-class reservoir sampling (Algorithm R). Returns exactly `countPerClass` rows from each distinct stratification-key class (or fewer if the class has fewer rows). Single-pass streaming with one reservoir per distinct class.

### `StratifiedSampleOperator`

Uniform Bernoulli filter at a given percentage. Each row is independently included with probability `percentage / 100`. Class proportions are preserved *in expectation* because the rate is constant across all classes.

---

## Operator-level cheat sheet

| Operator | Streaming? | Typical input shape |
|---|---|---|
| `ScanOperator` | ✅ | — (source) |
| `IndexScanOperator` | ✅ | — (source, index-ordered) |
| `FunctionSourceOperator` | ✅ | — (source) |
| `SingleEmptyRowOperator` | ✅ | — (source) |
| `FilterOperator` | ✅ | any |
| `DistinctOperator` | ✅ (hash set grows) | any |
| `LimitOperator` | ✅ | any |
| `SampleScanOperator` | ✅ | any |
| `ProjectOperator` | ✅ | any |
| `AliasOperator` | ✅ | any |
| `RowEnricherOperator` | ✅ | any |
| `ModelInvocationOperator` | ✅ (per-batch dispatch) | any |
| `GroupByOperator` | ⚠️ blocking in hash mode, streaming in sorted mode | any / sorted |
| `WindowOperator` | ❌ blocking | any |
| `PivotOperator` | ❌ blocking | any |
| `UnpivotOperator` | ✅ | any |
| `FoldScanOperator` | ❌ blocking within partition | ordered by partition key |
| `JoinOperator` | ⚠️ build side materializes | any, any |
| `MergeJoinOperator` | ✅ | both sides sorted on join key |
| `LateralJoinOperator` | ✅ | any, any |
| `SetOperationOperator` | ⚠️ dedup variants hash-materialize | any, any |
| `OrderByOperator` | ❌ blocking (or bounded Top-N) | any |
| `CommonTableExpressionOperator` | depends on mode | — |
| `RecursiveCommonTableExpressionOperator` | ❌ iterative blocking | — |
| `ScalarSubqueryOperator` | ✅ | any (outer) |
| `SubqueryOperator` | ✅ | derived |
| `BalancedSampleOperator` | ✅ (bounded memory per class) | any |
| `StratifiedSampleOperator` | ✅ | any |

**Streaming** = constant or near-constant memory; row-for-row output.
**Blocking** = must consume all input before emitting any output.

---

## Execution contracts to remember

1. **Subquery AST nodes must be rewritten before execution.** The evaluator throws on raw `SubqueryExpression` / `InSubqueryExpression` / `ExistsExpression`.
2. **Expressions can be hoisted.** `IQueryOperator.RewriteExpressions` is a default-nop that operators override to recursively replace expression trees. `LiteralHoister` uses this to pre-materialize literal `DataValue`s at execution start.
3. **Correlated subqueries resolve columns via `EvaluationFrame.OuterRow`.** Set by `LateralJoinOperator` / `ScalarSubqueryOperator` before running the inner plan.
4. **Arena lifetime follows batches.** An operator's output batch owns its arena; stabilization copies non-inline values across batch boundaries. Caches that must outlive a batch (cast-target names, IN literal sets, hoisted literals) go in the long-lived `ExecutionContext.Store`.
5. **Return batches to the pool.** Consumers `Pool.ReturnRowBatch(batch)` after processing; arena refcount drives actual pool-or-dispose decisions.

---

## See also

- [special-functions.md](special-functions.md) — scalar/aggregate functions and DatumIngest-specific syntax extensions.
