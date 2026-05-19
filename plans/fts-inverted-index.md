# Full-text inverted index

## Goal

Add a per-column inverted index to support full-text search over string columns
— first consumer is the LLM assistant's `messages.body` column, but the surface
is generic. Queries of shape

```sql
SELECT id, body, ts_rank(body, websearch_to_tsquery('error timeout')) AS score
FROM messages
WHERE body @@ websearch_to_tsquery('error timeout')
ORDER BY score DESC
LIMIT 20;
```

should return without scanning the table.

## Non-goals

- The index is **never** part of a PRIMARY KEY or a composite index. It is
  acceleration-only; PK enforcement stays on `MutableBPlusTree` /
  `MutableBPlusTreeBytes`.
- No multi-column FTS in v1 — one index covers one column. (Cross-column
  matches happen at the query level with `OR` or by indexing a generated
  concatenation column.)
- No phrase queries with positional postings in v1 (terms-only). Position lists
  are a v2 extension.
- No relevance feedback / query expansion / synonyms in v1.

## What "inverted index" means here

For each indexed text column, the index maps `term → posting list`, where a
posting is a `(chunkIdx, rowOffsetInChunk, termFreq)` triple referring back to
the underlying `.datum` file. At query time, the operator pulls the posting
lists for each query term, intersects/unions them, and either returns the rows
directly or scores them with BM25 first.

This sits alongside the existing per-column acceleration files
(`.datum-bptree-{column}`, `.datum-pkindex`, composite trees), in a new
`.datum-fts-{column}` sidecar.

## Architecture

### Layering

```
┌──────────────────────────────────────────────────────────────┐
│  SQL surface: @@, websearch_to_tsquery, ts_rank,             │
│               plainto_tsquery, to_tsquery                    │
├──────────────────────────────────────────────────────────────┤
│  FullTextSearchOperator (planner-injected SCAN replacement)  │
│  Hybrid rerank operator (later — reused by vector PR)        │
├──────────────────────────────────────────────────────────────┤
│  ITextSearchIndex                                            │
│    .Search(terms, mode) → posting iterator                   │
│    .DocLength(chunkIdx, rowOff) → int                        │
│    .DocCount, .AverageDocLength (for BM25)                   │
├──────────────────────────────────────────────────────────────┤
│  PostingListStore   (term → posting list lookup)             │
│  DocStatsStore      (per-doc length, total doc count)        │
├──────────────────────────────────────────────────────────────┤
│  IFullTextAnalyzer                                           │
│    .Tokenize(string) → IEnumerable<Token>                    │
│                                                              │
│  Default v1: Unicode word-boundary split + lowercase fold    │
│  + ASCII-stop-word list. Pluggable; Porter stemmer is one    │
│  swap away.                                                  │
└──────────────────────────────────────────────────────────────┘
```

`ITextSearchIndex` is **not** `IColumnIndex`. The unified column index
interface in [IColumnIndex.cs](../src/DatumV/Indexing/IColumnIndex.cs) is
value-keyed (`FindExact(DataValue)`, range scans, etc.); FTS is term-keyed and
returns documents, not value matches. Forcing it through `IColumnIndex` would
mean either lying about the contract (`FindExact("error")` returns rows
*containing* "error", not rows *equal* to "error") or watering the interface
down. New interface is the right call. The provider gets a parallel accessor:

```csharp
bool TryGetTextSearchIndex(string columnName, out ITextSearchIndex index);
```

added to [ITableProvider.cs](../src/DatumV/Catalog/ITableProvider.cs).

### Storage shape — Shape A (resolved)

Single dup-key B+Tree on top of `MutableBPlusTreeBytes` with
`allowDuplicates=true`. Key = `utf8(term) ‖ varint(chunkIdx) ‖ varint(rowOff)`,
value = `varint(termFreq)`. Range-scan by term prefix retrieves all postings.

- Pro: zero new file formats; rides the existing crash-safe COW.
- Con: term bytes repeated per posting (~3–5× larger than a packed posting
  list at scale); no skip-list — posting AND merges full lists.

Acceptable for the LLM-assistant target. Migration to Shape B (term dict +
posting heap with skip pointers) is filed as PR-FTS-D and hidden behind
`ITextSearchIndex` + `PostingListStore` — when it lands, nothing above
those interfaces moves.

For reference, Shape B would be: `.datum-fts-{column}.terms` keyed by term
with `(postingListPtr, postingListLen, docFreq)` payload + a separate
`.datum-fts-{column}.postings` append-mostly heap of delta-encoded varint
posting blocks with skip pointers every 64 entries (classic Lucene layout).

### Sidecar file layout

`.datum-fts-{column}` (Shape A v1):

```
┌────────────────────────────────────────────┐
│  Bytes B+Tree (MutableBPlusTreeBytes,      │
│  allowDuplicates=true)                     │
│    key   = utf8(term)                      │
│              ‖ varint(chunkIdx)            │
│              ‖ varint(rowOff)              │
│    value = varint(termFreq)                │
└────────────────────────────────────────────┘
```

Doc-length stats and corpus stats (total docs, average doc length) live in
the `.datum-manifest`. Manifest is the right home: it already carries
column-level stats used by the planner (see
[statistics_manifests.md](../docs/statistics_manifests.md)), and BM25 wants the
same lifecycle.

Piggyback on PR17's manifest v4 bump
([PR17 plan](../memory/project_pr17_auto_analyze.md)). `TextColumnStats`
slots in next to `TableTracking`:

```csharp
internal sealed record TextColumnStats(
    long DocCount,
    double AverageDocLength,
    long TotalTokenCount);
```

per FTS-indexed column. Coordinate scheduling so PR17 and PR-FTS-B don't
each land an independent format bump.

### Analyzer

`IFullTextAnalyzer` v1: stateless, registered per-index at create time, name
persisted in the manifest so REINDEX uses the same one.

```csharp
internal interface IFullTextAnalyzer
{
    string Name { get; }                          // "simple_en", etc.
    IEnumerable<Token> Tokenize(ReadOnlySpan<char> text);
}

internal readonly record struct Token(string Term, int Position);
```

Both `IFullTextAnalyzer` and `FtsAnalyzerRegistry` are `internal`. New
analyzers ship as PRs against DatumV. The manifest carries the
analyzer name string; opening an index whose analyzer name is no longer
registered is a hard error with a clear message ("REINDEX with a registered
analyzer or downgrade to a build that includes 'foo'").

V1 ships **one** analyzer — `simple_en`:
1. Unicode word-boundary segmentation via `System.Globalization.TextInfo`
2. Lowercase fold
3. Drop tokens shorter than 2 chars
4. Drop English stop words (small built-in list — ~50 words)
5. No stemming

This is deliberately the minimum that makes chat search not embarrassing.
Porter stemmer / language-aware stemmers / synonym filters are follow-ups; the
interface accommodates them without breakage.

Promotion to a public analyzer registry (third-party `IFullTextAnalyzer`
implementations registered in the host) or a SQL-level `CREATE TEXT
ANALYZER` is non-breaking on existing index files — the registry shape
stays the same; only the discovery layer changes. Deferred until there's
a concrete ask.

### Build / maintenance

The FTS index follows the same incremental-extend story as the per-column
B+Tree ([PR13 plan](../memory/project_pr13_incremental_index_plan.md)):

- **Initial build** — on `CREATE INDEX ... USING FTS`, scan the column,
  tokenize each row, insert postings into the bytes-tree. Persist analyzer
  name + corpus stats to manifest. Estimate cost: ~3-5× a regular B+Tree build
  per row (tokenization + multiple inserts per row).
- **INSERT path** — `DatumFileTableProviderV2`'s mutation hook tokenizes new
  rows and inserts postings keyed by the post-commit `(chunkIdx, rowOff)`.
  Updates doc-length running totals.
- **DELETE path** — leaves postings in place; SCAN filters by the
  same tombstone bitmap it already consults
  ([DatumFile GetRowCount is gross](../memory/project_datum_provider_getrowcount_gross.md)).
  Compaction happens during REINDEX. **Caveat:** doc count and average length
  drift after deletes; recompute on REINDEX or via the auto-ANALYZE worker.
- **UPDATE path** — at the page-COW level, treat as delete + insert: remove
  old postings (cheap because the tombstone-then-rewrite already happens),
  insert new ones for the new `(chunkIdx, rowOff)`.
- **REINDEX** — full rebuild; clean tombstones.

The tombstone-aware filter on read keeps the index from caring about
deletes in the steady state; the rebuild path mops up.

## SQL surface

Stay Postgres-flavored
([dialect anchor](../memory/feedback_postgresql_anchor.md)).

### DDL

```sql
CREATE INDEX idx_messages_body_fts ON messages USING FTS(body);

-- with explicit analyzer (default is 'simple_en')
CREATE INDEX idx_messages_body_fts ON messages USING FTS(body)
  WITH (analyzer = 'simple_en');

DROP INDEX idx_messages_body_fts;
REINDEX INDEX idx_messages_body_fts;
```

`USING FTS` is the divergence point. Postgres spells this
`USING GIN (to_tsvector('english', body))` — that's a much larger surface
(generic GIN + tsvector type + immutable expression indexes). We commit to a
purpose-built `USING FTS` keyword now and revisit GIN compatibility if/when
generalised inverted indexes show up for other column types.

### Query

PR-FTS-A ships `plainto_tsquery` only — AND of all terms after tokenization.
That's enough for the assistant's first cut.

```sql
-- v1 (PR-FTS-A): AND-only boolean match
SELECT id, body FROM messages WHERE body @@ plainto_tsquery('error timeout');
```

PR-FTS-B adds `websearch_to_tsquery` (web-search mini-language: bare terms
= AND, `or`, `"phrase"` treated as AND of phrase terms in v1 with no
positional match, `-term` for negation) and `to_tsquery` (explicit
operator syntax) alongside BM25 / `ts_rank`:

```sql
-- PR-FTS-B
SELECT id, body, ts_rank(body, websearch_to_tsquery('error -timeout')) AS score
FROM messages
WHERE body @@ websearch_to_tsquery('error -timeout')
ORDER BY score DESC LIMIT 20;
```

PR-FTS-A query AST nodes: `TextQueryAnd`, `TextQueryTerm`. PR-FTS-B adds
`TextQueryOr`, `TextQueryNot`. No phrase node in v1.

### Planner integration

A new `FullTextSearchOperator` is injected by the planner when:
1. `WHERE` contains `<col> @@ <tsquery>` and
2. `<col>` has an FTS index and
3. `<tsquery>` is a literal / parameter (not a per-row expression).

Otherwise we fall back to scan + per-row tokenize-and-match. The operator
emits `RowBatch`es of matching `(chunkIdx, rowOff[, score])`; `ScanOperator`'s
existing chunk-pruning interface gets a sibling "row picklist" entry point
(this already exists in spirit for PK lookups — confirm before sizing).

`ORDER BY ts_rank(...) DESC LIMIT k` becomes a top-k pull from the operator;
the operator can produce scored rows in score order natively, so this is free.

## File / code layout

```
src/DatumV/Indexing/Fts/
  IFullTextAnalyzer.cs
  SimpleEnglishAnalyzer.cs
  FtsAnalyzerRegistry.cs
  ITextSearchIndex.cs
  FtsIndexEntry.cs                    // (term, chunkIdx, rowOff, tf)
  FtsPostingKeyEncoder.cs             // utf8(term) ‖ varint(chunk) ‖ varint(off)
  FtsBytesTreeStore.cs                // Shape A: thin wrapper on MutableBPlusTreeBytes
  FullTextSearchIndex.cs              // ITextSearchIndex impl over the store
  Query/
    TextQuery.cs                      // AST
    WebSearchTsqueryParser.cs
    Bm25Scorer.cs

src/DatumV/Execution/Operators/
  FullTextSearchOperator.cs

src/DatumV/Catalog/
  ITableProvider.cs                    // + TryGetTextSearchIndex
  Providers/DatumFileTableProviderV2.cs // + open .datum-fts-{col} sidecars
  TableCatalog.cs                      // + CREATE INDEX USING FTS path

src/DatumV/Sql/
  Parser/*                             // USING FTS keyword + tsquery functions
  Ast/*                                // CreateIndexStatement gains an Fts variant
  Planner/*                            // FTS predicate detection + op injection

tests/DatumV.Tests/Indexing/Fts/
  SimpleEnglishAnalyzerTests.cs
  FtsPostingKeyEncoderTests.cs
  FullTextSearchIndexTests.cs
  Bm25ScorerTests.cs
  WebSearchTsqueryParserTests.cs
tests/DatumV.Tests/Execution/Operators/
  FullTextSearchOperatorTests.cs
tests/DatumV.Tests/Catalog/
  FtsIndexLifecycleTests.cs            // CREATE / INSERT / DELETE / REINDEX / DROP
tests/DatumV.Tests/Sql/
  FullTextSearchQueryTests.cs          // end-to-end @@ queries
```

## Phasing

| PR | Scope | LOC | Why this slice |
|---|---|---|---|
| **PR-FTS-A** | `IFullTextAnalyzer` + `SimpleEnglishAnalyzer` + `ITextSearchIndex` + Shape-A storage + `TryGetTextSearchIndex` provider hook + `CREATE/DROP INDEX USING FTS` DDL + `plainto_tsquery` (AND-only) + `@@` operator + lifecycle tests | ~900 | End-to-end vertical slice. AND-only boolean search is already useful for "does message X mention foo." Lets the LLM-assistant work start using it. |
| **PR-FTS-B** | BM25 scoring + `ts_rank` function + `TextColumnStats` slotted into PR17's manifest v4 + `websearch_to_tsquery` (web-search mini-language) + `to_tsquery` (operator syntax) + OR/NOT query nodes + scored top-k path | ~600 | Quality + richer queries. Coordinate the manifest bump with PR17 so we don't ship two versions back-to-back. |
| **PR-FTS-C** | Incremental maintenance: INSERT/UPDATE/DELETE hooks, tombstone-aware read, REINDEX rebuild path | ~400 | Required before the LLM assistant ships — chats are mutated constantly. |
| **PR-FTS-D** *(optional)* | Migrate to Shape B (term dict + posting heap with skip pointers) behind the existing `ITextSearchIndex` interface | ~700 | Defer until a workload measures pain. |
| **PR-FTS-E** *(optional)* | Trigram analyzer + `LIKE '%foo%'` planner integration | ~300 | Different analyzer, same plumbing. Big UX win for chat substring search. |

Stop after C for "FTS works in production." D and E are quality-of-life and
performance follow-ups.

Total to ship: ~1900 LOC, ~5–7 working days at typical PR cadence.

## Resolved decisions (2026-05-12)

1. **Storage shape:** Shape A (single dup-key B+Tree over `MutableBPlusTreeBytes`).
   Shape B filed as PR-FTS-D, deferred until a workload measures pain.
2. **Analyzer extensibility:** Sealed internal list — `IFullTextAnalyzer`
   and `FtsAnalyzerRegistry` stay `internal` to DatumV. New analyzers
   ship as PRs against DatumV. Manifest's `analyzer` field is a
   constrained name string. Promotion to a public registry (rung 2) or a
   SQL `CREATE TEXT ANALYZER` (rung 3) is non-breaking on existing index
   files — defer until there's a real ask.
3. **Query parser scope:** Start with `plainto_tsquery` only (AND of all
   terms) in PR-FTS-A. `websearch_to_tsquery` (web-search mini-language with
   AND/OR/NOT/negation) and `to_tsquery` (operator syntax) land in PR-FTS-B
   alongside BM25.
4. **`tsvector` as a column type:** Skip. FTS stays an index-time concept;
   `@@` takes `(text_column, tsquery)` directly. The legitimate use case
   ("different analyzers per language on the same column") is covered by
   allowing multiple FTS indexes on one column with different analyzers.
   Not a one-way door — we can add `tsvector` later without breaking
   existing indexes.
5. **Manifest version bump:** Piggyback on PR17's v4 bump. `TextColumnStats`
   slots in next to `TableTracking`. Schedule so PR17 and PR-FTS-B don't
   land independent format bumps.

## Hooks for what comes after

- **Vector search PR (next plan)** reuses:
  - `CREATE INDEX ... USING <kind>` parser surface
  - Provider sidecar-discovery pattern
  - Top-k operator shape from PR-FTS-B
  - Manifest column-stats slot
- **Hybrid rerank operator** is a small standalone PR that takes two ranked
  iterators (FTS scores + vector scores) and emits a Reciprocal Rank Fusion
  top-k. Lands after the vector PR; lets the assistant query both surfaces
  with one operator.
