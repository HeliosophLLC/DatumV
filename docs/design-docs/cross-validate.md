# CROSS VALIDATE — Design Plan

> **Status**: Proposed (complexity **S**). No prerequisites — all infrastructure exists (`hash_split`, expression evaluation, projection pipeline).

---

## Motivation

Cross-validation is a fundamental ML evaluation technique: split a dataset into k non-overlapping folds, train on k-1, evaluate on the held-out fold, rotate. Every ML practitioner does this, and every time they do it in SQL they write the same boilerplate:

```sql
-- Today: manual fold assignment
SELECT *,
  CAST(FLOOR(hash_split(id, 42) * 5) AS Int32) AS fold
FROM training_data

-- Then k separate queries to materialize each split:
SELECT * FROM training_data WHERE fold != 0 INTO 'fold_0_train.parquet';
SELECT * FROM training_data WHERE fold  = 0 INTO 'fold_0_val.parquet';
SELECT * FROM training_data WHERE fold != 1 INTO 'fold_1_train.parquet';
-- ... 8 more statements
```

The `hash_split` + `FLOOR` + `CAST` pattern is mechanical and error-prone. The k-value must be duplicated in every reference. The fold column has no semantic meaning — it is a number someone has to remember maps to "k-fold cross-validation with seed 42." And the boilerplate scales linearly with k.

DuckDB has no cross-validation primitive. BigQuery has no cross-validation primitive. scikit-learn has `KFold`, `StratifiedKFold`, `GroupKFold` — but those operate outside SQL, require materializing the full dataset in Python memory, and break the single-pipeline property that makes DatumIngest queries reproducible and auditable.

CROSS VALIDATE makes fold assignment a first-class, declarative SQL construct.

---

## Syntax

### Basic form

```sql
SELECT *, fold
FROM training_data
CROSS VALIDATE(k = 5) ON id AS fold
```

Appends a column named `fold` containing integer values in `[0, k)`. The assignment is deterministic: the same `id` always maps to the same fold. Default seed is 0.

### With explicit seed

```sql
SELECT *, fold
FROM training_data
CROSS VALIDATE(k = 5, seed = 42) ON id AS fold
```

Different seeds produce different fold assignments for the same key values. This enables multiple independent cross-validation runs.

### Stratified cross-validation

```sql
SELECT *, fold
FROM training_data
CROSS VALIDATE(k = 5, seed = 42) ON id STRATIFY BY label AS fold
```

Ensures each fold has approximately the same class distribution as the full dataset. Each class is independently assigned to folds, so minority classes are evenly distributed rather than concentrated in one or two folds.

### Group cross-validation

```sql
SELECT *, fold
FROM training_data
CROSS VALIDATE(k = 5) ON patient_id GROUP BY patient_id AS fold
```

All rows sharing the same `GROUP BY` key are assigned to the same fold. This prevents data leakage in scenarios where multiple rows belong to the same entity (e.g., multiple visits per patient, multiple images per subject). The fold is assigned based on the group key, not individual row keys.

When `GROUP BY` is specified, the `ON` key and `GROUP BY` key are the same concept — the `ON` clause specifies the hash input and the `GROUP BY` clause specifies the grouping constraint. In practice, `ON patient_id GROUP BY patient_id` is the common form. The parser accepts this redundancy for readability; if `ON` is omitted, it defaults to the `GROUP BY` expression.

### Composite keys

```sql
SELECT *, fold
FROM training_data
CROSS VALIDATE(k = 10, seed = 7) ON (user_id, session_id) AS fold
```

Multiple columns form the hash key. The composite key is hashed as the concatenation of individual `hash_split` inputs.

### Combined with other clauses

```sql
-- With WHERE (filter before fold assignment)
SELECT *, fold
FROM training_data
WHERE is_valid = 1
CROSS VALIDATE(k = 5) ON id AS fold

-- With ORDER BY
SELECT *, fold
FROM training_data
CROSS VALIDATE(k = 5) ON id AS fold
ORDER BY fold, id

-- With INTO (materialize fold-tagged dataset)
SELECT *, fold
FROM training_data
CROSS VALIDATE(k = 5, seed = 42) ON id AS fold
INTO 'training_with_folds.parquet'

-- With LET (use fold in downstream computation)
SELECT
  LET f = fold,
  *,
  CASE WHEN f = 0 THEN 'val' ELSE 'train' END AS split_role
FROM training_data
CROSS VALIDATE(k = 5) ON id AS fold
```

---

## Semantics

### Fold assignment algorithm

The fold index for a row is computed as:

```
fold = FLOOR(hash_split(key, seed) * k)
```

Where:
- `hash_split(key, seed)` produces a deterministic float in `[0, 1)` via XxHash64 (already implemented in `HashSplitFunction`)
- `k` is the fold count
- `FLOOR(... * k)` maps `[0, 1)` uniformly to `{0, 1, ..., k-1}`

This is the same algorithm practitioners write by hand. CROSS VALIDATE makes it declarative and gives the planner visibility into the intent.

### Determinism guarantees

- Same key + same seed + same k → same fold, always, across runs and machines
- Independent of row order, parallelism, or execution plan
- Independent of other rows in the dataset (no reservoir sampling, no counting)

### Fold balance

For large datasets (N >> k), each fold will contain approximately N/k rows. The uniformity guarantee comes from XxHash64's distribution properties — the hash function maps inputs to [0, 1) with negligible bias for practical key populations.

For small datasets (N ≈ k or N < k), some folds may be empty. This is an inherent property of hash-based assignment and matches the behavior of scikit-learn's `KFold(shuffle=True)` on small datasets.

### Stratified fold balance

When `STRATIFY BY` is specified, the fold assignment is computed **per class**: rows are grouped by the stratification column, and within each group the fold assignment proceeds independently using the same `hash_split(key, seed)` mechanism. This ensures each fold receives approximately the same proportion of each class.

Implementation: the fold formula remains identical — `FLOOR(hash_split(key, seed) * k)`. Stratification does not change the per-row computation; it is a **validation constraint** on the result. The hash-based assignment already produces approximately proportional folds when the stratification column is independent of the hash key, which is the typical case (e.g., `ON id STRATIFY BY label` where `id` is a surrogate key uncorrelated with `label`).

When the stratification column is correlated with the hash key (pathological case), the planner emits a diagnostic warning. A future enhancement could use per-class reservoir reassignment to guarantee exact stratification, but the hash-based approach is sufficient for the initial implementation.

### Group fold integrity

When `GROUP BY` is specified, all rows with the same group key receive the same fold. The fold is computed from the group key, not from individual row identifiers:

```
fold = FLOOR(hash_split(group_key, seed) * k)
```

This prevents leakage: if patient P has 10 visits, all 10 are in the same fold. The group key replaces the `ON` key for hashing purposes.

### Output column

- Name: the identifier after `AS` (required)
- Type: `DataKind.Int32`
- Value range: `[0, k-1]`
- Nullability: never null (the hash function handles null keys by hashing a sentinel)

---

## Clause Position and Interactions

CROSS VALIDATE is syntactically a post-FROM clause modifier that appends a computed column. It executes **after** WHERE/JOIN filtering and **before** projection.

| Clause | Interaction |
|---|---|
| FROM | CROSS VALIDATE operates on the rows produced by FROM |
| WHERE | Applied before fold assignment — only surviving rows receive folds |
| JOIN | Joins resolve before fold assignment |
| GROUP BY | If the query has GROUP BY (aggregation), CROSS VALIDATE applies to the *aggregated* rows, not the pre-aggregation rows. In practice CROSS VALIDATE on aggregated data is unusual — it is most natural on unaggregated row-level data |
| HAVING | Applied before fold assignment (same as WHERE for aggregated queries) |
| QUALIFY | Applied after window functions, before fold assignment |
| SELECT | The fold column is available for projection |
| LET | LET bindings can reference the fold column by its alias |
| ORDER BY | Can reference the fold column |
| LIMIT | Applied after fold assignment |
| INTO / SHARD ON | Fold-tagged rows flow into output writers normally |

---

## Implementation Plan

### Phase 1: Parser + AST

1. **Add `CROSS VALIDATE` as a compound keyword** in the token stream. The tokenizer already handles multi-word keywords (`GROUP BY`, `ORDER BY`, etc.). Register `CROSS` + `VALIDATE` as a compound token, or parse `CROSS` contextually followed by `VALIDATE` as an identifier (same pattern as `TABLESAMPLE` methods). The simpler path: parse `CROSS` as a keyword (already exists for `CROSS JOIN`) and `VALIDATE` as a contextual identifier.

2. **New AST node** in `src/DatumIngest.Parsing/Ast/AstNodes.cs`:

   ```csharp
   /// <summary>
   /// A CROSS VALIDATE clause that assigns deterministic fold indices to rows.
   /// </summary>
   public sealed record CrossValidateClause(
       Expression FoldCount,
       Expression? Seed,
       IReadOnlyList<Expression> KeyColumns,
       IReadOnlyList<Expression>? StratifyColumns,
       IReadOnlyList<Expression>? GroupColumns,
       string OutputAlias,
       SourceSpan? Span = null);
   ```

3. **Parser production** in `src/DatumIngest.Parsing/SqlParser.cs`. Add a `CrossValidateClauseParser` that matches:

   ```
   'CROSS' 'VALIDATE' '(' NamedArgList ')' 'ON' KeyExprList
     ['STRATIFY' 'BY' ExprList]
     ['GROUP' 'BY' ExprList]
     'AS' Identifier
   ```

   Named argument parsing for `k` and `seed`:
   - `k = <integer>` — required, validated > 1 at parse time
   - `seed = <integer>` — optional, defaults to 0

   The `STRATIFY` keyword is not currently a token. Parse it as a contextual identifier (same as `BERNOULLI`, `BALANCED`, `STRATIFIED`). `BY` already exists.

4. **Attach to `SelectExpression`**. Add an optional `CrossValidateClause?` field to `SelectExpression` (or to the `FromClause` family). The natural position is on `SelectExpression` since CROSS VALIDATE is a query-level modifier, not a table-level modifier.

5. **Parser tests** in a new `tests/DatumIngest.Tests/Parsing/CrossValidateParsingTests.cs`:
   - `BasicCrossValidate_ParsesKAndAlias`
   - `WithSeed_ParsesSeedParameter`
   - `CompositeKey_ParsesMultipleOnColumns`
   - `StratifyBy_ParsesStratificationColumn`
   - `GroupBy_ParsesGroupColumn`
   - `MissingK_Fails`
   - `KEqualsOne_Fails`
   - `MissingAs_Fails`
   - `MissingOn_Fails`

### Phase 2: Planner Integration

6. **Detect CROSS VALIDATE** in `QueryPlanner.cs` during SELECT planning. When present:

   - Evaluate `k` and `seed` as compile-time constants via `EvaluateConstantDouble()`
   - Validate `k >= 2` and `k <= 1000` (sane upper bound)
   - Resolve key column expressions against the source schema
   - **Desugar** to a synthetic LET binding:

   For a single key:
   ```
   LET <alias> = CAST(FLOOR(hash_split(<key>, <seed>) * <k>) AS Int32) AS <alias>
   ```

   For a composite key:
   ```
   LET <alias> = CAST(FLOOR(hash_split(
     concat_ws('|', CAST(<key1> AS String), CAST(<key2> AS String), ...),
     <seed>) * <k>) AS Int32) AS <alias>
   ```

   For GROUP BY:
   ```
   LET <alias> = CAST(FLOOR(hash_split(<group_key>, <seed>) * <k>) AS Int32) AS <alias>
   ```

   The desugared LET binding is prepended to the existing LET bindings list, making the fold column available to all subsequent LET bindings and output columns. The memoization guarantee of LET ensures the fold is computed once per row.

7. **STRATIFY BY handling**: When `STRATIFY BY` is present, the planner does *not* change the fold computation. Instead, it emits a planner note (visible in EXPLAIN output): `"Stratified cross-validation: fold balance per class depends on hash uniformity. For small datasets or highly imbalanced classes, verify fold distribution with: SELECT label, fold, COUNT(*) ... GROUP BY label, fold"`. A future enhancement can add exact stratification via per-class round-robin, but the hash-based approach is correct for the V1.

8. **GROUP BY handling**: When `GROUP BY` is present, the key expression in the hash is replaced with the group key expression. The planner validates that `ON` and `GROUP BY` reference the same columns (or that `ON` is omitted, in which case it defaults to the `GROUP BY` expression).

### Phase 3: Execution

9. **No new operator required.** CROSS VALIDATE desugars entirely to an expression-level computation (a synthetic LET binding). The existing `ProjectOperator` with LET memoization handles it. This is the key insight that makes the feature complexity **S** — no new operator, no new execution path, no buffering, no multi-pass.

   The execution flow is:
   ```
   Source rows → [existing operators: Filter, Join, etc.]
                → ProjectOperator with augmented LET bindings
                   (includes synthetic fold LET binding)
                → Output rows with fold column
   ```

10. **Execution tests** in `tests/DatumIngest.Tests/Execution/CrossValidateTests.cs`:
    - `BasicFoldAssignment_ProducesKDistinctFolds`
    - `DeterministicWithSeed_SameKeysSameFolds`
    - `DifferentSeeds_DifferentAssignments`
    - `FoldDistribution_ApproximatelyUniform` — for N=10000, k=5, each fold should have ~2000 ± 200 rows
    - `CompositeKey_DifferentCombinationsGetDifferentFolds`
    - `NullKey_AssignedToAFold` — null keys hash to a deterministic fold (not null output)
    - `WithWhere_FoldsAssignedAfterFilter`
    - `FoldRange_AlwaysZeroToKMinusOne`
    - `FoldColumn_IsInt32`
    - `WithOrderByFold_SortsByFoldCorrectly`
    - `GroupBy_AllRowsInGroupGetSameFold`
    - `StratifyBy_EachClassRepresentedInEachFold` — for 3 classes, k=3, verify no fold is missing a class (statistical test with tolerance)

### Phase 4: EXPLAIN Support

11. **EXPLAIN output**: Since CROSS VALIDATE desugars to a LET binding, EXPLAIN shows it as part of the projection:

    ```
    Project [columns..., fold = CAST(FLOOR(hash_split(id, 42) * 5) AS Int32)]
      └─ Scan [training_data]
    ```

    Optionally, add a descriptive annotation in the EXPLAIN output to make the intent clear:

    ```
    Project [columns..., fold] (CROSS VALIDATE k=5 seed=42 on=id)
      └─ Scan [training_data]
    ```

    This requires the planner to preserve the `CrossValidateClause` metadata on the plan node for EXPLAIN rendering, even though execution uses the desugared LET form.

### Phase 5: Language Server

12. **Autocomplete**: After `FROM table_name`, suggest `CROSS VALIDATE`. After `CROSS`, suggest `VALIDATE` (and `JOIN`). Inside the parentheses, suggest `k =` and `seed =`. After `)`, suggest `ON`. After key columns, suggest `STRATIFY BY`, `GROUP BY`, `AS`.

13. **Hover**: On the `CROSS VALIDATE` keyword, show documentation:
    > Assigns each row a deterministic fold index in [0, k) for k-fold cross-validation. Fold assignment is based on a hash of the key column(s) with an optional seed.

14. **Diagnostics**:
    - `k` must be an integer literal ≥ 2
    - `seed` must be a numeric literal (if present)
    - `ON` column(s) must exist in the source schema
    - `STRATIFY BY` column(s) must exist in the source schema
    - `GROUP BY` column(s) must exist in the source schema
    - Warning if `k` > 20 (unusual fold count — possible user error)

### Phase 6: Documentation

15. **Update `docs/sql.md`**: Add a CROSS VALIDATE section between TABLESAMPLE and QUALIFY (or after ORDER BY), with syntax, semantics, examples, and clause interaction table.

16. **Update `docs/functions.md`**: Cross-reference `hash_split` documentation to mention CROSS VALIDATE as the declarative alternative.

---

## Use Cases

### 1. Standard k-Fold Cross-Validation

The most common pattern: assign 5 folds, iterate over each as the hold-out set.

```sql
SELECT *, fold
FROM training_data
CROSS VALIDATE(k = 5, seed = 42) ON id AS fold
INTO 'training_with_folds.parquet'
```

Downstream consumers (Python, notebooks) filter by fold index:

```python
train = df[df['fold'] != 0]
val   = df[df['fold'] == 0]
# ... train model, evaluate, rotate fold
```

### 2. Stratified k-Fold for Imbalanced Classification

A fraud detection dataset with 2% positive rate. Without stratification, some folds may have 0% or 4% positives — destroying evaluation validity.

```sql
SELECT *, fold
FROM transactions
CROSS VALIDATE(k = 5, seed = 42) ON transaction_id STRATIFY BY is_fraud AS fold
```

Each fold has approximately 2% positives, matching the population rate.

### 3. Group k-Fold for Medical Data

A clinical dataset where each patient has multiple visits. Training on some visits and evaluating on other visits of the *same* patient leaks temporal patient-specific patterns into the evaluation set.

```sql
SELECT *, fold
FROM clinical_visits
CROSS VALIDATE(k = 5) ON patient_id GROUP BY patient_id AS fold
```

All visits for patient P are in the same fold. No cross-contamination.

### 4. Nested Cross-Validation

Outer CV for model evaluation, inner CV for hyperparameter tuning:

```sql
SELECT *,
  outer_fold,
  inner_fold
FROM training_data
CROSS VALIDATE(k = 5, seed = 1) ON id AS outer_fold
CROSS VALIDATE(k = 3, seed = 2) ON id AS inner_fold
```

The two fold assignments are independent (different seeds). For each outer fold's training set, the inner folds partition the training data further.

### 5. Reproducible Experiment Comparison

Two researchers using the same dataset and seed get identical folds, enabling apples-to-apples comparison:

```sql
-- Researcher A (any machine, any time)
SELECT * FROM data CROSS VALIDATE(k = 10, seed = 2026) ON id AS fold

-- Researcher B (different machine, same query)
SELECT * FROM data CROSS VALIDATE(k = 10, seed = 2026) ON id AS fold
-- Identical fold assignments ✓
```

### 6. Leave-One-Out Cross-Validation (LOO-CV)

For small datasets (N < 100), set k = N:

```sql
SELECT *, fold
FROM small_dataset
CROSS VALIDATE(k = 50, seed = 0) ON id AS fold
-- Each fold has ~1 row (with 50 samples)
```

The k ≤ 1000 upper bound on the planner is deliberately generous to accommodate LOO-CV.

---

## Comparison with Alternatives

| Approach | Deterministic | Single-pass | Declarative | Stratified | Group-aware | No Python |
|---|---|---|---|---|---|---|
| **CROSS VALIDATE** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Manual `hash_split` + `FLOOR` | ✅ | ✅ | ❌ Boilerplate | ❌ Manual | ❌ Manual | ✅ |
| scikit-learn `KFold` | ⚠️ Seed-dependent | ❌ Materializes | ❌ Code | ✅ `StratifiedKFold` | ✅ `GroupKFold` | ❌ |
| Random row numbering | ❌ Non-deterministic | ✅ | ❌ | ❌ | ❌ | ✅ |
| Recursive CTE modular assignment | ✅ | ❌ O(N) iterations | ❌ | ❌ | ❌ | ✅ |

---

## Synergies

- **`hash_split`** — CROSS VALIDATE is built entirely on `hash_split`. The relationship is: `hash_split` is the primitive, CROSS VALIDATE is the ergonomic abstraction. Users who need custom splitting logic (e.g., time-based splits, per-class oversampling) continue using `hash_split` directly.
- **SPLIT INTO** (proposed) — compose to materialize each fold's train/val split in one pipeline: `CROSS VALIDATE ... AS fold` followed by `SPLIT INTO ('fold_0_train.parquet' WHERE fold != 0, 'fold_0_val.parquet' WHERE fold = 0, ...)`.
- **STRATIFY / TABLESAMPLE BALANCED** (proposed) — stratified cross-validation shares the per-class distribution concept with `TABLESAMPLE BALANCED`. Both use the stratification column to ensure representative sampling.
- **LET** — the fold column is a LET binding, so downstream LET bindings can reference it: `LET is_train = fold != 0, LET is_val = fold = 0`.
- **INTO** — write the fold-tagged dataset once, consume it in k training runs.
- **`DESCRIBE`** (proposed) — `DESCRIBE SELECT *, fold FROM data CROSS VALIDATE(...) ON id AS fold` shows fold distribution statistics, validating balance before training.

---

## Design Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Implementation strategy | Planner-level desugaring to LET binding | Zero new operators, zero new execution paths. Reuses existing memoization, projection, and expression evaluation infrastructure. Keeps complexity at **S**. |
| Hash function | XxHash64 via existing `hash_split` | Already proven, deterministic, uniform, fast. No reason to introduce a second hash. |
| Fold type | `DataKind.Int32` | Integer fold indices are natural for `WHERE fold = i` filters. Float would invite rounding bugs. |
| Default seed | 0 | Matches `hash_split`'s convention. Explicit seed encouraged but not required. |
| Null key handling | Hash a sentinel (existing `hash_split` behavior) | Null keys produce a deterministic fold rather than a null fold. Avoids silent data loss if users forget a NOT NULL filter. |
| Stratification V1 | Hash-based (no per-class reassignment) | Sufficient for typical imbalance ratios and dataset sizes. Exact stratification is a future enhancement if needed. |
| k upper bound | 1000 | Accommodates LOO-CV on small datasets while preventing accidental `CROSS VALIDATE(k = 1000000)` on large datasets. |
| Clause position | After QUALIFY, before SELECT projection | Fold assignment is a row-level computation that should see final filtered rows but be available for projection, LET, and ORDER BY. |
| Composite key hashing | `concat_ws('|', CAST(k1 AS String), ...)` | Reuses existing string concatenation. Delimiter prevents collisions (e.g., keys `('a', 'bc')` vs `('ab', 'c')`). |
| Nested CROSS VALIDATE | Allowed (multiple clauses) | Independent fold assignments with different seeds. Each desugars to a separate LET binding. No interaction between them. |

---

## Relevant Files

| File | Change | Notes |
|------|--------|-------|
| `src/DatumIngest.Parsing/Ast/AstNodes.cs` | Modify | New `CrossValidateClause` record |
| `src/DatumIngest.Parsing/SqlParser.cs` | Modify | `CrossValidateClauseParser`, attach to `SelectExpression` |
| `src/DatumIngest/Execution/QueryPlanner.cs` | Modify | Desugar CROSS VALIDATE to synthetic LET binding |
| `src/DatumIngest/Functions/Math/RandomFunctions.cs` | None | `HashSplitFunction` consumed as-is |
| `src/DatumIngest/Execution/Operators/ProjectOperator.cs` | None | Existing LET memoization handles fold computation |
| `src/DatumIngest.LanguageServer/SemanticAnalyzer.cs` | Modify | Diagnostics for k, seed, column references |
| `src/DatumIngest.LanguageServer/CompletionProvider.cs` | Modify | Autocomplete for CROSS VALIDATE syntax |
| `tests/DatumIngest.Tests/Parsing/CrossValidateParsingTests.cs` | **New** | Parser tests |
| `tests/DatumIngest.Tests/Execution/CrossValidateTests.cs` | **New** | Execution and integration tests |
| `docs/sql.md` | Modify | CROSS VALIDATE section |

---

## Verification

1. **Parser**: All 9 parsing tests pass — basic, seed, composite key, stratify, group, error cases
2. **Fold correctness**: 10,000 rows with k=5 produce folds in `[0, 4]` with each fold containing ~2000 ± 200 rows
3. **Determinism**: Same query twice produces identical fold assignments
4. **Different seeds**: Changing seed produces different (but equally valid) fold assignments
5. **Composite keys**: `(user_id, session_id)` pairs produce different folds than `user_id` alone
6. **Null handling**: Null keys receive a fold (not null output)
7. **Group integrity**: All rows with same group key have same fold
8. **Stratification**: For 3 classes with k=3, each fold contains all 3 classes (statistical test)
9. **Nested**: Two CROSS VALIDATE clauses produce two independent fold columns
10. **EXPLAIN**: Shows fold computation with CROSS VALIDATE annotation
11. **No regressions**: `dotnet test` passes with zero failures
12. **Language server**: Autocomplete, hover, diagnostics function correctly (manual verification)
