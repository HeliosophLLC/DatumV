# Memory, not conversations

## Thesis

DatumV's chat surface does not use threads. The user has one continuous
conversation that persists for the life of the catalog. "Where we are in the
conversation" is **detected**, not declared — every new user turn fires a
retrieval over the message graph and the catalog, and the working LLM context
is a **projection** over the whole history rather than a slice anchored to a
thread id.

This is a deliberate product position. The competitor isn't ChatGPT (a
sequence of message turns inside a thread); it's "second brain" tools (Roam,
Obsidian, mem.ai) where everything is searchable and gets connected. Except
instead of the user manually tagging and linking, the LLM does it via
retrieval over the graph. The chat-app shape is the *vehicle* for the memory
layer; the memory layer is the product.

Threading is an artifact of cloud-LLM economics: cloud chat necessarily
resets per-session because per-token cost forces small context windows.
Local + cheap retrieval makes the continuous-memory shape feasible. Building
threads on top of that machinery would re-impose the cloud constraint we're
specifically free of.

## Why this matters

- **No "new conversation" button is needed.** Topic shifts emerge from
  content. Returning to a topic emerges from content. The user never
  navigates between conversations because there's only one.
- **Compaction is the retrieval layer.** "Compacting" the working window is
  the same operation as "deciding what's relevant right now." Tier 3 of the
  compaction plan (retrieval-augmented working window) *is* this design.
- **Backlinks are first-class.** "Every time I used the `sales` table" is a
  query, not a feature. The same machinery handles "every time we discussed
  Q3 numbers" and "every conversation that referenced this CSV."
- **Proactive triggers get richer.** The agent knows "user uploaded
  `customers.csv` last week but never asked about it" — that's a candidate
  proactive prompt. "User has been asking about sales for the third time
  this week" — clustering surface.

## Scenario (canonical)

Mid-conversation about something unrelated, the user asks:

> What are last quarter's sales?

The agent's behavior:

1. Embed the new user turn (background or sync; for v1, sync to keep the
   pipeline simple).
2. **Two parallel retrievals fire:**
   - Over the **message graph** — past turns whose embeddings are close
     (or whose tokens overlap, via the FTS index).
   - Over the **catalog metadata** — table names, column names, inspection
     summaries, file metadata, any user-supplied descriptions.
3. Catalog retrieval finds: `sales.csv` was uploaded 7 days ago, inspected,
   has columns `date, amount, quarter, region`. 12 sample rows attached.
4. Working context assembled: system prompt + last N linear turns +
   retrieved message clusters (likely none for this question) +
   catalog-side retrieval block ("Available data: sales.csv …").
5. LLM generates a query, executes it (when tool-calling lands), returns
   the answer.
6. **Two edges are written:**
   - Vector for this turn appended to `message_embeddings`.
   - Row in `message_references` linking this turn to `sales`.

User then says:

> anyway, back to what we were saying about X

1. Embed. The new turn has high similarity to the pre-sales-detour cluster.
2. Retrieval pulls back the prior cluster. The sales turns don't score
   high; the working context shifts back naturally.
3. The sales subgraph stays in the graph, searchable, but doesn't bleed
   into the current context unless relevant again.

No thread switch, no UI affordance, no user action. The system "remembers
where we were" the same way a person does — content drives recall.

## Data model

Evolution order (each row is one migration):

```
v1 (shipped)        messages(id, role, content, model, ..., created_at)
v2 (next)           message_embeddings(message_id, embedding Float32[384])
v3 (next-ish)       message_references(message_id, target_kind, target_name, target_id?)
v4 (when needed)    message_links(from_id, to_id, kind)  -- branch/merge/rejoin
```

Notes:

- **No `conversations` table.** Ever. The "single conversation" is the entire
  `messages` rowset, ordered by `created_at`. If sub-grouping is ever needed
  for UX (saved searches, pinned topics), it's stored separately and is a
  *view*, not a partition.
- **No `parent_message_id` column.** The graph lives in `message_links`
  when it lands. Until then, message order is `created_at` ascending.
- **`message_references` is new and load-bearing for this design.** It's
  the message ↔ catalog-object edge. Without it, the "5 times you used
  this table" UX has nowhere to live.
- **Clusters are not stored.** They are query results. If precomputed
  cluster ids ever become useful (offline UMAP-style projection for a
  visualisation), they live in a separate `message_clusters` table that's
  rebuilt on demand. Not part of v2/v3.

### `message_references` shape (provisional)

```sql
CREATE TABLE message_references (
    message_id Int64 NOT NULL,
    target_kind String NOT NULL,   -- 'table', 'file', 'column', 'view', 'udf'
    target_name String NOT NULL,   -- catalog-resolvable name
    target_id Int64,               -- optional; for entities with stable ids
    created_at DateTime NOT NULL DEFAULT now()
);
```

No PK gate beyond `(message_id, target_kind, target_name)` — a single turn
may reference the same table multiple times in a row (different columns,
different queries) and storing each is harmless; if dedup matters, the
collapsed view is `SELECT DISTINCT`.

## Two retrieval indexes, not one

The thesis demands two separate retrieval surfaces feeding the same
context-assembly step:

1. **Message-graph index** — embeddings over `messages.content`. Used to
   recall "what we've said before."
2. **Catalog-metadata index** — embeddings over a synthesised descriptor
   per catalog object (table-name + column-names + sample-summary + any
   user-provided description). Used to recall "what data exists."

Both are MiniLM-grade ANN indexes plus an FTS sidekick. Both feed the
agent's pre-LLM context assembly. They're structurally parallel but the
corpora are different and they're rebuilt under different triggers:

| Index            | Built on                   | Rebuilt when                          |
|------------------|----------------------------|---------------------------------------|
| Message graph    | `messages` table           | Background, on each `AppendAsync`     |
| Catalog metadata | `system_tables` projection | Background, on CREATE/ALTER/DROP/REINDEX |

Both are sister-plans to [vector-search-index.md](vector-search-index.md)
and [fts-inverted-index.md](fts-inverted-index.md), which describe the
index *mechanics* (HNSW + inverted posting lists); this plan describes the
*application* — what gets indexed and what consumes the search results.

## Reference detection

How does a row land in `message_references`?

### Phase A (until SQL tool-calling): inferred

Parse fenced SQL blocks out of the assistant's response. Run the AST
through the catalog's table-name resolver. Every resolved table/column/UDF
becomes a reference row. Lossy (the LLM might refer to a table in prose
without writing SQL — "let me check sales" without code), but workable.

### Phase B (with SQL tool-calling): explicit

The agent's tool-call carries an explicit table-name list. Every executed
query writes a reference row per resolved target. Authoritative.

Phase A ships now; Phase B replaces it when tool-calling lands.
Reference rows from Phase A are not retroactively rewritten — the schema
of the row is the same in both cases, the *source of the row* differs.

## What this plan rules out

- A "new conversation" / "new chat" button. Not now, not later.
- A `conversations` table or `conversation_id` column.
- A `parent_message_id` column on `messages` (graph edges go in
  `message_links` when they land).
- A sidebar that lists threads/conversations. The sidebar (when it
  exists) lists *saved searches*, *pinned topics*, or *catalog objects* —
  artefacts, not threads.
- Pre-declared cluster ids stored on messages. Clusters are query-time.

## Open questions

1. **Recency bias in the retrieval rank.** Pure cosine similarity will
   surface old turns even when newer ones are equally good. A simple
   `score = cos_sim * exp(-age_days / tau)` with tau ≈ 14 days is a
   defensible v1; tune from there.
2. **Catalog-metadata index granularity.** One descriptor per table, or
   one per column? Tables-only is cheaper and probably sufficient for v1;
   column-level retrieval is a future refinement (helps "what column has
   the customer name?" queries).
3. **Cluster boundary detection.** Do we *ever* need to compute hard
   cluster boundaries (for e.g. a visualisation), or is fuzzy
   similarity-based retrieval enough? My current bet: fuzzy is enough
   for v1. Hard clusters become interesting only for analytic surfaces.
4. **Cross-machine continuity.** "One conversation" within a catalog is
   clean; what happens when a user has two machines? Out of scope for v1
   (single-host); plays into the eventual peer-compute story.

## Cold corpus import

The memory layer is most valuable when it has weight on day one. Existing
cloud-chat exports (Claude, ChatGPT, Gemini, etc.) are corpora the user
has already produced — tens or hundreds of threads of accumulated decisions,
code, and patterns of thinking — and currently can't reuse, because cloud
apps treat them as navigable threads that go cold and stay cold. Importing
them into the message graph + embedding pipeline makes the cold corpus into
substrate for *new* conversations.

This is a corollary of the thesis, not a separate feature: if memory is the
product and threads are write-only in practice, then *every existing
write-only thread the user has is latent memory waiting to be unlocked*.
Cold-corpus import is the migration path from cloud chat into DatumV.

### Schema impact

None. Exported turns map directly to `messages` rows:

- `role` from the export's role field (`user` / `assistant` / `system`).
- `content` verbatim.
- `model` from the export's metadata (`claude-3-5-sonnet`, `gpt-4`, etc.)
  — surfaces in the same column we already populate from the local LLM.
- `created_at` from the export's timestamp, **not** `now()`. Preserves
  recency-bias scoring in retrieval.
- `input_tokens` / `output_tokens` if the export provides them; null
  otherwise.

The `message_references` table absorbs any tool-call / table-reference
metadata the source export provides; otherwise references are inferred
later via the same Phase A SQL-block parsing.

### Operational concerns

- **Per-source ingester.** Each cloud app's export format is different:
  Claude's `conversations.json`, ChatGPT's `conversations.json` (different
  shape), Gemini's takeout. Each gets a small tool —
  `tools/ImportClaudeExport`, `tools/ImportChatGPTExport`, etc. — that
  parses and INSERTs into `messages`. Format is owned by the source, not
  by us; if a vendor changes their export shape, we update one tool.
- **Embedding backlog.** A user with 50k turns dropping in an export
  causes the embedding worker to process 50k rows. MiniLM CPU inference
  is ~50ms/turn → 40 minutes to backlog. Worker should be rate-limitable
  and resumable; not blocking the chat surface during catch-up.
- **De-duplication across imports.** A user who imports from Claude
  *and* ChatGPT will have content overlap (same task asked twice in
  different tools). De-dup is a future concern; v1 just keeps both with
  distinct `model` values.
- **Privacy.** Cloud exports may contain sensitive data. Import is local-
  to-catalog; nothing leaves the machine. No special handling needed
  beyond what the catalog itself already provides.

### Competitive framing

Cloud chat data is "hostage" — even with export, the JSON is dead the
moment it leaves the source UI because there's no other place to *use*
it. DatumV becomes the place where exported chat data is *more
useful than it was in the cloud app*, because retrieval works across it.
That makes export → DatumV a migration path toward us, not just a
backup. Filed as part of the thesis; build when the chat surface is ready
to consume the imported corpus.

## Build order

1. **MiniLM + `tasks.embed`** — register `all-MiniLM-L6-v2` ONNX, expose
   as a UDF. CPU inference, no VRAM contention with the chat LLM.
2. **`message_embeddings` migration + background embed worker** —
   subscribes to message inserts (or polls `WHERE embedding_id IS NULL`),
   computes and stores embeddings.
3. **HNSW index over `message_embeddings.embedding`** — sister-plan
   [vector-search-index.md](vector-search-index.md).
4. **Catalog-metadata index** — synthesise descriptor rows, embed them,
   index them. Sister-plan to (3); same machinery, different corpus.
5. **Pre-LLM retrieval step in `ConversationAgent`** — query both
   indexes, build context block, inject into prompt. Implements compaction
   tier 3 + the canonical scenario above.
6. **Inferred reference detection (Phase A)** — parse fenced SQL, write
   `message_references` rows.
7. **FTS index** — sister-plan [fts-inverted-index.md](fts-inverted-index.md).
   Hybrid rerank with vector retrieval.
8. **Explicit reference detection (Phase B)** — replaces (6) once SQL
   tool-calling lands.

Stops 1–4 are mechanical; step 5 is where the design ships as user-visible
behaviour. Steps 6–8 sharpen it; the basic shape works at step 5.
