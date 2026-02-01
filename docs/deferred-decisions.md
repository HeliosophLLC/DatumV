---
title: Deferred decisions & v2 paths
---

A running ledger of v1 decisions that have a known v2 upgrade path. Comes here
when you hit a limitation in production, when an LLM-assistant query feels
worse than it should, or when you've got an hour and want to fix a backend
papercut.

Each entry names the limitation the way you'd phrase it when you ran into it,
the v1 behavior, the v2 path with a pointer to the relevant plan, the
*trigger* that should make you actually upgrade, and the compatibility story.
Entries are organised by feature area; within a feature, decisions are
ordered by "how likely is this to bite you first."

When an entry's v2 path ships, move it to "Resolved" at the bottom of its
section (don't delete — the resolved log is useful context for the next
person reading the v1 doc).

## How to read an entry

- **Limitation** — symptom as you'd describe it after hitting it.
- **v1** — what the engine does today.
- **v2 path** — concrete next step, with PR / plan reference where one exists.
- **Trigger** — measurable condition that should flip you from "deferred" to
  "scheduling this." If a trigger is "user complaint," say so explicitly.
- **Compat** — what breaks for existing data / queries when you do the
  upgrade. "None" means the on-disk format and SQL surface are unchanged.

---

## Full-text search

Plan: [fts-inverted-index.md](../plans/fts-inverted-index.md).

### 1. Inverted-index posting lists are bigger than they need to be

- **Limitation:** `.datum-fts-{column}` files grow ~3–5× larger than a
  packed posting list would.
- **v1:** Shape A — single dup-key `MutableBPlusTreeBytes` with
  `key = utf8(term) ‖ varint(chunk) ‖ varint(off)`. Term bytes are repeated
  per posting; no skip pointers, so AND of two long postings merges full
  lists.
- **v2 path:** Shape B — term dictionary + posting heap with skip pointers
  every 64 entries. Lucene-style. See PR-FTS-D in the plan. Hidden behind
  `ITextSearchIndex` + `PostingListStore` so nothing above the storage
  layer moves.
- **Trigger:** any one of — a single FTS sidecar crosses 1 GB; AND-query
  latency on the assistant's `messages.body` index crosses 50 ms p95;
  total FTS storage exceeds 10% of the source data files.
- **Compat:** new file format → existing indexes need REINDEX. No SQL surface
  change.

### 2. Stuck with `simple_en`; can't add a language-aware or stemmed analyzer

- **Limitation:** `running` and `runs` don't match `run`; non-English text
  goes through Latin-leaning rules.
- **v1:** Sealed internal analyzer list. One analyzer ships: `simple_en`
  (Unicode letter/digit runs, lowercase, 2-char min, ~50 English stop
  words, no stemming).
- **v2 path:** Three rungs, each non-breaking on existing index files:
  - **Rung 1.5** (cheapest): add more analyzers to the built-in list —
    Porter stemmer, German/French/Spanish, code-aware (preserves
    underscores/dots), CJK per-character. Each ~50–150 LOC.
  - **Rung 2**: make `IFullTextAnalyzer` + `FtsAnalyzerRegistry` public,
    allow host registration via DI. Mirrors `IModel` registration.
  - **Rung 3**: SQL-level `CREATE TEXT ANALYZER name AS PIPELINE
    (unicode_words, lowercase, stop_words('en'), porter_stem)` — token
    filters as data. Postgres ships this as `CREATE TEXT SEARCH
    CONFIGURATION`.
- **Trigger:** any non-English content; any "I searched for `running` and
  this doesn't find `runs`" complaint; any per-tenant analyzer
  customization need.
- **Compat:** None for rung 1.5 (just adds names). Public-API breakage
  considerations kick in at rung 2. Rung 3 is a new SQL statement,
  additive.

### 3. Query syntax is AND-only

- **Limitation:** Can't say "error OR warning"; can't exclude with `-`;
  no operator syntax.
- **v1:** `plainto_tsquery(text)` only — tokenizes input and ANDs all
  surviving terms. Implicit-AND `WHERE body @@ plainto_tsquery('error
  timeout')` works; everything else doesn't.
- **v2 path:** PR-FTS-B adds `websearch_to_tsquery` (PG web-search
  mini-language: bare = AND, `or`, `-term` for NOT, `"phrase"` treated
  as AND of phrase terms in v1) and `to_tsquery` (explicit `&` / `|` /
  `!` operators). New AST nodes `TextQueryOr`, `TextQueryNot`.
- **Trigger:** any user-facing search box where "OR" or "exclude" is a
  reasonable expectation. Likely from day one if the assistant exposes
  search at all.
- **Compat:** None — additive.

### 4. No phrase queries

- **Limitation:** `"connection refused"` matches any doc containing both
  "connection" and "refused," not just docs with them adjacent.
- **v1:** Positions are computed by the analyzer (`Token.Position`) but
  **not persisted** in the index sidecar. Storage is just
  `(term → chunkIdx, rowOff, termFreq)`.
- **v2 path:** Persist position lists per posting. Schema change to the
  bytes-tree value (varint count + varint deltas). Phrase queries
  become an intersection with positional constraints. ~400 LOC. New
  query AST node `TextQueryPhrase`.
- **Trigger:** any user complaint about precision; any RAG step where the
  retrieved context loses the exact phrase a user typed.
- **Compat:** On-disk format change → existing FTS indexes need REINDEX
  to gain phrase support. Indexes without positions still work for
  non-phrase queries.

### 5. No materialized tokenized column (`tsvector`)

- **Limitation:** Can't index two different analyzers' output of the same
  column under one shape. Can't write `WHERE body_tsv @@ ...` against a
  user-visible pre-tokenized column.
- **v1:** No `tsvector` type. FTS is purely an index-time concept; `@@`
  takes `(text_column, tsquery)` directly.
- **v2 path:** Two escape hatches:
  - **Cheap:** allow multiple FTS indexes per column with different
    analyzers (`CREATE INDEX ... USING FTS(body) WITH (analyzer =
    'porter_en')` and `... WITH (analyzer = 'code')` on the same column).
    The planner picks based on the tsquery's analyzer hint.
  - **Full:** add `tsvector` as a first-class type — column declarations,
    computed column generation, GIN-like generic inverted index machinery.
    Significant scope (~2000 LOC, parser/catalog/manifest reach).
- **Trigger:** users asking for PG-style `to_tsvector('english', body)`
  explicitly; needing per-row materialized lexemes for ranking or
  highlighting. The cheap path covers the multi-analyzer case alone.
- **Compat:** Cheap path is additive (new index option). Full `tsvector`
  is a new type + new catalog object, additive but bigger.

### 6. No multi-column FTS index

- **Limitation:** Searching across `messages.body` + `messages.title`
  requires either two indexes + `OR`, or a generated concatenation
  column.
- **v1:** One FTS index covers exactly one column.
- **v2 path:** Either generated column (`title_body GENERATED ALWAYS AS
  (title || ' ' || body) STORED`) + single FTS index — works today, just
  needs generated columns to ship — or a multi-column FTS index that
  internally maintains per-column posting lists with field markers.
- **Trigger:** assistant UI wants one search box covering multiple
  metadata columns. Generated columns are the cleaner answer; multi-col
  FTS is overkill until proven otherwise.
- **Compat:** Generated-column route reuses existing FTS format. Native
  multi-col is a new sidecar shape.

### 7. CJK gets coarse tokenization

- **Limitation:** `"机器学习"` becomes one token, not three.
  `Rune.IsLetterOrDigit` treats every CJK character as a letter, so any
  CJK run is one lexeme.
- **v1:** Documented limitation of `simple_en`. Test
  `Tokenize_CJKCharacters_TreatedAsLetters` locks the behavior.
- **v2 path:** Ship a `cjk_unigram` or `cjk_bigram` analyzer (rung 1.5
  from entry #2). Per-character or 2-char-sliding-window segmentation,
  trivial under 100 LOC. Real CJK segmentation (e.g. jieba-style) is
  much bigger.
- **Trigger:** any CJK content. Plausibly never for an English-first
  assistant; non-deferrable the moment you launch in a CJK market.
- **Compat:** None — new analyzer name.

### 8. Apostrophes split tokens

- **Limitation:** `"don't"` becomes `"don"` (and `"t"` dropped by length
  filter). Hurts recall on contractions.
- **v1:** Locked in by test
  `Tokenize_ApostropheSplitsTokens`. Side effect of the
  letter-run-only tokenization strategy.
- **v2 path:** A `uax29_en` analyzer that follows full Unicode word
  segmentation (UAX #29). Keeps `"don't"`, `"isn't"`, `"won't"`
  together. Either port the algorithm (~300 LOC) or pull in ICU bindings.
- **Trigger:** noticeable recall loss on chat corpora — likely real but
  not catastrophic because users tend to expand contractions when
  searching.
- **Compat:** None — new analyzer name.

### Resolved

*(none yet — entries land here as PRs ship)*

---

## Vector search

Plan: [vector-search-index.md](../plans/vector-search-index.md). All entries
are deferred-by-design pending the v1 ship.

### 1. WHERE filter + ORDER BY distance gets the wrong rows

- **Limitation:** `WHERE conversation_id = X ORDER BY embedding <=> q
  LIMIT 10` sometimes returns fewer than 10 rows when the
  conversation has many messages but few near the query vector.
- **v1:** Post-filter with overfetch — run HNSW with `k' = k * 4`, then
  apply `WHERE`, then truncate.
- **v2 path:** Pre-filter ANN — planner builds a permitted-nodes
  bitmap from `WHERE` predicates using existing per-column / bitmap
  indexes, passes it into HNSW search. PR-VEC-G in the plan.
- **Trigger:** observed result-count shortfalls in the assistant log;
  selectivity ratios where < 5% of rows match the filter.
- **Compat:** Operator API takes a new optional `IRowFilter` param;
  existing queries unaffected.

### 2. Vector dimension lives on the index, not the column

- **Limitation:** `INSERT INTO messages (embedding) VALUES
  ([1, 2, 3])` succeeds today even if every other row has 384-d
  vectors; only the *index* checks dim at insert time.
- **v1:** `CREATE INDEX ... USING HNSW(col) WITH (dim = 384)` puts the
  constraint on the index. No index = no dim check.
- **v2 path:** Column-typed `Float32[N]` or pgvector-style `vector(N)`.
  Catalog enforces on every INSERT/UPDATE regardless of index. PR-VEC-I.
- **Trigger:** any embedding column without an FTS index that gets
  garbage data; user request for `vector(N)` syntax for PG parity.
- **Compat:** Parser/catalog change; existing `Float32[]` columns stay
  untyped-dim and can be opted in.

### 3. Storage is float32 everywhere

- **Limitation:** A 1M × 384-dim index = 1.5 GB of vectors. Doubles RAM
  pressure during HNSW build.
- **v1:** Float32 vectors stored inline per node.
- **v2 path:** Float16 storage (half the bytes, ~negligible recall
  loss) or int8 scalar quantization with re-rank against float32 source
  (~quarter the bytes, small recall hit). Header flag selects precision.
  PR-VEC-H.
- **Trigger:** any vector index crosses 10 GB; build pressure
  noticeable on host RAM.
- **Compat:** New header field; existing indexes default to float32 and
  keep working.

### 4. Cosine pre-normalises at insert

- **Limitation:** Reading the raw stored vector via the vector index
  gives a normalised vector, not the original. This is fine for our
  uses but surprising if anyone reads the sidecar directly.
- **v1:** Cosine indexes divide each vector by its norm before storing;
  cosine query collapses to negative dot product. Header flag records
  the normalisation.
- **v2 path:** Store raw + compute norm at query time (slower per
  query, more flexible). Alternatively, expose source vectors via the
  source column always (which is what we already do — the index is
  opaque). The deferral is fine; documenting in case future tooling
  needs raw stored vectors.
- **Trigger:** debug tooling reading the sidecar; need to support
  per-query metric override on a single index.
- **Compat:** Header flag distinguishes; existing indexes interpret
  correctly.

### 5. Deletes degrade recall

- **Limitation:** Heavy-delete workloads accumulate orphan nodes in
  the HNSW graph; recall drops because deleted nodes still pull the
  greedy walk into dead areas.
- **v1:** Mark-delete via a tombstone bitmap. Graph topology
  unchanged. Query filters deleted nodes from the result set.
- **v2 path:** Auto-trigger REINDEX once deleted fraction crosses a
  threshold (~20%). Or proper graph repair (re-link neighbours of
  deleted nodes); much more complex.
- **Trigger:** observed recall drop on high-churn corpora.
- **Compat:** Auto-REINDEX is invisible; graph repair is an internal
  format detail.

### Resolved

*(none yet — vector search not started)*

---

## Cross-cutting

### 1. Manifest format bumps need coordination

- **Limitation:** Multiple in-flight features (PR17 auto-ANALYZE,
  PR-FTS-B BM25 stats) each want a `.datum-manifest` v-bump. Shipping
  them independently means two consecutive format versions.
- **v1:** Coordinate so format-bumping PRs share a single version
  increment when scheduled close together. PR-FTS-B piggybacks on
  PR17's v4.
- **v2 path:** Either keep coordinating ad-hoc, or move to additive
  field-tagged sections that don't require a version bump. The latter
  is a real refactor (~500 LOC) but pays off the moment three
  independent features want manifest fields.
- **Trigger:** scheduling collision where two PRs want a bump and one
  has to wait.
- **Compat:** Refactor is on-disk transparent — readers tolerant of
  unknown tags treat them as forward-compat.

### 2. `MutableBPlusTreeBytes` dup-key tie-breaker isn't actually applied

- **Limitation:** The tree's docstring claims duplicate-key entries are
  sorted by `(Key, ChunkIndex, RowOffsetInChunk)`, but `InsertIntoLeafAndPropagate`
  uses `BinarySearchInsertPosition` which only sees the byte key — new
  duplicates land *before* existing ones (newest-first within a key).
- **v1:** Each consumer sorts results themselves. Visible in
  `BPlusTreeContractTests.FindAll_WithDuplicates_ReturnsAllMatches`
  (explicit `.OrderBy`) and `FullTextSearchIndex.FindPostings`
  (explicit `Array.Sort` post-`FindPrefix`).
- **v2 path:** Either (a) fix `BinarySearchInsertPosition` to take the
  full `BytesIndexEntry` and do the (chunk, row) tie-break properly, or
  (b) change the docstring to match reality and require callers to sort.
  (a) is cleaner — the docstring's promise is the right contract.
- **Trigger:** any new consumer wants in-order traversal without
  per-call sort overhead; or someone hits the discrepancy and gets
  confused.
- **Compat:** Fix in (a) is internal — sort order changes but already-
  consuming code that sorts itself is unaffected. Existing
  `.OrderBy(...)` calls become dead.

### 3. Hybrid rerank not yet wired

- **Limitation:** FTS and vector search return ranked iterators
  separately. Combining them ("rank by BM25 score + cosine distance,
  pick top 10") requires manual SQL.
- **v1:** No hybrid operator. PR-VEC-F is the placeholder.
- **v2 path:** Reciprocal Rank Fusion operator — takes two ranked
  iterators, emits the RRF top-k. ~250 LOC. Lands after both FTS and
  vector are in.
- **Trigger:** assistant RAG quality measurable on a benchmark; the
  point where pure-vector or pure-FTS leaves obvious wins on the table.
- **Compat:** Additive operator + scalar function `rrf_combine(...)`.
