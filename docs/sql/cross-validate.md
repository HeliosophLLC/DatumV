---
title: CROSS VALIDATE
---

## Why Use This

You've built a model and want to know if it actually works — not just on the data it was trained on, but on data it hasn't seen. Cross-validation is the standard answer: split your data into k groups (called "folds"), train on k-1 of them, test on the one you held out, then rotate. Do this k times and you get a reliable estimate of how your model will perform on new data.

The problem is that setting this up in SQL is tedious. You end up writing `hash_split` + `FLOOR` + `CAST` boilerplate, duplicating the fold count in every query, and managing k separate train/test materializations by hand. CROSS VALIDATE replaces all of that with one line.

## How It Works

CROSS VALIDATE hashes each row's key column into a fold number between 0 and k-1. The assignment is deterministic — same data, same seed, same folds, every time, on any machine. Because it's based on hashing (not random sampling), it doesn't need to see all the data first. It's a streaming operation: each row gets its fold immediately, with zero buffering.

Under the hood, the clause desugars to a LET binding that calls `hash_split` — the same function you'd use manually, but without the boilerplate.

## Common Patterns

### Quick train/test split

The simplest use: tag every row with a fold, export once, and let your training script filter by fold.

```sql
-- Tag each row with one of 5 folds, export to Parquet
SELECT *, fold
FROM training_data
CROSS VALIDATE(k = 5, seed = 42) ON id AS fold
INTO 'training_with_folds.parquet'
```

In Python, your training loop becomes:

```python
for held_out in range(5):
    train = df[df['fold'] != held_out]
    val   = df[df['fold'] == held_out]
    model.fit(train)
    scores.append(model.evaluate(val))
```

### Handling imbalanced classes

If 98% of your transactions are legitimate and 2% are fraud, a naive split might leave some folds with 0% fraud — making evaluation meaningless. STRATIFY BY ensures each fold mirrors the overall class distribution.

```sql
SELECT *, fold
FROM transactions
CROSS VALIDATE(k = 5, seed = 42) ON transaction_id STRATIFY BY is_fraud AS fold
-- Each fold has approximately 2% fraud, matching the population rate
```

### Preventing data leakage

If a patient has 10 hospital visits and some end up in training while others end up in validation, the model can "cheat" by recognizing the patient rather than learning the disease pattern. GROUP BY keeps all of an entity's rows in the same fold.

```sql
SELECT *, fold
FROM clinical_visits
CROSS VALIDATE(k = 5) ON patient_id GROUP BY patient_id AS fold
-- All visits for patient P are in the same fold — no leakage
```

## Syntax

```sql
SELECT *, fold
FROM table
CROSS VALIDATE(k = N [, seed = S]) ON key_column [STRATIFY BY column] [GROUP BY column] AS alias
```

### Parameters

| Parameter | Required | Default | Description |
|-----------|----------|---------|-------------|
| `k` | Yes | — | Number of folds. Must be an integer >= 2. |
| `seed` | No | 0 | Deterministic seed. Different seeds produce different fold assignments. |

### Clauses

| Clause | Description |
|--------|-------------|
| `ON key` | Column(s) used as the hash key. Composite keys: `ON (col1, col2)`. |
| `STRATIFY BY col` | Ensures each fold has approximately the same class distribution. |
| `GROUP BY col` | All rows with the same group key get the same fold (prevents data leakage). |
| `AS alias` | Required. The output column name for the fold index. |

## Examples

### Basic k-fold

```sql
-- 5-fold cross-validation on an ID column
SELECT *, fold
FROM training_data
CROSS VALIDATE(k = 5, seed = 42) ON id AS fold
```

### Verify fold distribution

```sql
SELECT fold, COUNT(*) AS cnt
FROM training_data
CROSS VALIDATE(k = 5, seed = 42) ON id AS fold
GROUP BY fold
ORDER BY fold
```

### Composite key

```sql
SELECT *, fold
FROM training_data
CROSS VALIDATE(k = 10, seed = 7) ON (user_id, session_id) AS fold
```

### Stratified cross-validation

Ensures each fold has approximately the same class distribution. Critical for imbalanced datasets (e.g., fraud detection with 2% positive rate):

```sql
SELECT *, fold
FROM transactions
CROSS VALIDATE(k = 5, seed = 42) ON transaction_id STRATIFY BY is_fraud AS fold
```

### Group cross-validation

Prevents data leakage by keeping all rows from the same entity in the same fold (e.g., multiple visits per patient):

```sql
SELECT *, fold
FROM clinical_visits
CROSS VALIDATE(k = 5) ON patient_id GROUP BY patient_id AS fold
```

### Nested cross-validation

Outer CV for model evaluation, inner CV for hyperparameter tuning:

```sql
SELECT *, outer_fold, inner_fold
FROM training_data
CROSS VALIDATE(k = 5, seed = 1) ON id AS outer_fold
CROSS VALIDATE(k = 3, seed = 2) ON id AS inner_fold
```

### Combined with other clauses

```sql
-- Filter, assign folds, sort, and output
SELECT *, fold
FROM training_data
WHERE is_valid = 1
CROSS VALIDATE(k = 5, seed = 42) ON id AS fold
ORDER BY fold, id
INTO 'training_with_folds.parquet'

-- Use fold in downstream LET computation
SELECT
  LET f = fold,
  *,
  CASE WHEN f = 0 THEN 'val' ELSE 'train' END AS split_role
FROM training_data
CROSS VALIDATE(k = 5) ON id AS fold
```

### Composition with TABLESAMPLE BALANCED

Balance classes first, then assign folds:

```sql
-- 500 per class, then 5-fold split
SELECT fold, label, COUNT(*) AS cnt
FROM training_data
TABLESAMPLE BALANCED(500) ON label REPEATABLE(42)
CROSS VALIDATE(k = 5, seed = 7) ON id AS fold
GROUP BY fold, label
ORDER BY fold, label
```

## Determinism

- Same key + same seed + same k = same fold, always, across runs and machines.
- Independent of row order, parallelism, or execution plan.
- Independent of other rows in the dataset (no reservoir sampling, no counting).
- Default seed is 0 when omitted.

## Output column

- **Type:** `Int32`
- **Range:** `[0, k-1]`
- **Nullability:** Never null. Null keys hash to a deterministic fold via a sentinel.

## Clause interaction

CROSS VALIDATE applies after WHERE filtering and before GROUP BY. The fold column is available to GROUP BY, SELECT, LET, ORDER BY, and INTO.

```
FROM -> JOIN -> WHERE -> CROSS VALIDATE -> GROUP BY -> HAVING -> Window -> SCAN
  -> QUALIFY -> SELECT (LET) -> ASSERT -> DISTINCT -> ORDER BY -> LIMIT
```

| Clause | Interaction |
|--------|-------------|
| WHERE | Applied before fold assignment — only surviving rows receive folds |
| JOIN | Joins resolve before fold assignment |
| GROUP BY | Folds are assigned before GROUP BY — you can `GROUP BY fold` directly |
| LET | LET bindings can reference the fold column by its alias |
| ORDER BY | Can sort by the fold column |
| INTO | Fold-tagged rows flow into output writers normally |

## Execution model

CROSS VALIDATE is a streaming operation (0 query units, 0 buffering). It desugars at plan time to a synthetic LET binding:

```
CAST(FLOOR(hash_split(key, seed) * k) AS Int32)
```

For composite keys, the key is `concat_ws('|', CAST(k1 AS String), CAST(k2 AS String), ...)`. The `hash_split` function uses XxHash64 for uniform distribution.

## Choosing the Right Variant

| Situation | Use | Why |
|-----------|-----|-----|
| Standard evaluation | `CROSS VALIDATE(k = 5) ON id` | Simple, fast, good default |
| Imbalanced classes (fraud, rare diseases) | Add `STRATIFY BY label` | Prevents folds with no minority-class rows |
| Grouped data (multiple rows per entity) | Add `GROUP BY entity_id` | Prevents data leakage between train/test |
| Hyperparameter tuning | Nested: two CROSS VALIDATE clauses with different seeds | Outer folds for evaluation, inner folds for tuning |
| Reproducible experiments | Always specify `seed = N` | Two people get identical folds from identical data |

## Gotchas

- **k must be at least 2.** `CROSS VALIDATE(k = 1)` is a parse error — one fold means no held-out set.
- **Small datasets may have empty folds.** With 8 rows and k=5, some folds might get 1 row and others 2. This is inherent to hash-based assignment — the distribution is approximate, not exact.
- **Changing the seed changes everything.** The fold assignments for `seed = 42` and `seed = 43` are completely independent. This is by design (for running multiple experiments), but it means you can't "adjust" one fold without changing all of them.
- **STRATIFY BY doesn't guarantee exact proportions.** It relies on hash uniformity, which works well for large datasets but may show more variance on small ones. Verify with `SELECT label, fold, COUNT(*) ... GROUP BY label, fold`.
- **The fold column is computed, not stored.** It's a LET binding under the hood, so it only exists in the query that declares it. If you need folds persisted, use `INTO` to write the results.

## See Also

- [TABLESAMPLE](tablesample.md) — class-balanced sampling (STRATIFIED, BALANCED)
- [LET Bindings](let-bindings.md) — CROSS VALIDATE desugars to a LET binding
- [hash_split](../functions/random.md) — the underlying hash function
