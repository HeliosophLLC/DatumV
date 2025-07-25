# DatumIngest Roadmap

The following features are architecturally accounted for but deferred from V1:

- **GROUP BY / Aggregation**: COUNT, SUM, AVG, MIN, MAX, GROUP BY, HAVING
- **Spill-to-disk joins**: Grace hash join for datasets too large for memory
- **Adaptive batch sizing**: Auto-tune based on row size estimates and available memory
- **Excel provider**: Read .xlsx files (ITableProvider interface is ready)
- **UNION / INTERSECT / EXCEPT**: Set operations between query results
- **Window functions**: ROW_NUMBER, RANK, LAG, LEAD with OVER/PARTITION BY
- **User-defined functions**: Plugin DLL support via FunctionRegistry
- **Pipe mode**: Stream results to stdout as CSV/JSON/NDJSON
- ~~**Top-N bounded sort**: Bounded priority queue in `OrderByOperator` for ORDER BY + LIMIT without full materialization~~ ✅
- ~~**WHERE index seek**: Use sorted value indexes to fetch matching rows directly via `ISeekableTableProvider` instead of streaming and discarding — point lookups for high-selectivity equality predicates~~ ✅
- ~~**Range predicate index pruning**: Extend sorted index chunk pruning to handle `<`, `<=`, `>`, `>=`, `BETWEEN`, and `IN` (currently equality only)~~ ✅
- ~~**Seekable Parquet provider**: Implement `ISeekableTableProvider` on Parquet (row-group-level) to unlock index scan and WHERE index seek~~ ✅
- ~~**Seekable HDF5 provider**: Implement `ISeekableTableProvider` on HDF5 using PureHDF `HyperslabSelection` for partial dataset reads~~ ✅
- ~~**Index + manifest co-generation**: Single-pass `index-manifest` command producing both `.datum-index` and `.datum-manifest` with opt-in pairwise interactions~~ ✅
- **Cost-based optimizer**: Replace greedy join heuristic with cost model
- **Data-dependent function costs**: ~~Replace fixed per-invocation QU with `ComputeCost(DataKind[])` so vector/tensor operations scale with dimensionality~~ — Deprioritized. `ICostAwareFunction` already supports this pattern. Typical ETL vector dimensions (embeddings ≤4096, feature vectors <100) make flat QU 1–2 honest enough; supplemental scaling only matters beyond 100K elements. Trivially addable via base classes if needed.
- **Resolution-aware image costs**: ~~Post-execution cost reporting from image transforms based on actual decoded resolution~~ ✅
- ~~**Query metering system**: `QueryMeter` integrated into `ExecutionContext` for per-query QU accumulation across functions and operators, with `GetUsage` RPC for billing~~ ✅
- ~~**Statistics-based partition pruning**: Skip row groups whose min/max statistics prove a predicate unsatisfiable~~ ✅
- ~~**Bloom filter acceleration**: Use Parquet bloom filters to skip partitions for equality predicates~~ ✅
- **Remote data sources**: HTTP/S3/Azure Blob providers
- ~~**Schema caching**: Skip re-inference on repeated queries~~ ✅
- **Data validation**: CHECK constraints / VALIDATE clause for data quality gates
- ~~**Language server (WASM)**: SQL autocomplete, diagnostics, hover via Blazor WebAssembly~~ ✅
- ~~**Language server (SignalR)**: SignalR hub for server-side language intelligence~~ ✅
- **Language server — VS Code LSP extension**: Wrap LanguageServer core in LSP JSON-RPC over stdio
- ~~**Language server — multi-error diagnostics**: Error-recovering parser for multiple parse errors per document~~ ✅
- **Language server — semantic diagnostics**: ~~Unknown table/column warnings~~, type mismatch detection ✅ (partial)
- ~~**Language server — WASM size optimization**: Extract `DatumIngest.Parsing` with manifest POCOs to eliminate all transitive heavy dependencies from LanguageServer/Wasm~~ ✅
- **CASE / WHEN expressions**: Searched CASE (`CASE expr WHEN ... THEN ... ELSE ... END`) and simple CASE (`CASE WHEN cond THEN ... END`). Requires new lexer tokens, AST nodes, and evaluator support. `iif()` provides basic inline conditional as a function today.

---

## Type System Extensions (Deferred)

**Status**: Under consideration. These types have clear use cases in OLAP analytics and ML pipelines but are not blocking V1 workflows. The current type system (Scalar, UInt8, Vector, Matrix, Tensor, UInt8Array, Image, String, Date, DateTime, Time, Duration, JsonValue, UUID, Boolean) covers the primary ML and analytics needs.

### ~~Time-of-day type (`DataKind.Time`)~~ ✅

~~A standalone time without a date component. Common in OLAP engines (ClickHouse `Time`, DuckDB `TIME`, PostgreSQL `time`). Currently, time components are extractable via `hour()`, `minute()`, `second()` but there is no way to represent a first-class Time column. Would require a new `DataValue` factory (`FromTime(TimeOnly)`), CAST paths (String↔Time, DateTime→Time), and output format mappings.~~

### ~~Interval / Duration type (`DataKind.Duration`)~~ ✅

~~Represents elapsed time spans (days, hours, minutes, seconds). Would allow `date_diff` to return a typed duration instead of a Scalar. Useful for temporal aggregation and arithmetic (`date_add` could accept Duration directly). The current Scalar representation for durations works for ML consumption (models see numbers) but loses semantic information. Parquet has no native interval type; HDF5 would need a custom convention.~~

### Enum / Categorical type (`DataKind.Categorical`)

ML-relevant for one-hot and label encoding. Could be represented as String with a fixed domain constraint (known set of valid values). Natural fit with the planned data validation feature (CHECK constraints / VALIDATE clause). Would enable: automatic one-hot encoding, label encoding with stable integer mapping, domain validation on ingest. DuckDB and Polars both support this pattern.

**V1 status**: Explicit-domain encoding functions (`one_hot`, `label_encode`, variants) and stateless feature hashing (`hash_encode`) shipped without a dedicated `DataKind.Categorical`. These cover low-to-high cardinality encoding via SQL functions operating on String columns. A first-class Categorical type remains under consideration for automatic encoding and validation scenarios.

---

## Manifest-Derived Categorical Encoding (V3 — needs design)

**Status**: Deferred. V1 encoding functions use explicit inline domains. V3 would derive domains from the manifest’s TopK vocabulary, removing the need to embed labels in queries.

### Motivation

- **Interpretability**: Exact encoding gives a known, reversible mapping (0=cat, 1=dog). Feature hashing is a one-way black box.
- **Medium cardinality**: Columns with hundreds to thousands of distinct values (ZIP codes, product categories) are too many for inline variadic arguments but perfectly encodable without collisions.
- **Cross-dataset reproducibility**: The manifest vocabulary is the contract that guarantees identical encoding between train and score datasets.
- **Downstream model constraints**: Embedding layers need exact vocabulary indices; target encoding needs per-category statistics.

### Open design questions

1. **Vocabulary artifact format.** The current `.datum-manifest` stores TopK (default 10 values) as JSON. Full vocabularies (50K+ entries) shouldn’t bloat the stats manifest. Options: separate `.datum-vocabulary` file, vocabulary section within manifest with lazy loading, or external reference.
2. **`IManifestAwareFunction` interface.** Functions are currently stateless singletons. Manifest-derived functions need bound vocabularies — requires a clone-per-use or factory pattern to inject domain context.
3. **TopK insufficiency.** TopK truncates at K entries. Full-domain encoding needs the complete distinct value set, which may require a vocabulary-collection pass or raising K to full cardinality.
4. **Coverage semantics.** What happens when the vocabulary covers 95% of values? Should encoding auto-inject an UNK bucket? Should coverage ratio be reported?

---

## Output Manifest Generation (V3 — needs design)

**Status**: Deferred. Currently `query` writes output data but no manifest; `manifest` collects statistics but doesn’t write data. These are decoupled.

### V3a — Output sidecar manifest

Generate a `.datum-manifest` alongside query output that describes the derived projection. Same `QueryResultsManifest` format, computed from output data. Enables chained pipelines: raw data → transform → manifest → downstream transform.

### V3b — Encoding provenance extension

Extend the output manifest with encoding provenance: which source column was encoded, what encoding type was applied, and what domain was used. This is metadata about the transformation, not just statistics about the result. Enables round-trip understanding of feature pipelines.

---

## Cross-Manifest Analysis (V2 — needs design)

**Status**: Deferred. The single-manifest insights pipeline (InsightAnalyzer → InsightClusterer → QuerySynthesizer) shipped in V1. Cross-manifest is a distinct product surface that deserves its own design pass.

**The need is real.** Join candidate detection was attempted in consuming software with underwhelming results. Users working with multiple datasets need to know which columns correspond across tables, whether a join is feasible, and what the expected join quality would be. This is a genuine workflow gap, not a speculative feature.

### What exists today

- Per-column feature manifests with TopK, histograms, quantiles, entropy, cardinality estimates
- Pairwise within-manifest interactions (Pearson, Spearman, Cramér's V, ANOVA, MI, Theil's U, missingness correlation)
- Catalog can hold multiple named tables with optional manifests — infrastructure for multi-source is present
- QueryExplainer uses single-side statistics for join cost estimation

### What's needed (8 types sketched, design incomplete)

`CrossManifestAnalyzer`, `CrossManifestResult`, `JoinCandidate`, `JoinEvidence`, `CrossManifestThresholds`, `ManifestWithName`, `CrossManifestQueryBuilder`, `ColumnAliasMap`

### Open design questions

1. **Discovery vs. declaration.** If the user declares column equivalences (country ↔ native_country), ColumnAliasMap is a trivial dictionary. If the system *infers* them, we need fuzzy name matching, value-set intersection, type compatibility — a real schema matching pipeline. Probably both: allow user declarations, attempt inference for the rest.

2. **Evidence scoring.** The V1 plan listed "Schema + TopK + histogram IoU + cardinality" as signals. TopK Jaccard is fragile for numerics (two `age` columns from different populations overlap without being joinable). TopK Jaccard works for categorical join keys but ID columns with millions of distinct values have near-zero TopK overlap despite being perfect keys. Histogram IoU only applies to continuous numerics, not categorical keys. We need a signal ensemble with weights tuned by column type, not a one-size-fits-all similarity score.

3. **Output contract.** Single-manifest QuerySynthesizer rewrites SELECT projections. Cross-manifest QueryBuilder produces JOINs — fundamentally different SQL shape. What dialect? What FROM sources? What if the join is many-to-many (bad)? The output needs join-quality metrics (expected fanout, null-key ratio, cardinality ratio) alongside the generated SQL.

4. **Integration point.** The single-manifest pipeline lives inside ManifestBuilder.Build(). Cross-manifest takes *multiple* manifests — it sits above the single-manifest pipeline. Who calls it? CLI command? Server endpoint? A new top-level analyzer?

5. **What constitutes a "good" join?** Need clear definitions: key uniqueness on at least one side, acceptable null-key ratio, cardinality ratio bounds, value overlap threshold. These are domain-dependent — a star-schema fact→dimension join has very different expectations than a one-to-one entity merge.

### Possible approach

- Phase A: `ManifestWithName` wrapper, `ColumnAliasMap` (user-declared + fuzzy name inference), `CrossManifestThresholds`
- Phase B: `JoinEvidence` with per-signal scores (name similarity, type compatibility, NDV ratio, TopK Jaccard for categoricals, key uniqueness, null-key ratio, value-range overlap for numerics), composite confidence
- Phase C: `JoinCandidate` selection (above composite threshold), `CrossManifestResult` aggregation
- Phase D: `CrossManifestQueryBuilder` — generate JOIN SQL with quality annotations
- Phase E: Integration into CLI (`datum cross-manifest a.json b.json`) and server
