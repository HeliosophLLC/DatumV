# Vector search index (HNSW)

## Goal

Add a per-column approximate-nearest-neighbour index over `Float32[]` columns
so the LLM assistant can do semantic search over message embeddings:

```sql
SELECT id, body, embedding <=> $query_vec AS distance
FROM messages
WHERE conversation_id = $conv_id
ORDER BY embedding <=> $query_vec
LIMIT 10;
```

should return without computing distance against every row.

Sister plan to [fts-inverted-index.md](fts-inverted-index.md). Together they
cover the two retrieval shapes the assistant needs (keyword + semantic), and
both feed the hybrid rerank operator at the end.

## Non-goals

- Not part of any PRIMARY KEY or composite index. Acceleration-only.
- Not in the business of *producing* embeddings. The index takes `Float32[]`
  columns as input; how the column got populated (an `embed_text(...)` UDF, an
  external pipeline, INSERTs from the assistant ingest path) is the caller's
  problem.
- No multi-vector-per-row (ColBERT-style) in v1.
- No filtered ANN with arbitrary `WHERE` predicates in v1 — see "Filtering" below.
- No quantization (Float16 / PQ / int8) in v1 — float32 vectors throughout.
- Approximate, not exact: v1 ships HNSW only. Exact brute-force is the
  fallback when no index exists.

## What "vector index" means here

For each indexed `Float32[]` column with a declared dimension, the index
stores an HNSW graph mapping each row to a node in a small-world layered
graph. Query is a greedy walk top-down through the layers terminating with a
beam search at layer 0 that returns the approximate top-k neighbours under a
chosen distance metric.

Sits alongside FTS as a parallel sidecar: `.datum-vec-{column}`.

## Architecture

### Layering

```
┌────────────────────────────────────────────────────────────┐
│  SQL surface: <->, <=>, <#> distance operators;            │
│               ORDER BY <dist> LIMIT k                      │
├────────────────────────────────────────────────────────────┤
│  VectorTopKOperator  (planner-injected, returns top-k)     │
│  Hybrid rerank operator (shared with FTS, separate PR)     │
├────────────────────────────────────────────────────────────┤
│  IVectorIndex                                              │
│    .Dimension, .DistanceKind                               │
│    .SearchTopK(query, k, efSearch) → ranked iterator       │
│    .Insert(rowRef, vector)                                 │
│    .MarkDeleted(rowRef)                                    │
├────────────────────────────────────────────────────────────┤
│  HnswGraphStore   (page-based persistence, node layout)    │
│  DistanceKernels  (cosine, L2, dot — SIMD via Vector<T>)   │
└────────────────────────────────────────────────────────────┘
```

`IVectorIndex` is its own interface — same reasoning as FTS. It's not
`IColumnIndex` (no value-equality / range semantics) and not
`ITextSearchIndex` (the input is a numeric vector, not a text query).

Provider gains a parallel accessor on
[ITableProvider.cs](../src/DatumV/Catalog/ITableProvider.cs):

```csharp
bool TryGetVectorIndex(string columnName, out IVectorIndex index);
```

### Storage shape

HNSW node layout (one node = one row in the indexed column):

| Field | Size | Notes |
|---|---|---|
| `nodeId` (= row index) | implicit | derived from chunkIdx + rowOff |
| `level` | 1 byte | top layer this node appears in |
| `vector` | `4 * dim` bytes | inline float32 copy — vector reads dominate, indirection through the data file is too slow |
| `neighbourCount[level]` | 1 byte × (level+1) | |
| `neighbours[level]` | `4 bytes × M × (level+1)` | uint32 node ids, M = config (default 16; 2M at layer 0) |

Page layout for `.datum-vec-{column}`:

```
┌────────────────────────────────────────────────────────────┐
│ Dual-slot header (8 KiB total) — mirrors B+Tree pattern:   │
│   commitGen, entryPoint nodeId, maxLevel, nodeCount,       │
│   pageCount, dimension, distanceKind, M, efConstruction,   │
│   analyzerVersion, CRC32                                   │
├────────────────────────────────────────────────────────────┤
│ Node pages (8 KiB each) — pack as many nodes as fit;       │
│   per-page directory of (nodeId → in-page offset).         │
│   Nodes never span pages → max embedded vector size        │
│   ~1900 floats with M=16. (Bigger dims = ~1 node/page.)    │
├────────────────────────────────────────────────────────────┤
│ Free-list head — deferred (matches existing trees).        │
└────────────────────────────────────────────────────────────┘
```

This is **not** the same shape as `MutableBPlusTreeBytes`; we can't piggyback
on it (no tree topology, no key ordering). It's its own page-COW file. The
crash-safety pattern is shipped: dual-slot header + CRC + COW-on-write +
fsync-then-flip. Lift it from
[MutableBPlusTreeBytes.cs](../src/DatumV/Indexing/BTree/MutableBytes/MutableBPlusTreeBytes.cs)
as a shared `PageCowFile` abstraction in PR-VEC-B (small refactor; benefits
the existing trees too).

### Dimension constraint — new column metadata

Today `Float32[] + IsArray` columns have no declared length. HNSW needs every
vector in the index to match a single dimension. Two options:

**Option 1 — index-level dim, runtime-checked.**
`CREATE INDEX ... USING HNSW(embedding) WITH (dim = 384)` captures the
dimension on the index. Inserts whose array length ≠ dim throw at insert
time. Simple but late-failing.

**Option 2 — column-level dim, declared in DDL.**
`embedding Float32[384]` (or pgvector-style `embedding vector(384)`) carries
the dimension on the column. Catalog enforces on INSERT regardless of index.
The index just reads it.

Option 2 is the right shape long-term but expands the SQL/parser/catalog/
manifest scope materially. **Recommendation:** ship Option 1 in PR-VEC-A
(constraint lives on the index), file Option 2 as a separate parser/catalog
PR. Both can coexist — when the column declares a dim, the index validates it
matches.

### Distance metrics

Three at launch, matching pgvector:

| Operator | Metric | Notes |
|---|---|---|
| `<->` | Euclidean (L2) | `sqrt(sum((a-b)^2))` — actually L2² in scoring, sqrt only for display |
| `<=>` | Cosine distance | `1 - dot(a,b) / (norm(a) * norm(b))` — assumes normalized vectors? See below. |
| `<#>` | Negative inner product | pgvector's convention — `ORDER BY ... ASC` then naturally returns highest IP first |

Each index commits to one metric at create time (stored in the header).
You can't switch metrics on a built index — pick at CREATE INDEX.

**Normalization choice for cosine:** two valid designs:
- *Trust the caller* — store vectors as-is, compute norms at query/insert
  time. Slower per-query, more flexible.
- *Pre-normalize at insert* — divide each vector by its norm before storing;
  cosine collapses to negative dot product. Fastest, but the stored vectors
  no longer match the source column (subtle correctness gotcha if anyone
  reads the index file directly).

**Recommendation:** pre-normalize for cosine. The "stored vector ≠ source
vector" surprise is acceptable because the index is an opaque sidecar — no
one reads it directly except `IVectorIndex`. Store a flag in the header
saying the vectors are normalized; future tooling that needs raw vectors
re-reads the source column.

### Distance kernels — SIMD

Float32 distance kernels using `System.Numerics.Vector<float>`. Three
implementations × three operations (dot, L2², norm) = nine kernels, each ~30
LOC. `Vector256<float>` on AVX2 hardware; the runtime picks. Single-precision
only for v1.

Expected throughput: ~50–80 GFLOPS on a modern CPU for dot product, which
means a 384-dim query against a 1M-row corpus does ~770M ops ≈ 10–15 ms
brute-force. HNSW gets that under 1 ms at default params.

### Build / maintenance

Mirrors PR13's incremental-extend story:

- **Initial build** — on `CREATE INDEX ... USING HNSW`, scan the column,
  insert vectors one by one. **Build is expensive:** ~O(N · log N) graph
  operations, each touching M neighbours at multiple layers. 1M vectors at
  dim=384 is ~30 seconds on a fast box. Build must run on a background
  worker, not block the DDL statement. Statement returns once the index file
  is created + header committed in "building" state; an
  `IndexBuildWorker` (parallel to PR17's `AnalyzeWorker`) fills it. Queries
  fall back to brute-force scan until the header flips to "ready."
- **INSERT path** — vector insert is cheap (~M·log N graph touches). Hooks
  into the same mutation pipeline FTS uses; updates entry point + maxLevel
  in the header on level promotion.
- **DELETE path** — mark the node as deleted in a bitmap stored in a header
  trailer; the graph topology stays. Queries filter deleted nodes from the
  result set. Heavy delete workloads degrade recall; trigger REINDEX once
  the deleted fraction crosses a threshold (~20%).
- **UPDATE path** — mark old delete + insert new. The graph keeps both
  around until REINDEX.
- **REINDEX** — full rebuild from the source column. Cleans deletes,
  rebuilds graph topology, refreshes entry point.

### Filtering — the elephant

`WHERE conversation_id = $conv_id ORDER BY embedding <=> $q LIMIT 10` is
the canonical assistant query: scope by metadata, then rank by similarity.

V1 strategy — **post-filter with overfetch.** Run HNSW with `k' = k * α`
(α = 4 by default), then apply `WHERE` filters to the candidate set, then
truncate to `k`. Fails open when the filter selectivity is low (returns
fewer than `k` rows).

The right long-term answer is *pre-filter* — use the existing per-column
indexes / chunk pruning to compute a candidate row set first, then push that
set into HNSW search as a "permitted nodes" bitmap that the graph walk
respects. That's PR-VEC-G. Out of scope for v1, but the operator's API
should leave room: `SearchTopK(query, k, efSearch, IRowFilter? filter)`.

### Sidecar lifecycle

Same as FTS:
1. Provider discovers `.datum-vec-{column}` files on construction.
2. Opens through `HnswGraphStore`; exposes via `TryGetVectorIndex`.
3. REINDEX rebuilds; DROP INDEX deletes.
4. Crash mid-build → header in "building" state with a generation marker; on
   open, file is truncated to the last full commit point and the
   `IndexBuildWorker` restarts.

## SQL surface

Stay Postgres-flavored ([dialect anchor](../memory/feedback_postgresql_anchor.md))
and align with pgvector where pgvector is the de facto standard.

### DDL

```sql
-- Minimum
CREATE INDEX idx_messages_embedding ON messages USING HNSW(embedding)
  WITH (dim = 384, metric = 'cosine');

-- All knobs
CREATE INDEX idx_messages_embedding ON messages USING HNSW(embedding)
  WITH (
    dim = 384,
    metric = 'cosine',      -- 'cosine' | 'l2' | 'inner'
    m = 16,                 -- graph degree at upper layers
    ef_construction = 200,  -- build-time beam width
    ef_search = 40          -- default query-time beam width (overridable per query)
  );

DROP INDEX idx_messages_embedding;
REINDEX INDEX idx_messages_embedding;
```

`USING HNSW` parallels FTS's `USING FTS` — purpose-built clause, no GIN/GiST
generic-index machinery. pgvector spells it `USING hnsw (embedding
vector_cosine_ops)` — that's tied up with the `tsvector`/operator-class
plumbing we deliberately don't have. We commit to `USING HNSW` + a `metric`
WITH-option; revisit operator-class compatibility if/when pgvector
interoperation matters.

### Query

```sql
-- Top-k by cosine distance
SELECT id, body FROM messages
ORDER BY embedding <=> $1
LIMIT 10;

-- With metadata pre-filter (post-filter v1, pre-filter PR-VEC-G)
SELECT id, body FROM messages
WHERE conversation_id = $conv_id
ORDER BY embedding <=> $1
LIMIT 10;

-- Per-query ef_search override via a settings function (no special syntax)
SELECT set_config('hnsw.ef_search', '200', true);
SELECT ... ORDER BY embedding <=> $1 LIMIT 100;
```

Three new built-in distance operators registered as scalar functions:

| SQL | Function | Returns |
|---|---|---|
| `a <-> b` | `vector_l2_distance(Float32[], Float32[])` | Float64 |
| `a <=> b` | `vector_cosine_distance(Float32[], Float32[])` | Float64 |
| `a <#> b` | `vector_negative_inner_product(Float32[], Float32[])` | Float64 |

When neither operand has an index, the functions evaluate exactly per-row
(the SIMD kernels are already there).

### Planner integration

A new `VectorTopKOperator` is injected when:
1. `ORDER BY <distance-expr> [ASC] LIMIT k` is present, and
2. `<distance-expr>` is `<col> <op> <const-or-param>` where `<op>` is one of
   the three vector operators, and
3. `<col>` has an HNSW index built with the matching metric, and
4. The query has no `OFFSET` ≥ index entry count (degenerate case).

Otherwise: brute-force scan with the SIMD kernels + top-k heap. This path is
the universal fallback and should be benchmarked alongside HNSW — many
real-world corpora (≤100K vectors) are faster brute-force.

`<#>` deserves a special note: pgvector's `<#>` returns *negative* inner
product so that `ORDER BY <#> ASC` naturally ranks highest-IP-first. We
follow the same convention.

## File / code layout

```
src/DatumV/Indexing/Vector/
  IVectorIndex.cs
  VectorDistanceKind.cs              // Cosine | L2 | InnerProduct
  DistanceKernels.cs                 // SIMD float32 cosine/L2/dot
  HnswParameters.cs                  // M, efConstruction, efSearch defaults
  HnswGraph.cs                       // in-memory build + search
  HnswGraphStore.cs                  // page-COW persistence
  HnswHeader.cs                      // dual-slot header
  HnswNodePage.cs                    // page encode/decode
  HnswIndex.cs                       // IVectorIndex impl over the store
  VectorOperatorRegistration.cs      // <-> <=> <#> as scalar functions

src/DatumV/Indexing/Shared/   // lifted from BTree/MutableBytes
  PageCowFile.cs                     // dual-slot crash-safe page file
  PageCowHeader.cs

src/DatumV/Execution/Operators/
  VectorTopKOperator.cs

src/DatumV/Catalog/
  ITableProvider.cs                    // + TryGetVectorIndex
  Providers/DatumFileTableProviderV2.cs // + open .datum-vec-{col} sidecars
  TableCatalog.cs                      // + CREATE INDEX USING HNSW path

src/DatumV/Sql/
  Parser/*                             // USING HNSW + WITH (dim, metric, m, ...)
  Ast/*                                // distance operator AST nodes
  Planner/*                            // top-k injection logic

tests/DatumV.Tests/Indexing/Vector/
  DistanceKernelsTests.cs              // SIMD parity with reference impl
  HnswGraphTests.cs                    // in-memory recall@10 ≥ 0.95
  HnswGraphStoreTests.cs               // crash-safety, COW, reopen
  HnswIndexLifecycleTests.cs           // CREATE / INSERT / DELETE / REINDEX
tests/DatumV.Tests/Execution/Operators/
  VectorTopKOperatorTests.cs
tests/DatumV.Tests/Sql/
  VectorSearchQueryTests.cs            // end-to-end ORDER BY <=> queries
```

## Phasing

| PR | Scope | LOC | Why this slice |
|---|---|---|---|
| **PR-VEC-A** | `DistanceKernels` (SIMD float32 cos/L2/dot) + `<->/<=>/<#>` scalar functions registered + brute-force `VectorTopKOperator` (no index yet) | ~600 | Ship the SQL surface end-to-end with brute-force first. Validates the planner shape, gives a baseline to measure HNSW against, and works immediately for small corpora. |
| **PR-VEC-B** | In-memory `HnswGraph` (build + search) + `PageCowFile` refactor lifting dual-slot pattern out of `MutableBPlusTreeBytes` | ~900 | The graph algorithm in isolation, tested for recall on a known dataset (sift-1M / glove). The refactor is small and pays for itself the moment a second consumer of page-COW exists. |
| **PR-VEC-C** | `HnswGraphStore` (page-COW persistence over `PageCowFile`) + `HnswIndex` (`IVectorIndex` impl) + sidecar lifecycle in `DatumFileTableProviderV2` | ~700 | Make it persistent and crash-safe. |
| **PR-VEC-D** | `CREATE/DROP INDEX USING HNSW` DDL + `IndexBuildWorker` for async build + planner routes top-k through the index when present | ~600 | Wire it to SQL. After this, the LLM assistant can use it. |
| **PR-VEC-E** | Incremental INSERT / mark-delete on mutation, REINDEX rebuild | ~400 | Required before steady-state production use. |
| **PR-VEC-F** | Hybrid rerank operator (RRF) — combines FTS and vector ranked iterators | ~250 | Tiny PR; shared with FTS plan. Should land *after* both FTS and vector are in. |
| **PR-VEC-G** *(optional)* | Pre-filter support: pass `IRowFilter` bitmap into HNSW search, planner builds the bitmap from `WHERE` predicates that have other indexes | ~700 | Quality / correctness for "WHERE conv_id = X ORDER BY <=>" patterns. Defer until post-filter overfetch hits its limits. |
| **PR-VEC-H** *(optional)* | Quantization: Float16 storage + int8 scalar quantization with re-rank, header flag selecting precision | ~800 | Disk/memory win at scale. Skip until index files cross ~10 GB. |
| **PR-VEC-I** *(optional)* | Column-level `Float32[N]` / `vector(N)` typed-dimension declaration in DDL; catalog enforces at INSERT | ~500 | Separate parser/catalog PR; not on the critical path. |

Total to ship (A–F): ~3450 LOC, ~8–10 working days.

## Sequencing relative to FTS

These two plans interlock:

1. **FTS-A → FTS-B → FTS-C** (boolean → BM25 → maintenance) — gets the
   assistant keyword search.
2. **VEC-A → VEC-B → VEC-C → VEC-D → VEC-E** — gets the assistant semantic
   search.
3. **VEC-F (hybrid rerank)** lands once both are in, and gives the
   assistant fused retrieval in one operator.

Independent enough that VEC-A can start before FTS-C is done. VEC-A is a
particularly low-risk start — it ships the SIMD kernels and SQL operators
with no new file format and no planner changes beyond a function-style
top-k brute-force path. Worth doing even if the rest of the vector PRs slip.

## Open questions to resolve before PR-VEC-A

1. **Cosine normalization at insert vs at query.** Recommendation above is
   pre-normalize at insert. Confirm the "stored ≠ source" trade-off is
   acceptable.
2. **`vector(N)` type vs index-level dim.** Option 1 (index-level) for v1.
   Confirm we're OK deferring the column-type story to PR-VEC-I.
3. **Brute-force-first or HNSW-first?** Plan above ships brute-force in
   PR-VEC-A and HNSW after. Alternative: build PR-VEC-A as the full HNSW
   path. The brute-force-first ordering is much lower-risk and gives the SQL
   surface a working fallback forever — preferred unless time pressure
   demands the opposite.
4. **`set_config('hnsw.ef_search', ...)` vs a query-level `OPTION (...)`
   syntax.** PG uses `SET hnsw.ef_search = 200;`. We don't have session GUCs
   today. Cheapest v1: a hardcoded default in the header, plus a per-index
   override at CREATE. Per-query override deferred.
5. **Recall acceptance criterion.** What's "good enough" recall@10 for the
   test suite? Industry standard is 0.95 @ default params on sift-1M.
   Confirm we hold to that.

## Hooks for what comes after

- **Embeddings model in catalog** ([model expansion Phase 3](../memory/project_model_catalog_expansion.md))
  — once `embed_text('minilm-l6', body)` returns a `Float32[384]`, an
  `INSERT ... SELECT embed_text(...) FROM ...` flow combined with this index
  is the canonical "ingest text + make it semantically searchable" pipeline.
  No coupling between the two — vector search ships independently.
- **Generated columns** — once available, `embedding GENERATED ALWAYS AS
  (embed_text('minilm-l6', body)) STORED` makes the maintenance story
  automatic.
- **Hybrid rerank operator** (PR-VEC-F) is the shared payoff with the FTS
  plan: one operator that fuses BM25 + vector top-k with Reciprocal Rank
  Fusion. ~200 LOC after both surfaces exist.
- **Pre-filter ANN** (PR-VEC-G) lets the planner combine a B+Tree or bitmap
  selection with HNSW search — turns the assistant's "by conversation_id"
  filter from an overfetch hack into a first-class plan node.
