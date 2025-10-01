---
title: TABLESAMPLE
---

TABLESAMPLE limits the rows returned from a table source to an approximate sample. It is part of the table reference — placed after the table name and before any alias.

## Syntax

```sql
FROM table TABLESAMPLE method(argument) [ON column] [REPEATABLE(seed)] [AS alias]
```

### Sampling Methods

| Method | Argument | ON clause | Description |
|--------|----------|-----------|-------------|
| `BERNOULLI(pct)` | Percentage (0–100) | No | Row-level probabilistic sampling. Each row is independently included with the given probability. |
| `SYSTEM(pct)` | Percentage (0–100) | No | Chunk-level sampling. Entire chunks/pages are included or excluded. Currently falls back to row-level Bernoulli. |
| `STRATIFIED(pct)` | Percentage (0–100) | Required | Per-class proportional sampling. Each class (defined by the ON column) is sampled at the same rate, preserving class proportions. |
| `BALANCED(count)` | Rows per class | Required | Per-class fixed-count sampling. Returns exactly `count` rows from each class (or fewer if a class has fewer rows) via reservoir sampling. |

### Examples

```sql
-- Row-level: ~10% of rows
SELECT * FROM data TABLESAMPLE BERNOULLI(10)

-- Deterministic sampling with a seed
SELECT * FROM data TABLESAMPLE BERNOULLI(10) REPEATABLE(42)

-- Stratified: 10% of each class, preserving class distribution
SELECT * FROM training_data TABLESAMPLE STRATIFIED(10) ON label

-- Balanced: exactly 1000 rows per class, equalizing class distribution
SELECT * FROM training_data TABLESAMPLE BALANCED(1000) ON label

-- Composite stratification key
SELECT * FROM training_data TABLESAMPLE BALANCED(500) ON (label, split)

-- With alias and other clauses
SELECT * FROM training_data TABLESAMPLE STRATIFIED(10) ON label REPEATABLE(42) AS s
WHERE s.is_valid = 1
ORDER BY s.label, s.id
```

### REPEATABLE

`REPEATABLE(seed)` makes sampling deterministic — the same seed on the same data always returns the same sample. The seed is an integer. Without REPEATABLE, sampling uses a shared random source and results vary between runs.

### ON clause (stratification key)

The `ON` clause specifies the column(s) that define class membership for STRATIFIED and BALANCED sampling. A single column or a parenthesized list of columns is accepted:

```sql
-- Single column
TABLESAMPLE STRATIFIED(10) ON label

-- Composite key
TABLESAMPLE BALANCED(500) ON (label, split)
```

The ON clause is required for STRATIFIED and BALANCED, and forbidden for BERNOULLI and SYSTEM.

### STRATIFIED semantics

STRATIFIED applies a uniform Bernoulli filter at the given rate across all rows. Because every class is sampled at the same percentage, class proportions are preserved in expectation. The output row count is approximate — it varies due to random sampling.

```sql
-- If training_data has 5000 cats and 1000 dogs:
-- STRATIFIED(10) returns ~500 cats and ~100 dogs (5:1 ratio preserved)
SELECT * FROM training_data TABLESAMPLE STRATIFIED(10) ON label REPEATABLE(42)
```

### BALANCED semantics

BALANCED uses per-class reservoir sampling (Algorithm R) to return exactly `count` rows from each distinct class. All input rows are consumed in a single pass; each class maintains an independent reservoir. Classes with fewer rows than the target count return all available rows (no oversampling).

```sql
-- If training_data has 5000 cats and 1000 dogs:
-- BALANCED(500) returns exactly 500 cats and 500 dogs
SELECT * FROM training_data TABLESAMPLE BALANCED(500) ON label REPEATABLE(42)

-- Small class: if birds has only 50 rows, returns all 50
SELECT * FROM training_data TABLESAMPLE BALANCED(500) ON label
```

Output rows are emitted in class-first order (all rows from the first-seen class, then the second, etc.). Add `ORDER BY random()` for shuffled output.

### Memory model

BERNOULLI, SYSTEM, and STRATIFIED are streaming operators with no buffering (0 additional memory).

BALANCED buffers up to `count × C` rows in memory, where `C` is the number of distinct classes. A configurable maximum class count (default 10,000, adjustable via `QueryGovernor.MaxStratifyClasses`) prevents unbounded memory growth on high-cardinality columns. Exceeding the limit produces a clear error.

### Clause interaction

TABLESAMPLE is part of the table reference and applies at the scan level. Pushed-down WHERE predicates filter rows **before** sampling — the sample is drawn from the filtered population:

```sql
-- WHERE filters first, then STRATIFIED samples from the filtered rows
SELECT * FROM data TABLESAMPLE STRATIFIED(10) ON label
WHERE is_valid = 1
```

Pipeline position:
```
FROM → (pushed-down WHERE) → TABLESAMPLE → JOIN → WHERE → GROUP BY → ...
```

## Execution Model

BERNOULLI, SYSTEM, and STRATIFIED are streaming passes (0 query units, 0 buffering). BALANCED is a blocking operator — all input rows must be consumed before output is emitted (reservoir sampling requires a full pass). BALANCED adds 0 query units.

## See Also

- [FROM](from.md)
- [WHERE](filtering.md)
- [GROUP BY](group-by.md)
