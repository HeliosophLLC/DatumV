# EXPLODE Clause — Design Plan

> **Status**: Proposed (complexity **S–M**). No hard prerequisites — desugars to existing `CROSS JOIN LATERAL UNNEST(...)` infrastructure. Struct type (already landed) enables named-field explosion. Tuple destructuring (if landed first) provides a natural complement but is not required.

---

## Motivation

Expanding array and vector columns into rows is one of the most common operations in ML data pipelines. In pandas, it's a single call:

```python
df.explode('tags')
df.explode(['tags', 'scores'])  # paired: aligned element-wise expansion
```

In DatumIngest today, expanding a single array column requires a lateral join:

```sql
SELECT d.id, t.value AS tag
FROM documents AS d
CROSS JOIN LATERAL UNNEST(d.tags) AS t
```

This works, but has three problems:

1. **Verbosity**: the `CROSS JOIN LATERAL UNNEST(col) AS alias` pattern is 8 tokens of boilerplate for what is conceptually a one-word operation. Users coming from pandas, Spark, or Presto expect `EXPLODE`.
2. **Paired expansion is impossible**: when two array columns have aligned indices (e.g., `tags` and `tag_scores` where element *i* of each array correspond), there is no way to expand them together in a single lateral join. Users must either (a) use two separate `UNNEST` calls with a `ROW_NUMBER()` join to re-align them, or (b) drop into Python. Neither is acceptable.
3. **Discoverability**: users searching for "expand array" or "flatten nested" don't find `CROSS JOIN LATERAL UNNEST` — the syntax is a composition of three independent SQL concepts (cross join, lateral correlation, table-valued function), none of which individually describes the intent.

DuckDB offers `UNNEST` as both a TVF and a first-class clause, closing gap (1) but not (2). BigQuery has `UNNEST` in `FROM` but does not support multi-column aligned expansion. Spark SQL has `EXPLODE` and `POSEXPLODE` but not paired multi-column expansion.

**No SQL engine supports paired/aligned explosion of multiple array columns.** This is the primary differentiation opportunity — it is the SQL equivalent of pandas `df.explode(['col1', 'col2'])` and is critical for ML datasets where parallel arrays are a natural storage pattern (e.g., token IDs and attention masks, bounding boxes and class labels, feature names and feature values).

---

## Syntax

### Single-column explosion

```sql
SELECT id, tag
FROM documents
EXPLODE tags AS tag
```

Equivalent to:

```sql
SELECT d.id, t.value AS tag
FROM documents AS d
CROSS JOIN LATERAL UNNEST(d.tags) AS t
```

### Multi-column paired explosion

```sql
SELECT id, tag, score
FROM documents
EXPLODE tags AS tag, scores AS score
```

Element-wise: row `i` of the input produces `N` output rows, where `N = MAX(cardinality(tags), cardinality(scores))`. Element `j` of each array appears in the same output row. If one array is shorter than the other, the shorter array's column is NULL for the overflow positions.

### Expression arguments

The source need not be a bare column — any expression that evaluates to an `Array`, `Vector`, `UInt8Array`, or `JsonValue` (JSON array) is valid:

```sql
SELECT id, token
FROM documents
EXPLODE split_part(text, ' ', ',') AS token

SELECT id, element
FROM data
EXPLODE array_concat(left_items, right_items) AS element
```

### WITH ORDINALITY — positional index

An optional `WITH ORDINALITY` modifier adds a 1-based position column:

```sql
SELECT id, tag, position
FROM documents
EXPLODE tags WITH ORDINALITY AS tag, position

-- Paired with ordinality
SELECT id, tag, score, position
FROM documents
EXPLODE tags AS tag, scores AS score WITH ORDINALITY AS position
```

The ordinality column counts from 1 for each input row. In paired mode, the position is the shared index across all exploded arrays.

### NULL and empty semantics — OUTER EXPLODE

By default, `EXPLODE` drops input rows where all array arguments are NULL or empty (same as `CROSS JOIN LATERAL`). `OUTER EXPLODE` preserves them with NULL output columns (same as `LEFT JOIN LATERAL`):

```sql
-- Drops rows with NULL/empty tags
SELECT id, tag FROM documents EXPLODE tags AS tag

-- Preserves rows with NULL/empty tags (tag column is NULL)
SELECT id, tag FROM documents OUTER EXPLODE tags AS tag
```

### Grammar (informal EBNF)

```
explode_clause    ::= [OUTER] EXPLODE explode_binding (',' explode_binding)*
                      [WITH ORDINALITY AS identifier]

explode_binding   ::= expression AS identifier
```

`EXPLODE` appears after `FROM` (and any explicit `JOIN` clauses), before `WHERE`. It is syntactic position — the clause is part of the `FROM` processing pipeline, not the `SELECT` list.

### Clause ordering

```sql
SELECT ...
FROM source
  [JOIN ...]
  [EXPLODE ...  |  OUTER EXPLODE ...]
WHERE ...
GROUP BY ...
HAVING ...
QUALIFY ...
ORDER BY ...
LIMIT ...
```

Multiple `EXPLODE` clauses in sequence are permitted and compound multiplicatively (each row from the first explosion is expanded by the second):

```sql
-- Cartesian product of tags × scores (NOT paired)
SELECT id, tag, score
FROM documents
EXPLODE tags AS tag
EXPLODE scores AS score
```

This is distinct from **paired** expansion (`EXPLODE tags AS tag, scores AS score`), where both arrays expand in lockstep. Multiple `EXPLODE` clauses produce a cross product; multiple bindings in a single `EXPLODE` clause produce an aligned expansion.

---

## Supported Input Types

| Expression type | Output column type | Notes |
|---|---|---|
| `Array<T>` | `T` (element kind) | Most common case. Element kind resolved at plan time from `ColumnInfo.ElementKind`. |
| `Vector` | `Float32` | 1-D float vector. |
| `UInt8Array` | `UInt8` | Byte array expansion. |
| `JsonValue` (JSON array) | `String` | Each array element extracted as string. JSON objects within the array are serialized back to string. |
| `Tensor` | `Vector` | Rank-N tensor explodes along the first dimension, producing rank-(N-1) slices. A matrix produces vectors; a 3-D tensor produces matrices. |

For paired explosion, the output type of each binding is determined independently — pairs need not share the same element type:

```sql
-- tags: Array<String>, scores: Vector
SELECT id, tag, score
FROM documents
EXPLODE tags AS tag, scores AS score
-- tag: String, score: Float32
```

---

## Desugaring

The planner converts `EXPLODE` into the existing lateral join + UNNEST infrastructure. This means no new physical operators are required — all execution uses the existing `LateralJoinOperator` and `FunctionSourceOperator` with `UnnestFunction`.

### Single-column

```sql
-- Input
SELECT id, tag FROM documents EXPLODE tags AS tag

-- Desugared
SELECT documents.id, __explode_0.value AS tag
FROM documents
CROSS JOIN LATERAL UNNEST(documents.tags) AS __explode_0
```

### Single-column OUTER

```sql
-- Input
SELECT id, tag FROM documents OUTER EXPLODE tags AS tag

-- Desugared
SELECT documents.id, __explode_0.value AS tag
FROM documents
LEFT JOIN LATERAL UNNEST(documents.tags) AS __explode_0 ON 1 = 1
```

### Paired multi-column

Paired expansion requires a new internal TVF — `UNNEST_ALIGNED` — that accepts multiple array arguments and produces one output column per argument, expanding element-wise with NULL padding for length mismatches.

```sql
-- Input
SELECT id, tag, score
FROM documents
EXPLODE tags AS tag, scores AS score

-- Desugared
SELECT documents.id,
       __explode_0.c0 AS tag,
       __explode_0.c1 AS score
FROM documents
CROSS JOIN LATERAL UNNEST_ALIGNED(documents.tags, documents.scores) AS __explode_0
```

### WITH ORDINALITY

```sql
-- Input
SELECT id, tag, position
FROM documents
EXPLODE tags WITH ORDINALITY AS tag, position

-- Desugared
SELECT documents.id,
       __explode_0.value AS tag,
       __explode_0.ordinality AS position
FROM documents
CROSS JOIN LATERAL UNNEST_ORD(documents.tags) AS __explode_0
```

For paired + ordinality, `UNNEST_ALIGNED` includes the ordinality column automatically when the `WITH ORDINALITY` modifier is present.

---

## Implementation Plan

### Phase 1: Parser (S)

**New token**: `Explode` in `SqlToken.cs` — a new keyword token.

**New AST node** in `AstNodes.cs`:

```csharp
/// <summary>
/// A single binding within an EXPLODE clause: an expression mapped to an output column name.
/// </summary>
/// <param name="Expression">The array/vector expression to explode.</param>
/// <param name="Alias">The output column name for the exploded values.</param>
public sealed record ExplodeBinding(
    Expression Expression,
    string Alias);

/// <summary>
/// The EXPLODE clause that expands array/vector columns into rows.
/// </summary>
/// <param name="Bindings">One or more expression → alias pairs. Multiple bindings expand in lockstep (paired).</param>
/// <param name="IsOuter">When true, preserves input rows with NULL/empty arrays (LEFT LATERAL semantics).</param>
/// <param name="WithOrdinality">When true, a position column is appended to the output.</param>
/// <param name="OrdinalityAlias">The output column name for the 1-based position (required when WithOrdinality is true).</param>
public sealed record ExplodeClause(
    IReadOnlyList<ExplodeBinding> Bindings,
    bool IsOuter = false,
    bool WithOrdinality = false,
    string? OrdinalityAlias = null);
```

**Parser additions** in `SqlParser.cs`:

1. Add `SqlToken.Explode` to the tokenizer keyword list.
2. Add `ExplodeClauseParser` production between the join clause parser and the WHERE clause.
3. Add `ExplodeClause? Explode` field to the `SelectStatement` record.

The parser must handle:
- `OUTER EXPLODE` as a two-token prefix (similar to `LEFT JOIN`).
- Comma-separated bindings within a single EXPLODE clause.
- Optional `WITH ORDINALITY AS identifier` suffix.
- Multiple sequential EXPLODE clauses producing separate `ExplodeClause` entries (stored as `IReadOnlyList<ExplodeClause>? ExplodeClauses` on `SelectStatement`).

### Phase 2: UNNEST_ALIGNED TVF (S–M)

A new internal table-valued function that accepts N array/vector/byte-array arguments and produces N+1 output columns: one per argument (named `c0`, `c1`, ..., `cN-1`) plus an optional `ordinality` column.

**Expansion semantics**: the output row count equals `MAX(length(arg0), length(arg1), ..., length(argN-1))`. For each output row at position `j`:
- If `j < length(argK)`, column `cK` = `argK[j]`.
- If `j >= length(argK)`, column `cK` = NULL.

This handles the common case where parallel arrays have the same length (ML datasets: token IDs and attention masks are always equal length) while gracefully degrading when lengths differ.

**Implementation**: `UnnestAlignedFunction` implements `IElementKindAwareTableFunction`. The `GetOutputSchema` method returns one `ColumnInfo` per argument, with element kind resolved from the input array metadata. `ExecuteAsync` iterates up to the maximum array length, batching output rows in groups of 1024 (same as `UnnestFunction`).

**Registration**: register as `"unnest_aligned"` in `FunctionRegistry`. This name is internal — users interact via the `EXPLODE` syntax, not the raw TVF. However, exposing it as a callable TVF is harmless and follows the UNNEST precedent.

### Phase 3: Planner Desugaring (S)

In `QueryPlanner.cs`, after resolving the FROM clause and before processing WHERE:

1. For each `ExplodeClause` on the statement:
   a. If single binding (non-paired): desugar to `CROSS JOIN LATERAL UNNEST(expr)` (or `LEFT JOIN LATERAL` if `IsOuter`). If `WithOrdinality`, use a variant that emits the position.
   b. If multiple bindings (paired): desugar to `CROSS JOIN LATERAL UNNEST_ALIGNED(expr1, expr2, ...)` (or `LEFT JOIN LATERAL` if `IsOuter`).
2. Inject the desugared join into the operator tree.
3. Rename the TVF output columns (`value`, `c0`, `c1`, ..., `ordinality`) to the user-specified aliases via `AliasOperator`.

The desugaring happens at the AST→operator translation level, not as an AST rewrite. This avoids modifying the AST (useful for EXPLAIN, language server diagnostics, and error messages that should reference the original EXPLODE syntax).

### Phase 4: Language Server (S)

1. **Keyword completion**: add `EXPLODE` and `OUTER EXPLODE` to the completion list after FROM/JOIN clauses.
2. **Semantic diagnostics**: validate that expression types in EXPLODE bindings are expandable (`Array`, `Vector`, `UInt8Array`, `JsonValue`, `Tensor`). Emit a diagnostic for non-expandable types (e.g., `EXPLODE price AS p` where `price` is `Float32`).
3. **Hover**: document the EXPLODE keyword with a brief description and syntax example.
4. **Column resolution**: exploded aliases must be visible to downstream clauses (WHERE, GROUP BY, SELECT, ORDER BY). The semantic analyzer registers them as available columns in the scope after the EXPLODE clause.

### Phase 5: UNNEST with Ordinality (S)

Extend `UnnestFunction` to optionally emit a 1-based `ordinality` column. This is a minor extension to the existing TVF — adding an incrementing counter to each output row. The planner activates this mode when `WithOrdinality` is true on a single-binding EXPLODE clause.

---

## Use Cases

### 1. Tag Expansion (NLP / Recommendation)

Expand a tags array to join with a tag metadata table:

```sql
SELECT d.id, d.title, t.tag, tm.category
FROM documents AS d
EXPLODE d.tags AS tag
JOIN tag_metadata AS tm ON tag = tm.tag_name
```

**Without EXPLODE**: `CROSS JOIN LATERAL UNNEST(d.tags) AS t` — 8 extra tokens, an intermediate alias (`t`), and a `.value` dereference (`t.value AS tag`).

---

### 2. Paired Token IDs + Attention Masks (Transformer Input)

Tokenized text produces aligned arrays of token IDs and attention masks. These must be expanded together for per-token analysis:

```sql
SELECT doc_id, token_id, attention_mask, position
FROM tokenized_corpus
EXPLODE token_ids AS token_id, attention_masks AS attention_mask
  WITH ORDINALITY AS position
```

**Without EXPLODE**: Two separate `UNNEST` calls, each wrapped with `ROW_NUMBER() OVER (ORDER BY ...)`, then joined on position. Approximately 15 lines of SQL for what is semantically one operation.

---

### 3. Bounding Box + Class Label (Object Detection)

COCO-format annotations store parallel arrays of bounding boxes and class labels per image:

```sql
SELECT image_id, bbox, label, position
FROM annotations
EXPLODE bounding_boxes AS bbox, class_labels AS label
  WITH ORDINALITY AS position
WHERE label IN ('car', 'pedestrian', 'bicycle')
```

The WHERE clause filters *after* explosion — only rows matching the target classes survive. The ordinality column preserves the original annotation order for downstream NMS (non-maximum suppression) or evaluation metrics.

---

### 4. Feature Name–Value Pairs (Sparse Feature Vectors)

Sparse ML features stored as parallel name and value arrays:

```sql
SELECT user_id, feature_name, feature_value
FROM user_features
EXPLODE feature_names AS feature_name, feature_values AS feature_value
```

Equivalent to converting a sparse representation to a long-format (entity–attribute–value) table. This is the inverse of PIVOT — and EXPLODE + GROUP BY can reconstruct the wide format:

```sql
-- Round-trip: explode then re-aggregate
SELECT user_id, feature_name, SUM(feature_value) AS total_value
FROM user_features
EXPLODE feature_names AS feature_name, feature_values AS feature_value
GROUP BY user_id, feature_name
```

---

### 5. JSON Array Flattening (API Responses)

Expand a JSON array column into rows for further processing:

```sql
SELECT request_id, item
FROM api_responses
EXPLODE json_query(response_body, '$.items') AS item
WHERE json_value(item, '$.status') = 'active'
```

The expression argument (`json_query(...)`) extracts the JSON array first, then EXPLODE expands it. Each `item` is a JSON string that can be further decomposed with `json_value`.

---

### 6. Tensor Slicing (Image Batches)

A rank-3 tensor of shape `[batch, height, width]` can be exploded along the batch dimension into individual 2-D slices:

```sql
SELECT batch_id, slice
FROM image_batches
EXPLODE image_tensor AS slice WITH ORDINALITY AS batch_id
-- slice: Matrix (rank-2, [height, width])
```

---

### 7. Outer Explode for Preserving Empty Arrays

Users with no tags should still appear in the output (important for LEFT JOIN semantics):

```sql
SELECT u.user_id, u.name, tag
FROM users AS u
OUTER EXPLODE u.tags AS tag
-- Users with NULL or empty tags appear with tag = NULL
```

---

## Interaction with Other Clauses

| Clause | Interaction | Notes |
|---|---|---|
| **WHERE** | Filters *after* explosion | Exploded columns are available in WHERE predicates. |
| **GROUP BY** | Groups over exploded rows | `GROUP BY tag` after `EXPLODE tags AS tag` groups by individual tags. |
| **LET** | LET bindings see exploded columns | `LET upper_tag = upper(tag)` after `EXPLODE tags AS tag` works as expected. |
| **HAVING** | Filters groups post-aggregation | Standard behavior — no special interaction. |
| **QUALIFY** | Filters after window functions | Exploded columns available in QUALIFY predicates. |
| **ORDER BY** | Orders exploded rows | `ORDER BY tag` sorts by the exploded value. |
| **DISTINCT** | Deduplicates exploded rows | `SELECT DISTINCT tag FROM docs EXPLODE tags AS tag` produces unique tags. |
| **PIVOT** | Explode then pivot | `EXPLODE` feeds a narrow result into PIVOT for reshaping. |
| **UNPIVOT** | Conceptual overlap | UNPIVOT rotates named columns; EXPLODE expands array values. Different inputs, similar row-multiplying effect. |
| **IMPUTE** | Impute runs before explode | If IMPUTE is positioned between WHERE and EXPLODE, null arrays are filled before expansion. If after (design TBD), individual exploded values are filled. |
| **ASSERT** | Can validate exploded rows | `ASSERT tag IS NOT NULL` after explosion validates individual elements. |
| **LIMIT** | Short-circuits explosion | `LIMIT 10` stops the lateral join after 10 output rows — the upstream row may not be fully expanded. This is correct streaming behavior. |

---

## Comparison with Other Systems

| System | Syntax | Paired expansion | Ordinality | Outer semantics |
|---|---|---|---|---|
| **DatumIngest (proposed)** | `EXPLODE a AS x, b AS y` | ✅ Aligned | ✅ `WITH ORDINALITY` | ✅ `OUTER EXPLODE` |
| **DuckDB** | `SELECT UNNEST(a)` in SELECT | ❌ Separate calls | ✅ `generate_subscripts` workaround | ❌ No outer mode |
| **BigQuery** | `CROSS JOIN UNNEST(a) AS x` | ❌ | ✅ `WITH OFFSET` | ❌ Use LEFT JOIN |
| **Spark SQL** | `EXPLODE(a)` / `POSEXPLODE(a)` | ❌ One column only | ✅ `POSEXPLODE` | ✅ `OUTER EXPLODE` |
| **Presto/Trino** | `CROSS JOIN UNNEST(a, b)` | ✅ Multi-argument UNNEST | ✅ `WITH ORDINALITY` | ❌ |
| **Pandas** | `df.explode(['a', 'b'])` | ✅ Multi-column | ✅ Index preserved | N/A |

DatumIngest's EXPLODE combines the best of each: Spark's concise keyword, Presto's multi-argument alignment, and Spark's OUTER mode — with a pandas-inspired multi-binding syntax that is more readable than Presto's positional approach.

---

## Synergies

- **LET bindings** — compute derived arrays via LET, then explode them: `LET tokens = split_part(text, ' ', ...) ... EXPLODE tokens AS token`. The array is computed once (memoized) and expanded.
- **Lambda expressions** (proposed) — filter or transform arrays before explosion: `EXPLODE array_filter(tags, t -> len(t) > 3) AS tag`. Avoids exploding elements that will be immediately filtered out.
- **SPLIT INTO** (proposed) — explode then split: `EXPLODE ... SPLIT INTO ('train.parquet' WHERE ..., 'test.parquet' WHERE ...)`.
- **IMPUTE** (proposed) — fill NULL arrays before explosion: `IMPUTE tags WITH CONSTANT array('untagged') ... EXPLODE tags AS tag`.
- **Struct type** — exploding an array of structs produces struct-typed rows, which can then be accessed via `field(struct_col, 'name')` or tuple destructuring.
- **SCAN** (proposed) — sequential computation over exploded rows: expand a sequence, then fold over it with SCAN for running aggregations.
- **Tensor operations** — EXPLODE on a tensor slices along the first dimension, enabling per-slice processing pipelines for batched inference inputs.

---

## Design Decisions

**Should EXPLODE be a clause (between FROM and WHERE) or a SELECT-list expression (like Spark)?**

Clause, not expression. Rationale: EXPLODE changes the *cardinality* of the row stream — it is a relational operation (like JOIN), not a scalar operation (like a function). Placing it in the FROM pipeline makes the row multiplication visible at the structural level. Spark's `EXPLODE` in SELECT is a common source of confusion ("why did my row count change from a SELECT expression?"). The clause form also composes naturally with WHERE, GROUP BY, and other downstream clauses.

**Should multiple EXPLODE clauses be paired or produce a cross product?**

Multiple bindings within a single `EXPLODE` clause expand in lockstep (paired). Multiple separate `EXPLODE` clauses expand independently (cross product). This follows the principle of least surprise: comma-separated items within a single clause are related; separate clauses are independent. The distinction mirrors `JOIN a, b` (cross) vs. `JOIN a ON a.x = b.x` (correlated).

**What happens when paired arrays have different lengths?**

The shorter array's column gets NULL for positions beyond its length. This is the pandas behavior (`df.explode` pads with NaN when lengths differ) and the most useful default for ML workloads. Raising an error on length mismatch would require a full pre-scan of all arrays before emitting any rows — incompatible with streaming execution.

**Should EXPLODE support Struct arrays?**

Yes. An `Array<Struct>` explodes to individual `Struct` values. Users can then access fields via `field(alias, 'name')` or, if tuple destructuring lands, via `LET {field1, field2} = alias`. This is critical for complex nested data formats (COCO annotations, protocol buffers, JSON-derived structs).

**Should UNNEST_ALIGNED be user-callable or internal-only?**

Expose it. Following the UNNEST precedent, there is no harm in making the underlying TVF callable directly. Power users who prefer the explicit lateral join form should have access. The `EXPLODE` clause is syntactic sugar — it does not hide functionality.

**How does EXPLAIN display EXPLODE?**

EXPLAIN shows the desugared form: `Lateral Cross Join` (or `Lateral Left Join`) with `UNNEST` or `UNNEST_ALIGNED` as the lateral source. The EXPLODE clause name does not appear in the physical plan — it is a syntactic concept, not a physical operator. However, the `OperatorPlanDescription` should include a `source` property noting `"EXPLODE tags AS tag"` for readability.
