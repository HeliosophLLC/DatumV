# Plan: STRATIFY — Class-Balanced Sampling

## TL;DR

Add two new TABLESAMPLE methods — `STRATIFIED` (proportional per-class sampling) and `BALANCED` (fixed-count per-class sampling) — via an `ON column` clause that specifies the stratification key. Extends the existing TABLESAMPLE infrastructure (parser, AST, planner, operator) with a new `StratifiedSampleOperator` that uses per-group reservoir sampling. Single-pass streaming with bounded memory (reservoir per distinct class). No new tokens required — method names parsed as identifiers like `BERNOULLI`/`SYSTEM`.

## Syntax

```sql
-- Proportional: 10% of each class (preserves class distribution)
SELECT * FROM training_data
TABLESAMPLE STRATIFIED(10) ON label

-- Fixed count: exactly 1000 rows per class (balances class distribution)
SELECT * FROM training_data
TABLESAMPLE BALANCED(1000) ON label

-- With deterministic seed
SELECT * FROM training_data
TABLESAMPLE STRATIFIED(20) ON label REPEATABLE(42)

-- Composite stratification key
SELECT * FROM training_data
TABLESAMPLE BALANCED(500) ON (label, split)

-- Combined with other clauses
SELECT * FROM training_data
TABLESAMPLE STRATIFIED(10) ON label REPEATABLE(42)
WHERE is_valid = 1
ORDER BY label, id
INTO 'train_balanced.parquet'
```

## Steps

### Phase 1: Parser + AST (no new tokens)

1. **Extend `TablesampleMethod` enum** in `src/DatumIngest.Parsing/Ast/AstNodes.cs:194`. Add `Stratified` and `Balanced` variants to the existing `{ Bernoulli, System }` enum.

2. **Extend `TablesampleClause` record** in `src/DatumIngest.Parsing/Ast/AstNodes.cs:211`. Add an optional `StratifyColumns` field: `IReadOnlyList<Expression>? StratifyColumns`. Null for Bernoulli/System (backward compatible). Required for Stratified/Balanced — parser enforces this.

3. **Extend `TablesampleMethodParser`** in `src/DatumIngest.Parsing/SqlParser.cs:1112`. The existing parser matches identifiers `BERNOULLI` and `SYSTEM`. Add `STRATIFIED` and `BALANCED` to the same `.Where(...)` predicate and `.Select(...)` mapping. No new tokens — same pattern as existing methods.

4. **Add `ON` column-list parser** to `TablesampleClauseParser` in `src/DatumIngest.Parsing/SqlParser.cs:1128`. After the percentage `(percentage)` close-paren and before the optional `REPEATABLE(seed)`:
   - Parse optional `ON` keyword (`Token.EqualTo(SqlToken.On)`)
   - If present: parse either a single expression or `(expr, expr, ...)` parenthesized list
   - The `ON` token already exists (used on JOIN ON) — no new token needed
   - For Stratified/Balanced: `ON` is required (parser error if absent)
   - For Bernoulli/System: `ON` must be absent (parser error if present)

5. **Add parsing tests** in `tests/DatumIngest.Tests/Parsing/TablesampleParsingTests.cs`:
   - `Stratified_ParsesMethodAndPercentage`
   - `Balanced_ParsesMethodAndCount`
   - `Stratified_WithOnColumn_ParsesSingleColumn`
   - `Stratified_WithOnColumns_ParsesCompositeKey`
   - `Stratified_WithRepeatable_ParsesSeed`
   - `Balanced_WithoutOn_Fails`
   - `Bernoulli_WithOn_Fails`
   - Case-insensitivity: `stratified`, `STRATIFIED`, `Stratified`

*Parallel with Phase 2 design work. No dependencies.*

### Phase 2: StratifiedSampleOperator

6. **Create `StratifiedSampleOperator`** in `src/DatumIngest/Execution/Operators/StratifiedSampleOperator.cs`. Implements `IQueryOperator`. This is the core algorithm.

   **Constructor signature**:
   ```csharp
   StratifiedSampleOperator(
       IQueryOperator source,
       TablesampleMethod method,        // Stratified or Balanced
       double parameter,                // percentage (0–100) for Stratified, count for Balanced
       IReadOnlyList<Expression> stratifyColumns,
       int? seed)
   ```

   **Algorithm — STRATIFIED (proportional)**:

   Per-row Bernoulli at a uniform rate across all classes. Each row is independently included with probability `percentage / 100`, regardless of class. This is statistically correct for preserving class distribution — each class is sampled at the same rate, so proportions are maintained in expectation. Single-pass, no buffering, follows the existing `SampleScanOperator` pattern.

   If manifest `TopKValues` covers all classes (`EstimatedDistinctCount` ≤ TopK size), class counts are known at plan time. This enables a future single-pass reservoir optimization for exact proportional counts, but the initial implementation uses Bernoulli for simplicity.

   **Algorithm — BALANCED (fixed-count)**:

   Per-group reservoir sampling (Algorithm R). Maintain one reservoir of capacity `N` per distinct class. Stream all rows; for each row, evaluate the stratify expression to determine its class and feed it into that class's reservoir. After all input rows are consumed, emit all reservoirs.

   - **Memory model**: Each reservoir holds at most `N` `Row` references. Total memory = `N × C × sizeof(Row reference)` where `C` is the number of distinct classes. The reservoir stores row references, not copies — rows are kept alive by the reservoir's array.
   - **Memory budget enforcement**: If `N × C` exceeds `ExecutionContext.MemoryBudgetBytes`, the operator warns via diagnostics (no spill — reservoirs are fundamentally in-memory). Cap at a configurable maximum reservoir count (e.g., 10,000 classes) and fail with a clear error if exceeded: *"TABLESAMPLE BALANCED found {C} distinct classes on column '{col}', exceeding the maximum of 10,000. Use a less granular stratification column."*
   - **Emit order**: After all input consumed, iterate reservoirs in insertion order (preserving the order classes were first seen). Within each reservoir, rows are in reservoir-random order (correct for sampling).

   **Class key evaluation**: Use `ExpressionEvaluator` to evaluate each stratify expression per row. Multi-column keys: use the same composite key-building pattern as `GroupByOperator` (structural `DataValue` equality).

   **Seed handling**: Same as `SampleScanOperator` — `new Random(seed)` when deterministic, `Random.Shared` when not. For BALANCED, seed each per-class reservoir's selection random from a deterministic sequence derived from the master seed.

7. **Implement `DescribeForExplain()`** on `StratifiedSampleOperator`:
   ```
   "Stratified Sample" or "Balanced Sample"
   Properties: method, percentage/count, columns, seed
   Children: [(Source, null)]
   ```

8. **Add unit tests** in `tests/DatumIngest.Tests/Execution/StratifiedSampleTests.cs`:
   - `Stratified_ProportionalSampling_PreservesClassDistribution` — 3 classes with known counts, verify each class sampled at ~percentage%
   - `Balanced_FixedCount_ReturnsExactCountPerClass` — 3 classes, N=10, verify exactly 10 per class (or fewer if class has < 10 rows)
   - `Balanced_ClassSmallerThanTarget_ReturnsAllRows` — class has 5 rows, target is 10, returns all 5
   - `Stratified_WithSeed_IsDeterministic` — same seed → same rows
   - `Stratified_DifferentSeeds_ProduceDifferentResults`
   - `Balanced_EmptyInput_ReturnsEmpty`
   - `Balanced_SingleClass_ReturnsReservoir`
   - `Stratified_CompositeKey_StratifiesCorrectly`
   - `Balanced_ExceedsMaxClasses_FailsWithClearError`

*Depends on Phase 1 (AST types needed for constructor). Core implementation work.*

### Phase 3: Query Planner Integration

9. **Extend planner TABLESAMPLE handling** in `src/DatumIngest/Execution/QueryPlanner.cs:4038–4050`. The current code creates `SampleScanOperator` for all methods. Add a branch:

   ```csharp
   if (tablesampleClause.Method is TablesampleMethod.Stratified or TablesampleMethod.Balanced)
   {
       scanOperator = new StratifiedSampleOperator(
           scanOperator, tablesampleClause.Method, parameter,
           tablesampleClause.StratifyColumns!, seed);
   }
   else
   {
       scanOperator = new SampleScanOperator(
           scanOperator, tablesampleClause.Method, percentage, seed);
   }
   ```

   - Evaluate `parameter` and `seed` to constants via existing `EvaluateConstantDouble()`.
   - Stratify column expressions: resolve against the scan operator's output schema (same as how WHERE predicates resolve column references).

10. **Plan-time optimization** *(optional, Phase 3b)*: When manifest statistics are available for the stratify column:
    - Read `TopKValues` and `EstimatedDistinctCount` from `FeatureManifest`
    - If `EstimatedDistinctCount` is high (>10,000), emit a planner warning: *"Stratification column '{col}' has ~{N} distinct values; consider a less granular column."*
    - Pass manifest hints to `StratifiedSampleOperator` for potential single-pass optimization of STRATIFIED mode.

11. **EXPLAIN output test**: Verify that `EXPLAIN SELECT * FROM t TABLESAMPLE BALANCED(100) ON label` produces the correct operator tree description.

*Depends on Phase 1 + Phase 2.*

### Phase 4: End-to-End + Integration Tests

12. **End-to-end integration tests** in `tests/DatumIngest.Tests/Execution/StratifiedSampleTests.cs` (or a dedicated integration test file):
    - Execute full SQL queries through the parser → planner → execution pipeline
    - `SELECT * FROM test_table TABLESAMPLE STRATIFIED(50) ON category` with a known 3-class table
    - `SELECT * FROM test_table TABLESAMPLE BALANCED(5) ON category REPEATABLE(42)` — verify determinism
    - Combined with WHERE: `SELECT * FROM t TABLESAMPLE STRATIFIED(10) ON label WHERE is_valid = 1` — verify WHERE applies first (scan-level), then stratified sampling
    - Combined with ORDER BY and LIMIT
    - Combined with JOINs (stratified sampling on one side)

13. **Language server integration** — the language server in `src/DatumIngest.LanguageServer` should already handle STRATIFIED/BALANCED since they're parsed as identifiers (same as BERNOULLI/SYSTEM). Verify that:
    - Autocomplete suggests STRATIFIED and BALANCED after TABLESAMPLE
    - Hover on TABLESAMPLE shows updated documentation
    - Diagnostics fire for: missing ON clause with STRATIFIED/BALANCED, ON clause with BERNOULLI/SYSTEM

*Depends on Phase 1 + 2 + 3.*

### Phase 5: Documentation

14. **Update `docs/sql.md`** — add STRATIFIED and BALANCED to the TABLESAMPLE section with syntax, semantics, examples, memory model, and interaction with other clauses.

15. **Update docs-site content** — if the docs site mirrors sql.md, update the corresponding page.

*Depends on Phase 4.*

## Relevant Files

| File | Change | Notes |
|------|--------|-------|
| `src/DatumIngest.Parsing/Ast/AstNodes.cs` | Modify | `TablesampleMethod` enum (L194), `TablesampleClause` record (L211) |
| `src/DatumIngest.Parsing/SqlParser.cs` | Modify | `TablesampleMethodParser` (L1112), `TablesampleClauseParser` (L1128) |
| `src/DatumIngest.Parsing/Tokens/SqlToken.cs` | None | `ON` already exists; methods parsed as identifiers — no changes |
| `src/DatumIngest/Execution/Operators/SampleScanOperator.cs` | None | Reference implementation pattern for new operator |
| `src/DatumIngest/Execution/Operators/StratifiedSampleOperator.cs` | **New** | Core algorithm — STRATIFIED Bernoulli + BALANCED reservoir |
| `src/DatumIngest/Execution/QueryPlanner.cs` | Modify | TABLESAMPLE branch at L4038, add Stratified/Balanced routing |
| `src/DatumIngest/Manifest/FeatureManifest.cs` | None | `TopKValues`, `EstimatedDistinctCount` consumed for plan-time hints |
| `src/DatumIngest/Execution/ExecutionContext.cs` | None | `MemoryBudgetBytes` consumed for reservoir cap |
| `src/DatumIngest/Model/RowBatch.cs` | None | `Rent`/`Add`/`Return` pool pattern for operator output |
| `tests/DatumIngest.Tests/Parsing/TablesampleParsingTests.cs` | Modify | Extend with STRATIFIED/BALANCED parsing tests |
| `tests/DatumIngest.Tests/Execution/StratifiedSampleTests.cs` | **New** | Operator unit tests + end-to-end integration tests |
| `docs/sql.md` | Modify | TABLESAMPLE documentation section |

## Verification

1. **No regressions** — `dotnet test --filter "Tablesample"` confirms existing Bernoulli/System tests pass unchanged
2. **Parsing tests** — STRATIFIED/BALANCED parse correctly, ON clause enforced/forbidden, composite keys work
3. **Operator unit tests** — proportional preserves distribution, balanced returns exact counts, deterministic seeds, edge cases (empty input, single class, class < target)
4. **E2E integration** — full SQL roundtrip through parser → planner → execution
5. **EXPLAIN output** — correct operator descriptions for both methods
6. **Memory bounds** — high-cardinality column (>10K classes) triggers clear error, not OOM
7. **Language server** — autocomplete, hover, diagnostics (manual verification)
8. **Full suite** — `dotnet test` passes with zero failures

## Decisions

| Decision | Rationale |
|----------|-----------|
| **No new tokens** | STRATIFIED/BALANCED parsed as identifiers, matching BERNOULLI/SYSTEM. Avoids reserving keywords that could break user table/column names. |
| **ON reuses `SqlToken.On`** | Already a keyword for JOIN ON. No ambiguity — ON only appears after the TABLESAMPLE percentage parenthesis, before REPEATABLE. |
| **BALANCED = reservoir sampling, not two-pass** | Single-pass is simpler and works streaming. Trade-off: must hold `N × C` rows in memory. Acceptable because `N` is user-specified (typically small) and `C` is bounded by the max-classes cap. |
| **STRATIFIED = uniform Bernoulli** | Same rate for all classes. Statistically correct for preserving proportions. Simplest approach — no buffering, no counting. |
| **WHERE before TABLESAMPLE** | Existing architecture applies TABLESAMPLE at the scan level (wraps `ScanOperator`). Pushed-down WHERE predicates filter first. Correct semantics: sample from the filtered population. |
| **Emit order for BALANCED** | Class-first: all rows from class A, then class B, etc. Users add `ORDER BY random()` for shuffled output. Matches reservoir semantics without an extra shuffle. |
| **Max classes cap: 10,000** | Internal constant in `StratifiedSampleOperator`, not user-facing. Protects against accidental `ON user_id` on a high-cardinality column. |
| **Composite key via structural equality** | Multiple columns in `ON (col1, col2)` form a composite class key. Key equality follows the same pattern as `GroupByOperator`. |

## Further Considerations

1. **OVER clause for TABLESAMPLE** — future extension: `TABLESAMPLE BALANCED(100) ON label OVER (PARTITION BY dataset)` for per-partition balanced sampling. Requires `WindowOperator`-style partitioning infrastructure. Defer until user demand.

2. **Interaction with SHARD ON** — `TABLESAMPLE BALANCED(1000) ON label INTO 'output.parquet' SHARD ON sample_count 5000` works naturally — sharding is downstream of sampling. No special handling needed.

3. **Query Unit cost** — STRATIFIED Bernoulli adds 0 QU (same as existing TABLESAMPLE). BALANCED reservoir adds 0 QU (sampling is a filter, not a computation). Stratify column evaluation per row is analogous to a WHERE predicate — free.

4. **Plan-time class distribution hints** — `FeatureManifest.TopKValues` already provides exact frequencies for the most common values. When `EstimatedDistinctCount` ≤ TopK size, the full class distribution is known before execution. This could enable exact proportional sampling (reservoir per class, target = `ceil(classCount × percentage / 100)`) instead of probabilistic Bernoulli. Worth adding as a Phase 3b optimization — predictable output row counts improve downstream planning.

5. **BALANCED with underrepresented classes** — when a class has fewer rows than the target count, the reservoir contains all rows from that class (natural reservoir behavior). The operator does not oversample (repeat rows) to reach the target. If oversampling is desired, it would be a separate `OVERSAMPLE` method — distinct semantics, distinct operator.
