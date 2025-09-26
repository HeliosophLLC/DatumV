# SPLIT INTO — Single-Pass Multi-Output Routing

## TL;DR

Add `SPLIT INTO` syntax that routes each result row to one of N output files based on per-target predicates, evaluated in a single pass over the query plan. This replaces the current pattern of running 2–3 identical queries with different `WHERE` clauses for train/val/test splitting. Extends the existing `INTO` + `SHARD ON` infrastructure with a new `RoutingOutputWriter` that demultiplexes rows to N independent `IOutputWriter` instances. No new tokens required — `SPLIT` is a contextual identifier (like `BALANCED`, `STRATIFIED`). The operator is pure streaming with no buffering — each row is evaluated against target predicates in declaration order and dispatched immediately.

---

## Motivation

The most common ML ETL task is splitting a dataset into train/validation/test subsets. Today this requires three separate queries:

```sql
-- Three passes over the same data :(
SELECT * FROM data WHERE hash_split(id, 42) < 0.7 INTO 'train.parquet';
SELECT * FROM data WHERE hash_split(id, 42) >= 0.7 AND hash_split(id, 42) < 0.85 INTO 'val.parquet';
SELECT * FROM data WHERE hash_split(id, 42) >= 0.85 INTO 'test.parquet';
```

**Problems with this approach**:

1. **Triple I/O and computation cost**: The full query plan — scans, joins, window functions, LET evaluations — executes three times. For a 10-minute image processing pipeline, this means 30 minutes.
2. **No atomicity guarantee**: If the second query fails, you have a complete `train.parquet` but no `val.parquet` or `test.parquet`. Downstream consumers see an inconsistent state.
3. **Predicate consistency burden**: The user must manually ensure that the three WHERE clauses are exhaustive and mutually exclusive. An off-by-one in boundary conditions silently drops or duplicates rows.
4. **Query Unit tripling**: Three identical queries burn 3× the QU budget for the same logical operation.

`SPLIT INTO` solves all four problems: one pass, atomic multi-file output, compiler-checked coverage, single QU charge.

---

## Syntax

### Basic form

```sql
SELECT *
FROM data
SPLIT INTO (
  'train.parquet' WHERE hash_split(id, 42) < 0.7,
  'val.parquet'   WHERE hash_split(id, 42) BETWEEN 0.7 AND 0.85,
  'test.parquet'  WHERE hash_split(id, 42) >= 0.85
)
```

### Grammar (informal EBNF)

```
split_into_clause   ::= SPLIT INTO '(' split_target (',' split_target)+ ')'
split_target        ::= string_literal [WHERE expression] [SHARD ON shard_spec]
shard_spec          ::= ('sample_count' | 'byte_size') number_literal
```

- `SPLIT` is a **contextual identifier** — not a reserved keyword. Parsed as an identifier and matched by string value, following the same pattern as `BALANCED`/`STRATIFIED` in `TablesampleMethodParser` and `SKIP`/`WARN`/`ABORT` in `AssertClauseParser`.
- `INTO` reuses the existing `SqlToken.Into`.
- Each `split_target` has an optional `WHERE` predicate. At most one target may omit the `WHERE` clause, serving as a catch-all (ELSE) destination for rows that match no other predicate. If no catch-all exists and a row matches no target, it is discarded.
- Each target independently supports `SHARD ON` for per-target sharding.
- At least two targets are required (a single target is just `INTO`).
- Output format is inferred from each path's extension independently — targets may use different formats.

### Mutual exclusivity and exhaustiveness

Predicates are evaluated in **declaration order**, first-match-wins (like SQL CASE). A row routes to the first target whose predicate evaluates to true. This means predicates do not need to be mutually exclusive in their expressions — only the first matching target receives the row.

| Scenario | Behavior |
|---|---|
| Row matches target 1 and target 3 | Routed to target 1 only (first match) |
| Row matches no target, no catch-all | Row discarded (not an error) |
| Row matches no target, catch-all exists | Routed to catch-all |

The **catch-all target** is a target with no `WHERE` clause. It must be the last target in the list (parser enforces this). Only one catch-all is allowed.

```sql
-- With catch-all (no rows discarded)
SELECT *
FROM data
SPLIT INTO (
  'outliers.csv' WHERE abs(zscore) > 3,
  'normal.parquet' -- catch-all: everything else
)
```

### Interaction with other clauses

`SPLIT INTO` is syntactically **mutually exclusive with `INTO`** — a query uses one or the other, never both. It appears at the same position in the grammar:

```
SELECT ... FROM ... WHERE ... GROUP BY ... HAVING ... QUALIFY ...
ORDER BY ... LIMIT ... OFFSET ...
[INTO ... | SPLIT INTO (...)]
```

`SPLIT INTO` is compatible with all upstream clauses — the routing layer receives fully evaluated rows from the plan. Specific interactions:

| Clause | Interaction with SPLIT INTO |
|---|---|
| `LET` | Split predicates can reference LET output aliases (they receive the projected row) |
| `ORDER BY` | Rows arrive at the router in sorted order; each target file preserves that order |
| `LIMIT` | Applied before routing — limits total rows entering the router, not per-target |
| `SHARD ON` | Per-target sharding; each target independently manages its own shard rotation |
| `WHERE` | Query-level WHERE filters rows before they reach the router; split-target WHERE routes them |
| `QUALIFY` | Applied before routing |
| `DISTINCT` | Applied before routing |

### Interaction with LET bindings in SPLIT predicates

Split predicates are evaluated against the **projected row** — the row after all LET memoization and column aliasing. This means split predicates can reference LET output aliases:

```sql
SELECT
  LET split_key = hash_split(id, 42) AS split_value,
  *
FROM data
SPLIT INTO (
  'train.parquet' WHERE split_value < 0.7,
  'val.parquet'   WHERE split_value < 0.85,
  'test.parquet'
)
```

Because the predicates are evaluated on the already-projected row, LET bindings are already memoized — `hash_split(id, 42)` is computed once per row regardless of how many split targets reference it.

### Mixed output formats

Each target independently infers its output format from the file extension:

```sql
SELECT *
FROM data
SPLIT INTO (
  'train.parquet' WHERE split < 0.8,
  'metadata.csv' -- catch-all: write leftovers as CSV
)
```

### Per-target sharding

Each target may independently specify `SHARD ON`:

```sql
SELECT *
FROM data
SPLIT INTO (
  'train.parquet' WHERE hash_split(id, 42) < 0.7 SHARD ON sample_count 100000,
  'val.parquet'   WHERE hash_split(id, 42) < 0.85,
  'test.parquet'
)
```

Target 1 shards into `train_shard_00000.parquet`, `train_shard_00001.parquet`, etc. Targets 2 and 3 produce single files. Each target's `ShardingOutputWriter` manages its own rotation independently.

---

## Use Cases

### 1. Train/Validation/Test Split

The canonical ML dataset split with deterministic reproducibility:

```sql
SELECT
  LET features = image_to_tensor_chw(resize(load_image(path), 224, 224)),
  features AS input_tensor,
  label
FROM image_dataset
SPLIT INTO (
  'train.parquet' WHERE hash_split(path, 42) < 0.7,
  'val.parquet'   WHERE hash_split(path, 42) < 0.85,
  'test.parquet'
)
```

The image loading and tensor conversion execute once per row. Without `SPLIT INTO`, three passes would load, resize, and convert each image three times — a 3× compute cost for I/O-bound image pipelines.

### 2. Quality-Based Routing

Route rows to different destinations based on data quality:

```sql
SELECT *
FROM sensor_data
SPLIT INTO (
  'clean.parquet'   WHERE temperature BETWEEN -40 AND 60
                      AND humidity BETWEEN 0 AND 100
                      AND pressure BETWEEN 800 AND 1200,
  'suspect.csv'     WHERE temperature IS NOT NULL
                      AND humidity IS NOT NULL,
  'rejected.csv'    -- catch-all: rows with nulls or extreme outliers
)
```

The clean data goes to efficient Parquet for ML training; suspect and rejected data goes to human-readable CSV for review. Different formats for different consumers from a single pass.

### 3. Partitioned Feature Export

Split features by entity type into separate files for downstream model training:

```sql
SELECT entity_id, feature_vector, label
FROM combined_features
SPLIT INTO (
  'user_features.parquet'    WHERE entity_type = 'user',
  'product_features.parquet' WHERE entity_type = 'product',
  'session_features.parquet' WHERE entity_type = 'session'
)
```

### 4. Sharded Training Data with Validation Holdout

Large-scale training data sharded for distributed training, with a single-file validation set:

```sql
SELECT *
FROM preprocessed
SPLIT INTO (
  'train.parquet'  WHERE hash_split(id, 42) < 0.9 SHARD ON sample_count 50000,
  'val.parquet'    -- single file holdout
)
```

Produces `train_shard_00000.parquet` through `train_shard_NNNNN.parquet` plus one `val.parquet`.

### 5. Multi-Label Stratified Export

Export class-specific datasets for one-vs-rest model training:

```sql
SELECT
  LET conf = confidence_score,
  *
FROM predictions
SPLIT INTO (
  'high_confidence.parquet'   WHERE conf >= 0.9,
  'medium_confidence.parquet' WHERE conf >= 0.5,
  'low_confidence.parquet'
)
```

First-match semantics mean `medium_confidence` receives only rows with `0.5 <= conf < 0.9` — no overlap.

---

## Comparison with Alternatives

| Approach | Passes | Atomic | Consistent | QU Cost | Ergonomics |
|---|:---:|:---:|:---:|:---:|---|
| 3× `SELECT ... WHERE ... INTO` | 3 | No | Manual | 3× | Verbose, error-prone predicates |
| CTE + 3× `SELECT FROM cte INTO` | 3* | No | Shared CTE def | 3× | Better consistency, still 3 passes |
| `SPLIT INTO` | **1** | **Yes** | **Compiler-checked** | **1×** | Declarative, single statement |
| Python script with `if/elif` | 1 | No | Programmatic | N/A | Exits SQL pipeline entirely |

\* DatumIngest CTEs are not materialized across separate statements — each `SELECT FROM cte` re-executes the CTE's query plan from scratch.

---

## Steps

### Phase 1: AST Changes

**File:** `src/DatumIngest.Parsing/Ast/AstNodes.cs`

#### 1a. Add `SplitTarget` record

Add near the existing `IntoClause` record (line ~263):

```csharp
/// <summary>
/// A single output target within a <c>SPLIT INTO</c> clause.
/// Each target specifies a file path, an optional routing predicate,
/// and an optional shard specification.
/// </summary>
/// <param name="Format">The output format, inferred from the file extension.</param>
/// <param name="Path">The output file path.</param>
/// <param name="Predicate">
/// The routing predicate. Null for the catch-all target (receives rows
/// matching no other target). At most one target may have a null predicate,
/// and it must be the last target in the list.
/// </param>
/// <param name="Shard">Optional per-target shard specification.</param>
public sealed record SplitTarget(
    OutputFormat Format,
    string Path,
    Expression? Predicate = null,
    ShardClause? Shard = null);
```

#### 1b. Add `SplitIntoClause` record

```csharp
/// <summary>
/// A <c>SPLIT INTO</c> clause that routes rows to multiple output targets
/// based on per-target predicates, evaluated in a single pass.
/// </summary>
/// <param name="Targets">
/// Two or more output targets. Predicates are evaluated in declaration order
/// (first-match-wins). At most one target may omit the predicate (catch-all).
/// </param>
public sealed record SplitIntoClause(IReadOnlyList<SplitTarget> Targets);
```

#### 1c. Add `SplitInto` field to `SelectStatement`

Add an optional `SplitIntoClause? SplitInto = null` field to the `SelectStatement` record, after the existing `Into` field. The planner will validate that `Into` and `SplitInto` are not both non-null (mutually exclusive).

```csharp
public sealed record SelectStatement(
    IReadOnlyList<SelectColumn> Columns,
    FromClause? From = null,
    IntoClause? Into = null,
    SplitIntoClause? SplitInto = null,   // ← new
    IReadOnlyList<JoinClause>? Joins = null,
    Expression? Where = null,
    GroupByClause? GroupBy = null,
    Expression? Having = null,
    Expression? Qualify = null,
    IReadOnlyList<AssertClause>? Assertions = null,
    PivotClause? Pivot = null,
    UnpivotClause? Unpivot = null,
    OrderByClause? OrderBy = null,
    int? Limit = null,
    int? Offset = null,
    bool Distinct = false,
    IReadOnlyList<CommonTableExpression>? CommonTableExpressions = null,
    IReadOnlyList<LetBinding>? LetBindings = null);
```

Because the new parameter has a `null` default and follows the existing optional `Into` field, call sites that use named parameters need no changes.

#### 1d. Add `SplitInto` to `CompoundQueryExpression`

`CompoundQueryExpression` (UNION/INTERSECT/EXCEPT) already carries an optional `Into` field. Add a parallel `SplitInto` field with the same mutual-exclusivity constraint:

```csharp
public sealed record CompoundQueryExpression(
    ...
    IntoClause? Into = null,
    SplitIntoClause? SplitInto = null,   // ← new
    ...);
```

---

### Phase 2: Parser Changes

**File:** `src/DatumIngest.Parsing/SqlParser.cs`

#### 2a. Add `SplitTargetParser`

```
split_target ::= string_literal [WHERE expression] [SHARD ON shard_spec]
```

Parse a string literal (path), optionally followed by `WHERE` + expression, optionally followed by `SHARD ON`. Reuse the existing `ShardClauseParser`. Construct a `SplitTarget` using `InferOutputFormat` on the path (same helper used by `IntoClauseParser`).

#### 2b. Add `SplitIntoClauseParser`

```
split_into_clause ::= SPLIT INTO '(' split_target (',' split_target)+ ')'
```

- Match the identifier `SPLIT` (contextual — not a new token). Use `Token.EqualTo(SqlToken.Identifier).Where(t => GetTokenText(t).Equals("SPLIT", StringComparison.OrdinalIgnoreCase))`.
- Match `Token.EqualTo(SqlToken.Into)`.
- Match parenthesized, comma-separated list of `SplitTarget`.
- Validate: at least 2 targets. At most one target may lack a `WHERE` predicate. A target without a predicate must be the last in the list.

#### 2c. Integrate into `SelectStatementParser`

The `INTO` clause is currently parsed at the end of the SELECT statement, after `LIMIT`/`OFFSET`. `SPLIT INTO` occupies the same position. The parser should attempt `SplitIntoClauseParser` first (since `SPLIT INTO` starts with an identifier followed by `INTO`, while plain `INTO` starts with the `INTO` token directly — no ambiguity), then fall back to `IntoClauseParser`.

The lookahead for disambiguation:
- `SPLIT INTO (...)` — `SplitIntoClause`
- `INTO '...'` — `IntoClause`

These are unambiguous at the token level — `SPLIT` is an identifier, `INTO` is a keyword. No backtracking needed.

#### 2d. Add parsing tests

**File:** `tests/DatumIngest.Tests/Parsing/SplitIntoParsingTests.cs`

| Test | Description |
|---|---|
| `TwoTargets_ParsesCorrectly` | Basic two-target split with WHERE predicates |
| `ThreeTargets_WithCatchAll_ParsesCorrectly` | Three targets, last has no WHERE |
| `CatchAllNotLast_FailsWithError` | Catch-all in non-terminal position |
| `SingleTarget_FailsWithError` | Must have at least two targets |
| `MixedFormats_ParsesIndependently` | Parquet + CSV targets |
| `PerTargetSharding_ParsesCorrectly` | Individual SHARD ON per target |
| `WithLetBindings_ParsesFullQuery` | Full SELECT with LET + SPLIT INTO |
| `MutualExclusion_IntoAndSplitInto_Fails` | Both INTO and SPLIT INTO present |
| `WithOrderByAndLimit_ParsesCorrectly` | SPLIT INTO after ORDER BY + LIMIT |
| `CaseInsensitiveSplit_Parses` | `split`, `SPLIT`, `Split` all accepted |

---

### Phase 3: RoutingOutputWriter

**File:** `src/DatumIngest/Output/RoutingOutputWriter.cs`

The `RoutingOutputWriter` is the core runtime component. It implements `IOutputWriter` and demultiplexes rows to N child writers based on compiled predicate evaluation.

#### 3a. Class design

```csharp
/// <summary>
/// Routes rows to multiple output writers based on per-target predicates.
/// Predicates are evaluated in declaration order (first-match-wins).
/// </summary>
public sealed class RoutingOutputWriter : IOutputWriter
{
    private readonly IReadOnlyList<RoutingTarget> _targets;
    private Schema? _schema;

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingOutputWriter"/> class.
    /// </summary>
    /// <param name="targets">The routing targets, in evaluation order.</param>
    public RoutingOutputWriter(IReadOnlyList<RoutingTarget> targets) { ... }

    /// <inheritdoc />
    public async Task InitializeAsync(Schema schema, CancellationToken cancellationToken = default)
    {
        _schema = schema;
        // Initialize ALL child writers eagerly with the shared schema.
        // This ensures all output files are created even if a target receives zero rows
        // (producing valid empty files rather than missing files).
        foreach (RoutingTarget target in _targets)
        {
            await target.Writer.InitializeAsync(schema, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task WriteRowAsync(Row row, CancellationToken cancellationToken = default)
    {
        foreach (RoutingTarget target in _targets)
        {
            if (target.Predicate is null || target.Evaluate(row))
            {
                await target.Writer.WriteRowAsync(row, cancellationToken);
                return; // first-match-wins
            }
        }
        // No match and no catch-all: row is discarded.
    }

    /// <inheritdoc />
    public async Task WriteBatchAsync(RowBatch batch, CancellationToken cancellationToken = default)
    {
        for (int index = 0; index < batch.Count; index++)
        {
            await WriteRowAsync(batch[index], cancellationToken);
        }
    }

    /// <summary>
    /// Finalizes all child writers and aggregates their summaries.
    /// </summary>
    public async Task<OutputSummary> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        long totalRows = 0;
        long totalBytes = 0;
        List<string> allFiles = new();

        foreach (RoutingTarget target in _targets)
        {
            OutputSummary summary = await target.Writer.FinalizeAsync(cancellationToken);
            totalRows += summary.RowsWritten;
            totalBytes += summary.BytesWritten;
            allFiles.AddRange(summary.FilesCreated);
        }

        return new OutputSummary(totalRows, totalBytes, allFiles);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (RoutingTarget target in _targets)
        {
            await target.Writer.DisposeAsync();
        }
    }
}
```

#### 3b. RoutingTarget record

```csharp
/// <summary>
/// A single routing target: an output writer paired with an optional predicate.
/// </summary>
/// <param name="Writer">The output writer for this target.</param>
/// <param name="Predicate">
/// The compiled predicate expression. Null for the catch-all target.
/// </param>
/// <param name="Evaluator">
/// The expression evaluator used to test the predicate against each row.
/// </param>
public sealed class RoutingTarget
{
    /// <summary>The output writer for this target.</summary>
    public IOutputWriter Writer { get; }

    /// <summary>The predicate expression, or null for the catch-all target.</summary>
    public Expression? Predicate { get; }

    private readonly ExpressionEvaluator? _evaluator;

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingTarget"/> class.
    /// </summary>
    public RoutingTarget(IOutputWriter writer, Expression? predicate, ExpressionEvaluator? evaluator)
    {
        Writer = writer;
        Predicate = predicate;
        _evaluator = evaluator;
    }

    /// <summary>
    /// Evaluates the predicate against the given row.
    /// Returns true if the row should be routed to this target.
    /// </summary>
    public bool Evaluate(Row row)
    {
        if (Predicate is null) return true;
        DataValue result = _evaluator!.Evaluate(Predicate, row);
        return result.IsTruthy;
    }
}
```

The `ExpressionEvaluator` is the same evaluator used by `FilterOperator` — no new evaluation infrastructure needed. Each `RoutingTarget` receives a dedicated evaluator instance constructed with the same function registry and execution context as the main query.

#### 3c. Empty-file semantics

All child writers are initialized eagerly (during `InitializeAsync`), meaning all output files are created even if a target receives zero rows. This is intentional — downstream consumers should be able to assume all declared output files exist. An empty Parquet file with a valid schema header is preferable to a missing file that causes a `FileNotFoundException` in a training pipeline.

#### 3d. Error handling

If a child writer's `WriteRowAsync` throws, the exception propagates immediately. The `RoutingOutputWriter` does not catch per-target errors. On `DisposeAsync`, all child writers are disposed regardless of which one failed — this is important for releasing file handles.

If predicate evaluation throws (e.g., type mismatch in the expression), the error propagates as a standard expression evaluation error with the row context, same as `FilterOperator`.

---

### Phase 4: Query Planner Integration

**File:** `src/DatumIngest/Execution/QueryPlanner.cs`

The planner does not create a routing operator — `SPLIT INTO` (like `INTO`) is handled post-planning at the CLI/execution layer. The planner's role is limited to:

1. **Validation**: Verify that `Into` and `SplitInto` are not both present on the same statement. Emit a diagnostic error if they are.
2. **Passthrough**: Carry `SplitInto` through plan reconstruction (same as `Into` today).
3. **Predicate validation**: Validate that split-target predicate expressions reference only columns that exist in the projected schema. This uses the same column-resolution logic as `WHERE` clause validation.

No new operator is created. The `SPLIT INTO` clause is a **sink directive**, not a plan operator. The execution layer constructs the `RoutingOutputWriter` and drives it from the streaming row loop.

---

### Phase 5: CLI / Execution Integration

**File:** `src/DatumIngest.Cli/Program.cs`

#### 5a. Extract SPLIT INTO clause

Extend `ExtractIntoClause` or add a parallel `ExtractSplitIntoClause`:

```csharp
static SplitIntoClause? ExtractSplitIntoClause(QueryExpression query)
{
    return query switch
    {
        SelectQueryExpression select => select.Statement.SplitInto,
        CompoundQueryExpression compound => compound.SplitInto,
        _ => null,
    };
}
```

#### 5b. Create RoutingOutputWriter from SplitIntoClause

```csharp
static RoutingOutputWriter CreateRoutingOutputWriter(
    SplitIntoClause splitInto,
    FunctionRegistry functionRegistry,
    ExecutionContext executionContext)
{
    List<RoutingTarget> targets = new(splitInto.Targets.Count);

    foreach (SplitTarget target in splitInto.Targets)
    {
        IOutputWriter writer = target.Shard is not null
            ? CreateShardedWriter(target)
            : CreateBaseWriter(target.Format, target.Path);

        ExpressionEvaluator? evaluator = target.Predicate is not null
            ? new ExpressionEvaluator(functionRegistry, executionContext)
            : null;

        targets.Add(new RoutingTarget(writer, target.Predicate, evaluator));
    }

    return new RoutingOutputWriter(targets);
}
```

#### 5c. Integrate into main execution loop

The SPLIT INTO branch mirrors the existing INTO branch. The key difference is that `RoutingOutputWriter` replaces the single `IOutputWriter`:

```csharp
if (splitIntoClause is not null)
{
    await using RoutingOutputWriter router = CreateRoutingOutputWriter(
        splitIntoClause, functionRegistry, context);

    bool schemaInitialized = false;
    await foreach (RowBatch batch in plan.ExecuteAsync(context))
    {
        for (int index = 0; index < batch.Count; index++)
        {
            Row row = batch[index];
            if (!schemaInitialized)
            {
                Schema schema = InferSchema(row);
                await router.InitializeAsync(schema);
                schemaInitialized = true;
            }

            await router.WriteRowAsync(row);
            progress.ReportRow();
        }
        batch.Return();
    }

    OutputSummary summary = await router.FinalizeAsync();
    progress.WriteSummary();
    Console.WriteLine($"Output: {summary.FilesCreated.Count} file(s), {summary.BytesWritten:N0} bytes");
    foreach (string file in summary.FilesCreated)
    {
        Console.WriteLine($"  {file}");
    }
}
```

#### 5d. Server/Compute integration

`ComputeService` and `CommandDispatcher` pass the clause through identically — `SplitIntoClause` is part of the AST, which the server reconstructs into a plan. The server's output-writing layer mirrors the CLI pattern.

---

### Phase 6: Language Server Integration

**File:** `src/DatumIngest.LanguageServer/...`

#### 6a. Syntax highlighting

`SPLIT` should receive keyword highlighting when followed by `INTO`. The language server's semantic token provider can detect the `Identifier("SPLIT") + Into` pattern and classify the identifier as a keyword.

#### 6b. Diagnostics

| Diagnostic | Severity | Condition |
|---|---|---|
| `SplitInto_SingleTarget` | Error | Fewer than 2 targets |
| `SplitInto_CatchAllNotLast` | Error | A target without `WHERE` is not the last target |
| `SplitInto_MultipleCatchAlls` | Error | More than one target lacks `WHERE` |
| `SplitInto_MutualExclusion` | Error | Both `INTO` and `SPLIT INTO` present |
| `SplitInto_UnknownColumn` | Warning | Split predicate references a column not in the projected schema |
| `SplitInto_OverlappingPredicates` | Info | Optional: detect obviously overlapping predicates (e.g., `< 0.8` and `< 0.9` without excluding the first range). First-match-wins makes this non-erroneous but potentially surprising. |

#### 6c. Completions

After `SPLIT`, offer `INTO`. Inside the parenthesized target list, offer file path string completion (if integrated with the file system) and `WHERE` after the path string. After `WHERE`, offer column names from the projected schema. After the predicate, offer `,` (next target), `)` (close), or `SHARD ON`.

---

### Phase 7: Testing

**File:** `tests/DatumIngest.Tests/Execution/SplitIntoTests.cs`

#### Unit tests (RoutingOutputWriter)

| Test | Description |
|---|---|
| `TwoTargets_RoutesCorrectly` | Rows go to correct target based on predicate |
| `ThreeTargets_FirstMatchWins` | Overlapping predicates route to first match |
| `CatchAll_ReceivesUnmatchedRows` | No-predicate target gets leftover rows |
| `NoCatchAll_DiscardsUnmatchedRows` | Unmatched rows silently dropped |
| `EmptyInput_AllTargetsInitialized` | All output files created even with zero rows |
| `AllRowsToOneTarget_OthersEmpty` | Extreme routing still produces all files |
| `PerTargetSharding_RotatesIndependently` | Sharded and non-sharded targets coexist |
| `MixedFormats_WritesCorrectly` | Parquet + CSV targets receive typed output |
| `PredicateError_PropagatesException` | Type mismatch in predicate fails cleanly |
| `LargeDataset_StreamsWithoutBuffering` | Memory remains bounded (no row accumulation) |

#### Integration tests (end-to-end with parsing)

| Test | Description |
|---|---|
| `EndToEnd_TrainValTestSplit` | Full pipeline: parse → plan → execute → verify 3 output files |
| `EndToEnd_WithLetBindings` | Split predicates reference LET aliases |
| `EndToEnd_WithOrderBy` | Output files preserve sort order |
| `EndToEnd_WithShardedTarget` | One target shards, others don't |
| `EndToEnd_HashSplit_Deterministic` | Same seed produces identical splits across runs |

---

## Query Unit Cost

`SPLIT INTO` adds **0 QU** beyond the base query cost. The routing predicate evaluation is trivially cheap compared to the upstream plan (a few comparisons per row). This matches the precedent set by `INTO` (0 QU) and `SHARD ON` (0 QU) — output directives are free.

The critical QU saving is that the *query plan itself* runs once instead of N times. For a query costing 100 QU, three separate `INTO` statements cost 300 QU. `SPLIT INTO` costs 100 QU.

---

## Performance Considerations

### Streaming, zero-copy routing

`RoutingOutputWriter.WriteRowAsync` evaluates predicates and dispatches the same `Row` reference to the matched child writer. No row copying occurs. The routing overhead is N predicate evaluations per row in the worst case (row matches the last target or no target), where N is the number of targets. For typical 2–5 targets, this is negligible.

### Writer contention

Each child writer operates on a separate file. There is no shared state between writers (no locking). The `RoutingOutputWriter` calls child writers sequentially (one row at a time, first-match dispatch), so there is no concurrency within the router itself. Concurrent I/O across targets could be beneficial for high-throughput scenarios but is not worth the complexity for V1.

### Memory model

The router holds N `RoutingTarget` references (one per target). Each target holds one `IOutputWriter` (or `ShardingOutputWriter`). Memory usage is bounded by the individual writers' buffer sizes — typically one Parquet row group buffer per Parquet target, or streaming (no buffer) for CSV targets. The router itself has no per-row state.

### Predicate evaluation cost

Split predicates are standard SQL expressions evaluated by `ExpressionEvaluator` — the same code path as `WHERE` clauses. For the common case of `hash_split(id, 42) < 0.7`, this is a hash computation + float comparison. If the predicate is expensive (e.g., `REGEXP` match), the user should use LET to memoize the expensive part and reference the binding in the split predicates.

---

## Future Extensions

### Checkpoint support

`ShardingOutputWriter` already supports checkpointed writes for crash-resume. Extending this to `RoutingOutputWriter` requires per-target checkpoint tracking — each target maintains its own `CheckpointManager` state. This is a natural follow-on but not required for V1.

### Per-target column selection

A future extension could allow each target to project different columns:

```sql
-- Hypothetical future syntax (not in V1)
SPLIT INTO (
  'features.parquet' SELECT feature_cols WHERE split < 0.8,
  'labels.parquet'   SELECT label_col WHERE split < 0.8,
  'test.parquet'     SELECT * WHERE split >= 0.8
)
```

This requires per-target projection operators, which significantly complicates the writer initialization (different schemas per target). Deferred — the current design uses a shared schema across all targets.

### Row counting per target

The `OutputSummary` from `FinalizeAsync` reports aggregate counts. A per-target breakdown (rows per target, bytes per target, files per target) would be useful for logging:

```
Output: 5 file(s), 2,341,567,890 bytes
  train_shard_00000.parquet  (70,000 rows)
  train_shard_00001.parquet  (70,000 rows)
  train_shard_00002.parquet  (60,000 rows)
  val.parquet                (30,000 rows)
  test.parquet               (30,000 rows)
Split distribution: train=200,000 (66.7%), val=30,000 (16.7%), test=30,000 (16.7%)
```

This is a V1 quality-of-life addition worth implementing — the per-target `OutputSummary` already contains the per-target row counts; `RoutingOutputWriter.FinalizeAsync` just needs to aggregate and report them.
