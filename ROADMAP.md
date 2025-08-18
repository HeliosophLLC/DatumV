# DatumIngest Roadmap

The following features are architecturally accounted for but deferred from V1:

- ~~**GROUP BY / Aggregation**: COUNT, SUM, AVG, MIN, MAX, GROUP BY, HAVING~~ ✅
- ~~**Spill-to-disk joins**: Grace hash join for datasets too large for memory~~ ✅
- ~~**Parameterized queries**: Named `$parameter` syntax with early binding (AST-level substitution before planning). CLI via `--param key=value`, gRPC via `parameters` map on `QueryRequest`.~~ ✅
- **Adaptive batch sizing**: Auto-tune based on row size estimates and available memory
- **Excel provider**: Read .xlsx files (ITableProvider interface is ready)
- ~~**Scalar subqueries**: Uncorrelated subqueries constant-fold at plan time; correlated subqueries inject a `ScalarSubqueryOperator` that executes per outer row with `OuterRow` context threading.~~ ✅
- ~~**Scalar subquery decorrelation**: Rewrite correlated scalar subqueries with single-column equality correlation and an aggregate (`SELECT MAX(b.val) FROM b WHERE b.id = a.id`) into a `GROUP BY` + `LEFT JOIN`, reducing O(N×M) per-row execution to O(N+M). `TryDecorrelateScalarSubquery` in `SubqueryRewriter` detects the pattern and rewrites into a LEFT JOIN against the grouped inner query. Decorrelated scalar subqueries execute at ~12 ms / 9.85 MB vs. O(N×M) per-row.~~ ✅
- ~~**IN / NOT IN / EXISTS / NOT EXISTS subqueries**: `SemiJoinRewriter` decorrelates correlated subqueries into LEFT SEMI / LEFT ANTI-SEMI hash joins. Uncorrelated IN subqueries are constant-folded to literal value lists; uncorrelated EXISTS subqueries are evaluated as boolean gates at plan time. NOT IN implements SQL-standard three-valued NULL semantics.~~ ✅
- ~~**UNION / INTERSECT / EXCEPT**: Set operations between query results — all six variants (UNION, UNION ALL, INTERSECT, INTERSECT ALL, EXCEPT, EXCEPT ALL) with SQL-standard precedence (INTERSECT binds tighter than UNION/EXCEPT). Hash-based execution with spill-to-disk for UNION DISTINCT.~~ ✅
- ~~**Window functions**: ROW_NUMBER, RANK, LAG, LEAD with OVER/PARTITION BY~~ ✅
- ~~**Dataset splitting**: `hash_split(key, seed)` function returning a deterministic float in [0, 1) per row, enabling reproducible train/val/test splits via WHERE clauses. Combined with window functions (ROW_NUMBER + PARTITION BY), supports stratified splitting that preserves class proportions per split. Temporal splits already expressible via WHERE on date columns.~~ ✅
- **Pipe mode**: Stream results to stdout as CSV/JSON/NDJSON
- ~~**Top-N bounded sort**: Bounded priority queue in `OrderByOperator` for ORDER BY + LIMIT without full materialization~~ ✅
- ~~**WHERE index seek**: Use sorted value indexes to fetch matching rows directly via `ISeekableTableProvider` instead of streaming and discarding — point lookups for high-selectivity equality predicates~~ ✅
- ~~**Range predicate index pruning**: Extend sorted index chunk pruning to handle `<`, `<=`, `>`, `>=`, `BETWEEN`, and `IN` (currently equality only)~~ ✅
- ~~**Seekable Parquet provider**: Implement `ISeekableTableProvider` on Parquet (row-group-level) to unlock index scan and WHERE index seek~~ ✅
- ~~**Seekable HDF5 provider**: Implement `ISeekableTableProvider` on HDF5 using PureHDF `HyperslabSelection` for partial dataset reads~~ ✅
- ~~**Index + manifest co-generation**: Single-pass `index-manifest` command producing both `.datum-index` and `.datum-manifest` with opt-in pairwise interactions~~ ✅
- ~~**Greedy join reordering**: Place the largest table on the probe (streaming) side so LIMIT can short-circuit earlier; smaller tables become hash-table build sides. Gates on all-INNER, non-lateral joins with estimated row counts.~~ ✅
- ~~**Multi-level bloom pruning**: Bloom filter acceleration traverses through nested join operators, propagating build-side key values to buried scan operators across the entire join tree instead of stopping at the first join boundary.~~ ✅
- ~~**Hybrid Grace hash join**: Grace hash join now streams results for in-memory partitions during Phase 1b instead of buffering all probe rows. A LIMIT above the join short-circuits immediately after enough rows are found — eliminating catastrophic probe materialization (previously 21 GB for a 5-table join with LIMIT 100 on a 122 MB dataset).~~ ✅
- ~~**Default memory budgets**: CLI defaults to 2 GB (`CliOptions.MemoryBudgetBytes`); server defaults to 256 MB (`DatumComputeOptions.MemoryBudgetBytes`). Both activate `GraceHashJoinExecutor` and the hybrid streaming probe automatically — no explicit `--memory-budget` flag required. CLI users can override or disable with `--memory-budget <size|0>`.~~ ✅
- **Predicate-filtered row count propagation**: Multiply each table's `EstimatedRowCount` by the selectivity of pushed-down WHERE predicates before feeding the estimates to `TryReorderJoins`. Equality predicates on high-NDV columns get selectivity `1/NDV` (from the manifest `DistinctCount`); range predicates use a fraction of the domain from manifest min/max; multi-predicate selectivities are multiplied (independence assumption). This refines the greedy join order for queries where a small filtered subset of a large table should be a build side rather than the probe side — capturing most of the CBO benefit without a full cardinality model. 32M rows is nothing at the scale of production embedding tables or event logs; the greedy heuristic sees raw table sizes, not what actually flows through the join after a WHERE clause has cut the input by 99.9%.
- **Cost-based optimizer**: Replace greedy join heuristic with cost model
- **Data-dependent function costs**: ~~Replace fixed per-invocation QU with `ComputeCost(DataKind[])` so vector/tensor operations scale with dimensionality~~ — Deprioritized. `ICostAwareFunction` already supports this pattern. Typical ETL vector dimensions (embeddings ≤4096, feature vectors <100) make flat QU 1–2 honest enough; supplemental scaling only matters beyond 100K elements. Trivially addable via base classes if needed.
- **Resolution-aware image costs**: ~~Post-execution cost reporting from image transforms based on actual decoded resolution~~ ✅
- ~~**Query metering system**: `QueryMeter` integrated into `ExecutionContext` for per-query QU accumulation across functions and operators, with `GetUsage` RPC for billing~~ ✅
- **Aggregate finalization surcharge**: Extend `ICostAwareFunction` to aggregate functions with a one-time finalization cost proportional to group size (e.g. MEDIAN's O(N log N) sort, PERCENTILE sorting, MODE frequency scanning). Per-accumulation QU 2 is a reasonable first approximation; revisit if groups routinely exceed 100K rows.
- ~~**Statistics-based partition pruning**: Skip row groups whose min/max statistics prove a predicate unsatisfiable~~ ✅
- ~~**Bloom filter acceleration**: Use Parquet bloom filters to skip partitions for equality predicates~~ ✅
- **Late materialization for Parquet provider**: Implement `IKeyedTableProvider` on `ParquetTableProvider` so expensive binary/nested columns can be deferred past joins. Requires synthetic row-number keying and row-group-level seeking via ParquetSharp.
- **Late materialization for HDF5 provider**: Implement `IKeyedTableProvider` on `Hdf5TableProvider` so large multi-dimensional datasets can be deferred past joins. Requires implicit row-index key and lazy per-dataset loading.
- **Remote data sources**: HTTP/S3/Azure Blob providers
- ~~**Schema caching**: Skip re-inference on repeated queries~~ ✅
- **Data validation**: CHECK constraints / VALIDATE clause for data quality gates
- ~~**Language server (WASM)**: SQL autocomplete, diagnostics, hover via Blazor WebAssembly~~ ✅
- ~~**Language server (SignalR)**: SignalR hub for server-side language intelligence~~ ✅
- **Language server — VS Code LSP extension**: Wrap LanguageServer core in LSP JSON-RPC over stdio
- ~~**Language server — multi-error diagnostics**: Error-recovering parser for multiple parse errors per document~~ ✅
- **Language server — semantic diagnostics**: ~~Unknown table/column warnings~~, type mismatch detection ✅ (partial)
- ~~**Language server — WASM size optimization**: Extract `DatumIngest.Parsing` with manifest POCOs to eliminate all transitive heavy dependencies from LanguageServer/Wasm~~ ✅
- ~~**CASE / WHEN expressions**: Searched CASE (`CASE WHEN cond THEN ... END`) and simple CASE (`CASE expr WHEN value THEN ... ELSE ... END`). Requires new lexer tokens, AST nodes, and evaluator support. `iif()` provides basic inline conditional as a function today.~~ ✅
- **NULLIF function**: `NULLIF(a, b)` returns NULL when a equals b. `COALESCE(a, b, ...)` is already ✅ implemented as a registered function. `NULLIF` has no implementation yet — not registered as a function, not available as expression syntax. Straightforward to add alongside `CoalesceFunction`.
- **`WithChildren` method on `IQueryOperator`**: A method like `IQueryOperator WithChildren(IReadOnlyList<IQueryOperator> children)` that returns a copy of the operator with replaced children. Enables `InstrumentedOperator.InstrumentRecursive` to use generic child discovery from `DescribeForExplain().Children` instead of operator-specific switch statements, and unlocks future copy-on-write tree rewrites.

---

## Type System Extensions (Deferred)

**Status**: Under consideration. These types have clear use cases in OLAP analytics and ML pipelines but are not blocking V1 workflows. The current type system (Float32, UInt8, Vector, Matrix, Tensor, UInt8Array, Image, String, Date, DateTime, Time, Duration, JsonValue, UUID, Boolean) covers the primary ML and analytics needs.

### ~~Time-of-day type (`DataKind.Time`)~~ ✅

~~A standalone time without a date component. Common in OLAP engines (ClickHouse `Time`, DuckDB `TIME`, PostgreSQL `time`). Currently, time components are extractable via `hour()`, `minute()`, `second()` but there is no way to represent a first-class Time column. Would require a new `DataValue` factory (`FromTime(TimeOnly)`), CAST paths (String↔Time, DateTime→Time), and output format mappings.~~

### ~~Interval / Duration type (`DataKind.Duration`)~~ ✅

~~Represents elapsed time spans (days, hours, minutes, seconds). Would allow `date_diff` to return a typed duration instead of a Float32. Useful for temporal aggregation and arithmetic (`date_add` could accept Duration directly). The current Float32 representation for durations works for ML consumption (models see numbers) but loses semantic information. Parquet has no native interval type; HDF5 would need a custom convention.~~

### Enum / Categorical type (`DataKind.Categorical`)

ML-relevant for one-hot and label encoding. Could be represented as String with a fixed domain constraint (known set of valid values). Natural fit with the planned data validation feature (CHECK constraints / VALIDATE clause). Would enable: automatic one-hot encoding, label encoding with stable integer mapping, domain validation on ingest. DuckDB and Polars both support this pattern.

**V1 status**: Explicit-domain encoding functions (`one_hot`, `label_encode`, variants) and stateless feature hashing (`hash_encode`) shipped without a dedicated `DataKind.Categorical`. These cover low-to-high cardinality encoding via SQL functions operating on String columns. A first-class Categorical type remains under consideration for automatic encoding and validation scenarios.

---

## Dataset Revision Pipeline (V3)

**Status**: Design finalized. The revision pipeline transforms raw data into ML-ready shards with vocabulary files, output provenance, and an output manifest. Usable standalone via CLI or via the `CreateOutput` gRPC RPC.

### Architecture

DatumIngest receives a SQL projection plus **output column specifications** — column+encoding pairs that tell the output writer how to handle each column. This is not a SQL-level concern (functions stay stateless); encoding is an output-writer directive.

```
OutputColumnSpecification[] = [
    ("image",  ColumnEncoding.Image),        // raw image bytes → image shard column
    ("label",  ColumnEncoding.Categorical),  // string/uint8 → integer index with vocabulary file
    ("score",  ColumnEncoding.Passthrough),  // default — write as-is
]
```

**Two-pass execution** for categorical columns:

1. **Pass 1 — Vocabulary collection**: Execute the full query but only evaluate categorical column expression subtrees. Non-categorical columns (images, vectors, scalars) are skipped entirely — no decoding, no I/O. Accumulate sorted distinct values per categorical column. Enforce a cardinality cap (default 100K); abort with diagnostic if exceeded (Heliosoph should default to hash encoding for high-cardinality columns).
2. **Pass 2 — Output**: Execute the full query with bound vocabularies. Write shards, emit `.datum-vocabulary` files, collect statistics, produce output manifest and output provenance.

If no categorical columns are specified, pass 1 is skipped and output is single-pass.

### Vocabulary format (`.datum-vocabulary`)

One label per line, deterministically sorted (lexicographic for strings, numeric for integers). Line number = index. Reproducible regardless of data order, shard boundaries, or parallelism.

```
0
1
2
3
4
5
6
7
8
9
```

Naming convention: `{column_name}.datum-vocabulary` alongside output shards.

### Vocabulary acquisition strategy

Three paths, chosen automatically:

1. **Manifest TopK covers full domain** (distinctCount <= TopK): Vocabulary already known from input manifest. Sort and assign indices. No extra pass. Zero additional cost.
2. **Manifest TopK insufficient** (distinctCount > TopK, <= cardinality cap): Two-pass. Pass 1 evaluates only categorical column expressions, accumulates sorted distinct values, skips all non-categorical work (image decode, vector ops, etc.). Cost is dominated by I/O, not compute — typically <2% overhead vs. the output pass.
3. **Cardinality exceeds cap**: Reject categorical encoding for this column. Heliosoph surfaces the diagnostic and suggests hash encoding (irreversible but bounded). User can override with explicit confirmation and higher cap.

### Plan cost estimation

Mandatory before execution. The estimator walks each output column's expression AST, sums `IScalarFunction.QueryUnitCost` (and `ICostAwareFunction` supplemental costs via manifest-derived resolution estimates) per function node, and multiplies by row count. Produces a per-pass QU breakdown:

```
Pass 1 (vocabulary):  categorical columns only     →   180K QU
Pass 2 (output):      all columns                  → 9,065K QU
────────────────────────────────────────────────────
Estimated total:                                    ~ 9,245K QU
```

Image costs estimated from input manifest's mean resolution statistics. If no manifest exists for a source column, the estimator reports the column as "uncosted" and Heliosoph can request manifest generation first or proceed with a conservative default.

**Heliosoph integration**: Receives estimate via `EstimateCost` RPC → checks user balance → auto-approves cheap plans → prompts for expensive plans. During execution, `QueryMeter` budget enforcement acts as the governor: if actual cost exceeds the approved budget (user tried to game the estimate, or the estimate was wrong), execution terminates with `ResourceExhausted`.

### Output artifacts

```
revision_name/
  shard_000000.parquet
  shard_000001.parquet
  label.datum-vocabulary          # one per categorical column
  species.datum-vocabulary
  revision_name.datum-manifest    # output statistics (same format as input manifests)
  revision_name.datum-provenance  # encoding provenance, source query, cost
```

### Output manifest (`.datum-manifest`)

Same `QueryResultsManifest` format as input manifests — pure statistics, no provenance. This means an output manifest is immediately usable as an input manifest for chained pipelines (`raw → transform → manifest → downstream transform`). Contains per-column statistics (min, max, distinctCount, topK, nullCount, etc.) computed from the output data.

### Output provenance (`.datum-provenance`)

Separate file recording *how* the output was produced — source query, encoding decisions, vocabulary references, and cost. This is metadata about the transformation, not statistics about the result. Provenance and manifest evolve independently.

```json
{
  "version": 1,
  "rowCount": 60000,
  "shardCount": 2,
  "costEstimate": 9245000,
  "costActual": 9218400,
  "source": {
    "query": "SELECT image, label FROM 'mnist_train.hdf5'",
    "inputTables": [{ "alias": "mnist_train.hdf5", "rowCount": 60000 }]
  },
  "columns": {
    "image": {
      "storage": "image",
      "semanticType": "image",
      "encoding": {
        "type": "passthrough",
        "sourceColumn": "image",
        "sourceStorage": "image",
        "resolution": [28, 28, 1]
      }
    },
    "label": {
      "storage": "uint8",
      "semanticType": "categorical",
      "encoding": {
        "type": "vocabulary",
        "sourceColumn": "label",
        "sourceStorage": "uint8",
        "vocabularyFile": "label.datum-vocabulary",
        "vocabularySize": 10,
        "sortOrder": "numeric",
        "coverage": 1.0,
        "hasUnknownBucket": false,
        "vocabularySource": "manifest-topk"
      }
    }
  }
}
```

`vocabularySource` records provenance: `"manifest-topk"` (no extra pass), `"vocabulary-pass"` (pass 1 collected it), or `"explicit"` (user-provided vocabulary file).

### Client integration

1. **Client explores dataset** (live gRPC session, queries via `Query` RPC).
2. **Client configures output**: Submits SQL + `OutputColumnSpecification[]` via `CreateOutput` RPC. Input manifest's `EstimatedDistinctCount` informs encoding decisions — high cardinality columns should prefer hash encoding.
3. **DatumIngest estimates cost**: Walks expression ASTs, produces QU estimate, returns to client.
4. **Client approves**: Auto-approve cheap plans, prompt for expensive ones, check balance.
5. **DatumIngest executes**: Two-pass (if categorical) or single-pass, with `QueryMeter` budget governor. Streams progress updates.
6. **Output**: Shards + vocabulary files + output manifest + output provenance.
7. **Client consumes provenance**: Reads `.datum-provenance` for encoding metadata (semanticType, vocabulary references, encoding decisions). Reads `.datum-manifest` for statistics.

### Implementation phases

**Phase 1 — Output manifest generation** (foundation)
- `OutputColumnSpecification` type with `ColumnEncoding` enum (Passthrough, Categorical, Image, Hash)
- Wire `StatisticsCollector` into `IOutputWriter` pipeline to collect statistics during output
- `OutputProvenanceBuilder` — builds `.datum-provenance` with encoding metadata per column
- Output manifest + provenance serialization (AOT-compatible source-generated JSON)
- CLI: `--manifest` flag on `query` command to emit output manifest and provenance alongside data

**Phase 2 — Vocabulary collection**
- `VocabularyCollector` — sorted set accumulator with cardinality cap, implements `IStatisticAccumulator`
- `VocabularyWriter` — writes `.datum-vocabulary` files (one label per line, deterministic sort)
- Two-pass execution orchestration in output writer: detect categorical columns → pass 1 (vocab-only, skip non-categorical expression evaluation) → bind vocabularies → pass 2 (full output)
- String column re-encoding: dictionary build during vocab pass, integer substitution during output pass
- Integer column passthrough: vocabulary file emitted as documentation, data written as-is

**Phase 3 — Plan cost estimation**
- `PlanCostEstimator` — walks expression AST per output column, sums function QU costs, multiplies by manifest row count. Uses `ICostAwareFunction` with manifest-derived resolution for image columns.
- `EstimateCost` gRPC RPC — accepts SQL + output column specs, returns per-pass QU breakdown without executing
- CLI: `--estimate` flag to print cost estimate before execution

**Phase 4 — gRPC output pipeline**
- `CreateOutput` RPC — accepts SQL, output column specs, output format, shard strategy
- Progress streaming — row count, shard count, current pass (vocabulary/output), QU consumed
- Governor integration — `QueryMeter` budget covers both passes combined; abort with `ResourceExhausted` if exceeded

---

## Schema Matching ✅

**Status**: Shipped. Lightweight schema matching discovers star-schema hubs across tables using only manifest statistics.

### Implemented

- **Column matching** — Levenshtein name similarity with suffix bonuses, type compatibility scoring
- **Evidence scoring** — Six signals (name, type, TopK Jaccard, cardinality ratio, range overlap, unique key) combined with configurable weights into composite confidence
- **Join classification** — Automatic 1:1, 1:N, N:1, N:M classification from NDV/RowCount ratios
- **Star schema detection** — Identifies hub tables with ≥2 spoke relationships from OneToMany/ManyToOne candidates
- **Tests** — Column matching, evidence scoring, and star schema detector tests
