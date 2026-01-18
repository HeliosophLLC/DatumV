# AI assistant + direct model invocation from the front-end

## Goal

Two use cases sharing one architecture:

1. **DevWeb dev-loop**: front-end invokes any model in the catalog directly
   (image → depth map, prompt → LLM response, image → caption, etc.) and
   hosts a docked AI assistant panel.
2. **Production inference orchestration service**: containerized DatumIngest
   with UDFs + models loaded at boot, exposed as an HTTP API where each
   request stages input data, runs SQL that chains UDFs / LLMs / vision
   models, and returns the result.

Both use cases reduce to: HTTP request → optionally stage typed input data →
run SQL referencing parameters and/or staged tables → stream typed results
back. There is no second paradigm to build.

## Core architecture decision: SQL is the API

Every model invocation — assistant turn, image-to-depth, batched OCR, chat
completion — is a SQL query routed through `/api/query/stream` with two
mechanisms for feeding data in:

```
POST /api/query/stream
{
  "sql": "SELECT depth_estimation(@img)",
  "parameters": { "img": { "kind": "Image", "bytes": "<base64>" } }
}
```

For multi-row inputs, the request also carries a `tables` map:

```
POST /api/query/stream
{
  "sql": "SELECT id, blip_caption(image) FROM uploads",
  "tables": {
    "uploads": {
      "columns": [{"name": "id", "kind": "Int32"}, {"name": "image", "kind": "Image"}],
      "rows": [[1, "<base64>"], [2, "<base64>"], ...]
    }
  }
}
```

Behind the scenes, `tables` entries are wrapped in `InMemoryTableProvider`
instances and registered in a **per-request child `TableCatalog`** that uses
the existing `Parent` chain (already used throughout the resolver). The
child catalog is disposed when the response completes. No mutation of the
shared catalog, no session state, no concurrent-request name collisions.

The same shape covers everything we want to do:

| Use case            | SQL                                                              |
| ------------------- | ---------------------------------------------------------------- |
| Depth map           | `SELECT depth_estimation(@img)`                                  |
| Salient cutout      | `SELECT u2net_cutout(@img)`                                      |
| Caption             | `SELECT blip_caption(@img)`                                      |
| OmniParser          | `SELECT omniparser(@img)`                                        |
| Single-shot LLM     | `SELECT llama_3_2(@prompt, temperature => @t)`                   |
| Embedding           | `SELECT minilm_embed(@text)`                                     |
| Batched captions    | `SELECT id, blip_caption(image) FROM uploads`                    |
| Chat with history   | `SELECT llama_3_2(chat_format(speaker, text)) FROM conversation` |
| Production pipeline | Any SQL the operator writes against staged tables + UDFs         |

Streaming tokens flow through the existing NDJSON sink for any of these.

### Why this works

- `ParameterBinder.Bind(query, dict<string, DataValue>)` already substitutes
  `@name` with literals before planning; engine-level support is done.
- `InMemoryTableProvider` is fully featured (scan, seek, schema inference,
  optional indexing) — we just need to register it in a per-request catalog.
- `TableCatalog.Parent` chain already exists; child catalogs delegate to
  the parent for everything they don't override.
- `DataValue` already covers all the kinds we'd pass (Image, UInt8 array,
  String, primitives, struct, array).
- `/api/query/stream` already streams NDJSON tokens from model invocations.
- Models stay in the catalog; same dispatch path for SQL queries and
  front-end calls.
- Tool calling (later) uses the same path: a tool dispatch is just another
  `/api/query/stream` round-trip with different SQL+params/tables.

### What's missing

`/api/query` and `/api/query/stream` currently only accept `{ sql, maxRows }`.
The enabling changes:

1. Extend the request shape with `parameters` and `tables` maps.
2. JSON ↔ DataValue mapper (including base64 for binary kinds).
3. Per-request child `TableCatalog` builder that registers an
   `InMemoryTableProvider` for each `tables` entry, parented to the
   global catalog, disposed on response completion.

That's it. No `CREATE TEMP TABLE` repair work, no session machinery, no
new RPC paradigm.

## Persisted conversation + uploads (m:1 attachments)

DDL/DML are now shipped (PR9.5–PR12), so chat state lives in real catalog
tables — no per-request staging required for the dev assistant. Three tables
get seeded into a workspace on first use:

```sql
CREATE TABLE conversations (
  id          BIGINT PRIMARY KEY IDENTITY,
  workspace   TEXT,
  title       TEXT,
  started_at  TIMESTAMP);

CREATE TABLE uploads (
  id           BIGINT PRIMARY KEY IDENTITY,
  workspace    TEXT,
  bytes        IMAGE,
  mime         TEXT,
  size_bytes   INT,
  uploaded_at  TIMESTAMP);

CREATE TABLE messages (
  id              BIGINT PRIMARY KEY IDENTITY,
  conversation_id BIGINT,        -- FK convention; constraint pending
  turn_index      INT,
  role            TEXT,          -- 'user'|'assistant'|'system'|'tool'
  content         TEXT,
  upload_id       BIGINT,        -- FK to uploads.id (nullable, m:1 in v1)
  tool_call_id    TEXT,          -- links role='tool' rows to assistant JSON
  created_at      TIMESTAMP);
```

The single-`upload_id`-per-message shape covers v1 chat ergonomics (one
image attached to one user turn). Multi-attachment ("compare these three
screenshots") is a v2 migration to a `message_attachments` join table — the
schema add is cheap once the FK feature exists, and starting m:1 is easier
to evolve from than retrofit constraints onto.

### Folding history into a prompt

Conversation context is built with ordinary SQL primitives — the
[chat-templates plan](chat-templates-as-functions.md) ships per-family
`templates.X_open` / `_msg(role, content)` / `_assistant_turn` scalar
functions:

```sql
SELECT models.llama_3_2(
  templates.llama31_open()
  || string_agg(templates.llama31_msg(role, content)) WITHIN GROUP (ORDER BY turn_index)
  || templates.llama31_assistant_turn(),
  templated => true)
FROM messages WHERE conversation_id = @conv_id;
```

No `chat_format` aggregate, no special model dispatch. A user could write
the same query against their own tables — the assistant has no privileged
path.

### Image-attached turn

When the user attaches an image with their question, the front-end runs three
inserts + the assistant query:

```sql
-- (1) Stage the image
INSERT INTO uploads (workspace, bytes, mime, size_bytes, uploaded_at)
VALUES (@ws, @img, 'image/png', @sz, now())
RETURNING id;

-- (2) User turn references the upload
INSERT INTO messages (conversation_id, turn_index, role, content, upload_id, created_at)
VALUES (@conv, @next_idx, 'user', @text, @upload_id, now());

-- (3) Auto-caption row — a separate message so string_agg picks it up naturally
INSERT INTO messages (conversation_id, turn_index, role, content, upload_id, created_at)
SELECT @conv, @next_idx + 1, 'system',
       'Image attached. Auto-description: ' || models.florence_2(bytes, 'describe'),
       @upload_id, now()
FROM uploads WHERE id = @upload_id;

-- (4) Assistant turn — same fold as before, now sees the caption
SELECT models.llama_3_2(...) FROM messages WHERE conversation_id = @conv;
```

Auto-caption model selection is a front-end choice — Florence-2 for general
descriptions, Phi-3.5-vision for targeted UI questioning, Moondream2 when
latency matters more than fidelity (cf. the shipped VLM comparison harness).
Any of them, or several, can write rows against the same `upload_id` — the
FK is the join key, not a uniqueness constraint.

### Direct invocation ("upload image, run model")

The assistant flow is one specialization of a more general pattern: any
front-end caller can stage data with `INSERT` and run a model query against
it. "I uploaded an image, run depth estimation" is just step (1) above plus
a single SELECT — no `messages` involvement at all:

```sql
INSERT INTO uploads (workspace, bytes, mime, size_bytes, uploaded_at) VALUES (...) RETURNING id;
SELECT models.depth_estimation(bytes) FROM uploads WHERE id = @upload_id;
```

### Why this supersedes per-request `conversation` staging

The original plan staged conversation history per request via a `tables`
JSON map and a per-request child catalog overlay. With DDL/DML shipped, that
mechanism isn't needed for the dev assistant — chat state survives reload,
history grows incrementally, and the front-end stops re-uploading the prior
turns each request. The per-request overlay still has a role for the
production inference service (stateless requests, ephemeral data, no catalog
mutation under load) — see v2 / later.

## v1 scope

The smallest slice that delivers (a) image-upload model demos, (b) a
working docked chat assistant, and (c) the per-request catalog overlay
that the production inference service depends on.

### In scope

1. **Persisted schema seed.**
   First-boot DDL for `conversations`, `messages`, `uploads` against the
   workspace catalog. Idempotent (skip when tables already exist).

2. **`parameters` map on `/api/query{,/stream}`.**
   Request gains `parameters: { name: { kind, value } }`. JSON-to-DataValue
   mapper handles primitives + base64 for Image / UInt8 array.
   `ParameterBinder` runs before planning (already exists). The `tables`
   JSON map / per-request catalog overlay is **not** in v1 scope — see v2.

3. **Chat-templating functions** — tracked separately in
   [chat-templates-as-functions.md](chat-templates-as-functions.md). Ships
   per-family `templates.X_open` / `_msg(role, content)` / `_assistant_turn`
   scalars and a `templated => true` override on `LlamaModel`.

4. **Image-attachment + auto-caption flow.**
   Front-end orchestrates upload + user-turn + caption-turn inserts and
   the assistant SELECT. Auto-caption defaults to a single configurable
   vision model (`models.florence_2` initial pick, with Phi-3.5-vision and
   Moondream2 selectable per workspace). No engine changes — vision models
   are already in the catalog.

5. **Right-docked AI assistant panel UI.**
   Sibling to `#group-container`, toggleable from the top toolbar. Markdown
   rendering, streaming token display, image-attach button, send button.
   Reuses existing CSS variables / layout patterns / NDJSON reader.

6. **IndexedDB last-known-good cache for conversation/messages.**
   Reuses the result-IDB pattern. Acts as a hydration cache for instant
   reload — the *source of truth* is the catalog tables. Schema:
   `conversations[workspaceId][convId] = { messages: [...] }`.

7. **Cursor-context auto-injection.**
   On submit, the front-end inserts a `role='system'` `messages` row with
   active tab name, cursor line/col, and ±10 lines around the cursor before
   firing the assistant SELECT. No model-side tool call needed for v1.

### Out of scope (deferred, see "v2 / later")

- Tool calling / agentic loop.
- m:n attachments (`message_attachments` join table). v1 ships m:1 — single
  `upload_id` column on `messages`.
- Per-request `tables` JSON map + child `TableCatalog` overlay. Now an
  inference-service concern — the dev assistant uses persisted tables.
- OmniParser integration (separate track).
- Multi-conversation per workspace.
- Native multimodal input to the assistant LLM itself (LLaVA-style). v1
  routes images through the vision-caption hop.
- FK constraint enforcement on `messages.conversation_id` /
  `messages.upload_id`. Convention only in v1 — the catalog supports the
  PRIMARY KEY side but cross-table FK constraints aren't yet a feature.

### Effort estimate

| Piece                                                                 | Time      |
| --------------------------------------------------------------------- | --------- |
| Schema seed (CREATE TABLE on first boot, front-end-driven)            | 0.25 day  |
| Extend `/api/query{,/stream}` with `parameters` + base64              | 0.5 day   |
| Chat-templates functions (separate plan, ~1.5d, **shared work**)      | —         |
| Image-attachment + auto-caption front-end flow                        | 0.5 day   |
| Right-docked panel UI + streaming + image attach                      | 1.0 day   |
| IDB conversation cache                                                | 0.5 day   |
| Cursor-context auto-inject (now a `messages` insert)                  | 0.25 day  |
| **Total (excludes chat-templates plan)**                              | ~3.0 days |
| **Total (with chat-templates plan)**                                  | ~4.5 days |

## Open issues / things to work through

1. **Default vision model for auto-caption.**
   Three candidates already wired (Florence-2, Phi-3.5-vision, Moondream2).
   Florence-2 is the proposed default — broad coverage, fast on CUDA, and
   the OCR-region prompt covers most "what's on screen" needs without a
   targeted question. Make the choice per-workspace settable so the user
   can swap to Phi-3.5-vision when reasoning over UI controls or to
   Moondream2 for latency. Decide a default during PR landing.

2. **Schema seeding ergonomics.**
   When does the front-end run the CREATE TABLE statements? Options: (a)
   on every workspace open (idempotent CREATE TABLE IF NOT EXISTS), (b)
   once at first assistant-panel toggle, (c) baked into the workspace
   create flow. (a) is the safest — paying nothing if tables exist — but
   needs IF NOT EXISTS to actually be a no-op. Verify against the shipped
   DDL.

3. **FK convention without enforcement.**
   `messages.conversation_id` and `messages.upload_id` are FK by
   convention only — the catalog doesn't enforce cross-table refs yet.
   Risk: front-end bugs leave dangling refs. Mitigation in v1: front-end
   never DELETEs uploads or conversations (just adds to history), so the
   only way to break refs is a partial INSERT failure mid-turn. Acceptable
   for v1; revisit when DELETE flows land.

4. **Wire format for binary data.**
   Base64 inside JSON is the obvious choice. If image sizes get large
   (>5 MB) we'll hit JSON parsing latency and want multipart instead. For
   v1, 512×512 PNGs at base64 are fine. Decision to revisit when actual
   image sizes become a problem.

5. **Cancellation.**
   `/api/query/stream` already supports cancellation via the existing
   AbortController plumbing. Assistant panel's "stop" button just aborts
   the in-flight fetch. Verify the `LlamaModel` honours the cancellation
   token mid-stream.

6. **Concurrent assistant requests.**
   Per-workspace serial-only? Or allow multiple in-flight (e.g. user fires
   off a depth-map call from a tab while the assistant is streaming)?
   Each request is a separate query-stream so they don't collide at the
   transport level, but a single GGUF model serialises at the executor
   level. Decide whether the panel UI guards against concurrent submits.

7. **Where the front-end gets the "current model name."**
   Probably a workspace-level setting + a small picker in the panel. The
   default is the first available LLM in `system_models` filtered by
   category=text. Enumeration via the existing catalog/models endpoint.

8. **Error shape the assistant panel needs.**
   `/api/query/stream` error events are JSON lines today; assistant UI
   just renders them as a red bubble. Make sure model load failures
   (CUDA missing, file not found) surface readably and don't kill the
   panel.

9. **Prompt size guards.**
   Conversation history grows unbounded as `messages` rows accumulate.
   Front-end trims before SELECT (drop oldest user/assistant pair until
   the formatted prompt fits `model.max_context_tokens / 2`) is the
   simplest v1 shape. A SCAN-based "running token count, drop while
   over budget" expression is the natural SQL-side alternative — defer
   until the front-end heuristic is shown to be lossy.

10. **Multi-statement request bodies.**
    The image-attachment flow is four separate `/api/query/stream`
    round-trips. Could be one round-trip if the endpoint accepted a
    statement list and chained the `RETURNING id` value into the next
    statement's parameters. v1 keeps it as four separate calls — cheap
    enough at the JSON level, and avoids inventing a parameter-chaining
    syntax. Revisit if latency becomes user-visible.

## v2 / later

- **Tool calling** with GBNF-constrained JSON output.
  Tools: `get_cursor_context`, `get_active_tab_sql`, `get_schema(table?)`,
  `run_query(sql)`, `read_results(tab_id)`, `caption_image(upload_id, prompt?)`.
  Tool dispatch is JS-side; each tool call becomes another
  `/api/query/stream` round-trip; the result is INSERTed as a `role='tool'`
  message and the model gets the next turn. ~2-3 days extra.

- **m:n attachments** via `message_attachments(message_id, upload_id, position)`
  for "compare these three screenshots" / multi-image reasoning. Migration
  from the v1 m:1 column is a backfill plus a column drop; the FK story
  needs to land first to make the join table worth it.

- **Per-request `tables` JSON map + child `TableCatalog` overlay** for the
  production inference service. Stateless requests, ephemeral data, no
  catalog mutation under load. Same shape as the original v1 plan;
  deferred because the dev assistant doesn't need it.

- **Production inference service deployment.**
  Container image with UDFs registered at boot from a startup script or
  bundle file. Same `/api/query/stream` endpoint as the dev assistant.
  Auth/rate-limiting added at the edge. Uses the per-request overlay
  above — direct API consumers post `parameters` + `tables` per call,
  no shared catalog state.

- **Multi-conversation per workspace** + conversation switcher in the
  panel. Schema already supports it (`conversations.id` is the
  partition key); just a UI piece.

- **Native multimodal** assistant when LLaVA or similar lands in the
  catalog (per the vision roadmap). Lets the assistant *see* a result
  cell directly instead of relying on a vision-caption hop.

- **OmniParser as a tool** the assistant can call, paired with a captioner
  for non-UI image content. Per the discussion that motivated this plan,
  this is the right shape — not the primary vision path.

- **Multipart upload** for large binary tables when base64-in-JSON gets
  expensive (>5 MB images).

- **FK constraint enforcement** on `messages.conversation_id` /
  `messages.upload_id`. Currently convention only.
