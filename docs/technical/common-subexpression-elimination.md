# Common Subexpression Elimination

Common subexpression elimination (CSE) is a plan-time pass that detects when the same expression appears at multiple sites in a query and rewrites it to evaluate only once per row. The result is materialised as a hidden column that every reference site reads from, replacing the duplicate AST occurrences with `ColumnReference`s.

CSE is the engine's answer to two distinct sources of redundant work:

1. **Pure scalar duplication** — `WHERE expensive(x) > 10 AND expensive(x) < 100`, `ORDER BY upper(name)` paired with `SELECT upper(name)`, etc.
2. **Model call duplication** — `SELECT models.phi3('test'), models.phi3('test')` should dispatch the model once, not twice.

The first is handled by [`CommonSubexpressionEliminator`](../../src/DatumV/Execution/CommonSubexpressionEliminator.cs); the second by [`ModelInvocationHoister`](../../src/DatumV/Execution/ModelInvocationHoister.cs). They run in series during planning and share the same dependency-ordering helper. This page documents both.

## Why It's Worth Doing

A SQL writer may legitimately repeat an expression — once in `WHERE`, once in `SELECT`, once in `ORDER BY` — because the language doesn't always offer a clean way to bind a per-row value across clauses. [`LET` bindings](../sql/let-bindings.md) live in the `SELECT` projection but the planner lifts referenced LETs into upstream rungs so they're visible to `WHERE` predicates and to lateral function-source arguments (e.g. `CROSS JOIN unnest(classes)` where `classes` is a LET). Outside those lifted sites the LET name doesn't exist as a column, so CSE remains the answer for genuine cross-clause sharing. Without CSE, the expression evaluates once per clause per row.

For pure scalar work this is a constant-factor cost. For model invocations it's a correctness concern: the engine guarantees *same call site → one evaluation per row*, even for nondeterministic models, because users naturally treat `models.phi3('test')` as a referentially transparent reference rather than a fresh dispatch.

CSE moves the cost off the user. Identical SQL with the same expression repeated runs the same plan whether it appears at one site or five.

## Eligibility

A subtree is eligible for hoisting when **all** of the following hold:

- Its structural fingerprint (computed by `QueryExplainer.FormatExpression`) appears at two or more sites within a single linear chain of operators.
- Every leaf function is **pure** — `IScalarFunction.IsPure == true`. Functions like `random()`, `now()`, `current_timestamp` are excluded.
- It is **non-trivial** — not just a `ColumnReference` or `LiteralExpression`. Hoisting a column reference adds operator overhead with no benefit.
- It does **not** reference any LET-bound name. The hoist sits upstream of the projection where LETs live, so a hoisted enrichment can't see them yet.
- It does **not** contain a `LambdaExpression`. Two textually different lambdas with alpha-equivalent bodies (e.g. `x -> x + 1` and `y -> y + 1`) aren't unified.
- It is **not** an aggregate, window, or subquery expression. These cross row boundaries; CSE works at row scope.

Model calls (`models.<name>(...)`) follow a parallel rule set in `ModelInvocationHoister`: a model call is **always** considered for hoisting, regardless of purity, and hoists out of every expression site that reaches a chainable operator.

## Two Stages

Both passes are structured the same way:

### Stage 1 — Cross-Clause

Walks linear chains of `{Project, Filter, OrderBy}` operators. The chain ends at the first non-chainable boundary (`GroupBy`, `Window`, `Join`, scan, etc.). Within a chain, any fingerprint appearing in **two or more distinct operators** is hoisted into a single shared operator placed upstream of the deepest reference (so eager evaluation only happens for rows that survive prior filters).

```
Filter (predicate: __cse_xc0 > 10)        ← rewritten
  └─ Project (upper(name) AS u, __cse_xc0 + 1)  ← rewritten
       └─ RowEnricher (__cse_xc0 = upper(name))  ← inserted
            └─ Scan
```

Cross-clause hoists use the column prefix `__cse_xc{N}` for pure scalars and `__model_<name>_xc{N}` for model calls.

### Stage 2 — Within-Operator

After cross-clause hoisting, residual duplicates that live entirely inside one operator's expressions get their own pass. Each operator type has its own column prefix so multiple stage-2 hoists in the same plan don't collide:

| Operator | Prefix | Notes |
|---|---|---|
| `Project` | `__cse_{N}` | LET-name unification: if a duplicate's fingerprint matches an existing LET binding's body, references rewrite to that LET name instead of allocating a hidden column. |
| `Filter` | `__cse_f{N}` | Catches `WHERE expensive(x) > 10 AND expensive(x) < 100`. The planner splits AND-compound predicates into separate filters, so AND-only duplicates are usually caught by stage 1; OR-keeping-one-filter is the common stage-2 case. |
| `OrderBy` | `__cse_o{N}` | Rare. The realistic case is sort-key + tiebreaker derived from the same expression: `ORDER BY concat(a,b) DESC, length(concat(a,b)) ASC`. |
| `GroupBy` | `__cse_g{N}` | Hoists across group keys and aggregate arguments: `SELECT SUM(upper(name)), upper(name) FROM t GROUP BY upper(name)` shares one `upper(name)` evaluation. |
| `Window` | `__cse_w{N}` | Hoists across the window column's arguments, partition keys, and order items: `RANK() OVER (PARTITION BY upper(name) ORDER BY upper(name))`. |

Models go through the same operator types but with prefix `__model_<name>_{N}`.

## LET-Name Unification

Inside a `ProjectOperator`, when an existing `LET` binding's body matches a CSE-eligible duplicate, references rewrite to the LET's user-visible name instead of allocating a synthetic `__cse_N` column.

```sql
SELECT
  LET u = upper(name),
  upper(name) AS shouted,
  length(upper(name)) AS shouted_length
FROM t
```

After CSE the projection columns reference `u`, not `__cse_0`. The plan reads cleanly in EXPLAIN and the LET stays load-bearing.

LET unification is **only** applied within `Project` — not in stage 1 (cross-clause). Cross-clause hoists land upstream of the projection, where `LET` names don't yet exist as columns. A cross-clause duplicate that happens to match a LET body is hoisted to a `__cse_xc{N}` column, and the LET body is rewritten to reference that column.

## Subtree Subsumption

When two CSE-eligible expressions are nested — e.g. both `f(g(x))` and `g(x)` qualify — naive hoisting would compute `g(x)` twice (once inside `f(g(x))`'s enrichment, once standalone).

The pass detects this and stacks `RowEnricherOperator`s in dependency order:

```
... uses __cse_1 (= f(g(x)) which has been rewritten to f(__cse_0))
  └─ RowEnricher (__cse_1 = f(__cse_0))   ← outer level
       └─ RowEnricher (__cse_0 = g(x))    ← inner level
            └─ Scan
```

[`HoistDependencyOrdering.OrderByDependency`](../../src/DatumV/Execution/HoistDependencyOrdering.cs) topologically groups the hoists into levels — level 0 references no other hoists, level *n* may reference any earlier level. One operator is emitted per level, with level 0 closest to the source. Within a level, the order is unspecified (level peers don't depend on each other).

The same machinery applies to model hoists. If a query nests one model call inside another (`models.classifier(models.embedder(x))`), the embedder's `ModelInvocationOperator` lands below the classifier's, and the classifier's argument rewrites to the embedder's hidden column.

## Pass Ordering

In [`QueryPlanner.Finalize`](../../src/DatumV/Execution/QueryPlanner.cs):

```
ModelInvocationHoister.Hoist(plan, modelCatalog)
  ↓
CommonSubexpressionEliminator.Eliminate(plan, functionRegistry)
```

Models hoist first because they're not eligible for scalar CSE — by the time the eliminator runs, every model call has been replaced by a `ColumnReference` to a `__model_*` column, and any remaining textual duplicates have already been deduplicated to share that column.

A subtle consequence: scalar CSE can't directly dedup textually-identical model calls. That's the model hoister's concern, and it handles it via structural fingerprint matching inside `ModelHoistCollector`. Once both passes are done, the rule "same call site → one evaluation per row" holds for both pure scalars and model calls.

## Runtime Side

Both hoist passes lower into operator nodes that ride on top of the existing batch + arena machinery:

- **`RowEnricherOperator`** — evaluates pure scalar enrichments per row, appends them as hidden columns on the augmented `RowBatch`. Earlier enrichments in the same operator are *not* visible to later ones; if dependent computation is needed (e.g. subsumed subtrees), the planner stacks a second operator.
- **`ModelInvocationOperator`** — same shape, but each operator wraps a single model call. Inputs are evaluated per row and dispatched in batches; outputs are scattered back as a hidden column. See [Operators](operators.md) for the runtime contract.

Hoisted columns appear in plan output with their synthetic names (`__cse_*`, `__model_*`), making EXPLAIN a faithful picture of how often each expression actually evaluates.

## Examples

### Cross-clause WHERE + SELECT

```sql
SELECT upper(name) AS shouted, length(upper(name)) AS n
FROM t
WHERE upper(name) LIKE 'A%'
```

Stage 1 hoists `upper(name)` to `__cse_xc0` upstream of the filter. Both the filter and the projection read the cached column.

### Within-Filter

```sql
SELECT *
FROM t
WHERE expensive(x) > 10 OR expensive(x) < 0
```

The OR keeps both predicates inside one `FilterOperator`. Stage 2 hoists `expensive(x)` to `__cse_f0` upstream of the filter.

### Model dedup

```sql
SELECT models.phi3('test'), models.phi3('test')
```

The structural-fingerprint collector in `ModelInvocationHoister` recognises both calls as the same fingerprint and shares one `ModelInvocationOperator`. The projection reads `__model_phi3_0` twice.

### Subsumed subtrees

```sql
SELECT
  upper(name) AS u,
  length(upper(name)) AS n,
  reverse(length(upper(name))) AS r
FROM t
```

Three eligible fingerprints — `upper(name)`, `length(upper(name))`, `reverse(length(upper(name)))` — all appearing more than once across columns and the projection rewrite. The pass stacks three `RowEnricherOperator`s, each level building on the previous.

### LET unification

```sql
SELECT
  LET u = upper(name),
  upper(name) AS shouted
FROM t
```

The duplicate `upper(name)` rewrites to a reference to `u`. No `__cse_*` column is allocated.

## See Also

- [Operators](operators.md) — runtime contract for `RowEnricherOperator` and `ModelInvocationOperator`.
- [Execution Plans](execution-plans.md) — how these operators render in EXPLAIN.
- [LET Bindings](../sql/let-bindings.md) — the user-visible memoisation primitive that CSE complements.
- [Planner-Time Inline-Metadata Accessor Elision](planner-time-elision.md) — sibling pass that runs immediately before CSE; rewrites cheap inline-metadata reads (`image_width`, `length`, …) so their elided form deduplicates here.
