# AI assistant + direct model invocation from the front-end

## Goal

Two use cases sharing one architecture:

1. **Web dev-loop**: front-end invokes any model in the catalog directly
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
completion — is a SQL query routed through `/api/query/stream`. The request
is `multipart/form-data` with one JSON part naming the SQL + parameters and
zero or more additional parts carrying binary payloads:

```
POST /api/query/stream
Content-Type: multipart/form-data; boundary=----X

----X
Content-Disposition: form-data; name="request"
Content-Type: application/json

{
  "sql": "SELECT models.depth_estimation($img)",
  "parameters": {
    "img": { "kind": "Image", "ref": "img_part" }
  }
}
----X
Content-Disposition: form-data; name="img_part"
Content-Type: image/png

<binary bytes>
----X--
```

Two parameter shapes:

- **Inline scalar**: `{ "kind": "String", "value": "hello" }` for primitives,
  numbers, booleans, dates — anything cheap to encode in JSON. The `value`
  field's JSON type matches the `kind`.
- **Binary reference**: `{ "kind": "Image", "ref": "img_part" }` for any
  binary kind (`Image` / `Audio` / `Video` / `UInt8` array). The `ref`
  field names a sibling multipart part in the same request; the server
  reads it as a stream into the corresponding `DataValue`.

There is no base64-in-JSON path. Binary ships as binary from day one, so
audio + video parameters work the moment those `DataKind`s do — no retrofit
when image sizes grow past the JSON-parsing cliff, and no double encoding
on every assistant turn.

For multi-row inputs, the request will also gain a `tables` map (deferred
to v2 — see "Out of scope"). When that lands, a `tables` entry uses the
same multipart shape: per-row binary cells reference part names instead of
inlining bytes.

Behind the scenes, `tables` entries (when v2 lands) are wrapped in
`InMemoryTableProvider` instances and registered in a **per-request child
`TableCatalog`** that uses the existing `Parent` chain. The child catalog
is disposed when the response completes. No mutation of the shared
catalog, no session state, no concurrent-request name collisions.

The same shape covers everything we want to do:

| Use case            | SQL                                                              |
| ------------------- | ---------------------------------------------------------------- |
| Depth map           | `SELECT depth_estimation($img)`                                  |
| Salient cutout      | `SELECT u2net_cutout($img)`                                      |
| Caption             | `SELECT blip_caption($img)`                                      |
| OmniParser          | `SELECT omniparser($img)`                                        |
| Single-shot LLM     | `SELECT llama_3_2(@prompt, temperature => @t)`                   |
| Embedding           | `SELECT minilm_embed($text)`                                     |
| Batched captions    | `SELECT id, blip_caption(image) FROM uploads`                    |
| Chat with history   | `SELECT llama_3_2(chat_format(speaker, text)) FROM conversation` |
| Production pipeline | Any SQL the operator writes against staged tables + UDFs         |

Streaming tokens flow through the existing NDJSON sink for any of these.

### Why this works

- `ParameterBinder.Bind(query, dict<string, DataValue>)` already substitutes
  `$name` with literals before planning; engine-level support is done.
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

`/api/query` and `/api/query/stream` currently only accept `{ sql, maxRows }`
as a JSON body. The enabling changes:

1. Accept `multipart/form-data` on both endpoints. Parse the named
   `request` part as JSON; expose remaining parts to the parameter binder
   keyed by part name.
2. Extend the JSON request shape with
   `parameters: { name: { kind, value | ref } }`.
3. JSON ↔ `DataValue` mapper for inline scalars (every primitive
   `DataKind`).
4. Binary-`ref` resolver: given a `ref` name, look up the corresponding
   multipart part stream and construct the appropriate `DataValue`
   (`Image` / `Audio` / `Video` / `UInt8` array).

That's it. No `CREATE TEMP TABLE` repair work, no session machinery, no
new RPC paradigm. The `tables` JSON map + per-request child `TableCatalog`
overlay is deferred to v2 (production inference service); the dev
assistant uses persisted catalog tables instead.

## Generalization: drag-and-drop files as named parameters

Once parameters can reference multipart parts, "attach a file to a query"
is a first-class front-end capability — not specific to the assistant
panel.

The shape:

- Drop a file into the editor or an "Attachments" side panel.
- Front-end stages the file in browser memory under a stable name (default:
  filename normalised to a SQL-friendly identifier; user-renameable).
- The Attachments panel lists each staged file with its inferred kind
  (`Image` / `Audio` / `Video` / `UInt8` array — derived from MIME), size,
  and original filename.
- The SQL editor auto-completes `@filename` against the panel.
- On query submit, every `$x` reference whose name matches a staged file
  becomes a `{ kind, ref: "<part_name>" }` entry in `parameters`, and the
  file ships as a multipart part with that name. Inline scalars
  (`$temperature`, `$text`) come from a separate per-query inputs row in
  the editor, sent as `{ kind, value }`.

This unlocks experimentation against models already in the catalog, with
no LLM-assistant prerequisite:

| Drop a... | Run                                                     |
| --------- | ------------------------------------------------------- |
| PNG       | `SELECT models.depth_estimation($photo)`                |
| PNG       | `SELECT models.florence_2($photo, 'describe')`          |
| WAV       | `SELECT models.whisper($clip)` (when wired)             |
| MP4       | `SELECT models.video_caption($scene)` (when wired)      |
| CSV       | `INSERT INTO surveys SELECT * FROM read_csv($data)`     |

The assistant panel's image-attach button is a thin wrapper over the same
mechanism: it pre-stages an attachment with a generated name and references
it in the assistant turn's `INSERT INTO uploads`. Dropping a file into the
editor and dropping one into the chat both end up at the same multipart
part + `ref` parameter shape.

Sequencing: this layer ships **first**. The LLM-assistant work that
follows reuses the multipart endpoint and the attachments panel without
modification.

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
FROM messages WHERE conversation_id = $conv_id;
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
VALUES ($ws, $img, 'image/png', $sz, now())
RETURNING id;

-- (2) User turn references the upload
INSERT INTO messages (conversation_id, turn_index, role, content, upload_id, created_at)
VALUES ($conv, $next_idx, 'user', $text, $upload_id, now());

-- (3) Auto-caption row — a separate message so string_agg picks it up naturally
INSERT INTO messages (conversation_id, turn_index, role, content, upload_id, created_at)
SELECT $conv, $next_idx + 1, 'system',
       'Image attached. Auto-description: ' || models.florence_2(bytes, 'describe'),
       $upload_id, now()
FROM uploads WHERE id = $upload_id;

-- (4) Assistant turn — same fold as before, now sees the caption
SELECT models.llama_3_2(...) FROM messages WHERE conversation_id = $conv;
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
SELECT models.depth_estimation(bytes) FROM uploads WHERE id = $upload_id;
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

Two phases. Phase A delivers a generalized capability — drag a file in,
reference it from any SQL — that immediately unlocks experimentation
against image / audio / video models already in the catalog. Phase B adds
the AI assistant features on top of the Phase A substrate.

### Phase A — multipart parameters + attachments panel (ships first)

A1. **Multipart `parameters` on `/api/query{,/stream}`.**
   Endpoints accept `multipart/form-data` with a JSON `request` part
   carrying `parameters: { name: { kind, value | ref } }`. Inline `value`
   for scalars; binary `ref` resolves against a sibling multipart part for
   `Image` / `Audio` / `Video` / `UInt8` array. `ParameterBinder` runs
   before planning (already exists). The `tables` JSON map / per-request
   catalog overlay is **not** in v1 scope — see v2.

A2. **Attachments panel + drag-and-drop staging.**
   Right-side panel (or an inline editor zone) shows files staged in
   browser memory: filename, inferred kind, size. Files are renameable
   to SQL-friendly identifiers. SQL editor `$name` references auto-complete
   against the panel; on submit, the front-end builds the `parameters` map
   and multipart parts automatically. No backend coupling beyond A1.

### Phase B — AI assistant features (reuses Phase A)

B1. **Persisted schema seed.**
   First-boot DDL for `conversations`, `messages`, `uploads` against the
   workspace catalog. Idempotent (skip when tables already exist).

B2. **Chat-templating functions** — tracked separately in
   [chat-templates-as-functions.md](chat-templates-as-functions.md) and
   already shipped. Ships per-family `templates.X_open` /
   `_msg(role, content)` / `_assistant_turn` scalars and a
   `templated => true` override on `LlamaModel`.

B3. **Image-attachment + auto-caption flow.**
   Reuses Phase A's attachments panel for the file-staging step.
   Front-end orchestrates upload + user-turn + caption-turn inserts and
   the assistant SELECT. Auto-caption defaults to a single configurable
   vision model (`models.florence_2` initial pick, with Phi-3.5-vision
   and Moondream2 selectable per workspace). No engine changes — vision
   models are already in the catalog.

B4. **Right-docked AI assistant panel UI.**
   Sibling to `#group-container`, toggleable from the top toolbar.
   Markdown rendering, streaming token display, image-attach button (a
   thin shortcut over Phase A's drag-and-drop), send button. Reuses
   existing CSS variables / layout patterns / NDJSON reader.

B5. **IndexedDB last-known-good cache for conversation/messages.**
   Reuses the result-IDB pattern. Acts as a hydration cache for instant
   reload — the *source of truth* is the catalog tables. Schema:
   `conversations[workspaceId][convId] = { messages: [...] }`.

B6. **Cursor-context auto-injection.**
   On submit, the front-end inserts a `role='system'` `messages` row with
   active tab name, cursor line/col, and ±10 lines around the cursor
   before firing the assistant SELECT. No model-side tool call needed for
   v1.

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

| Phase | Piece                                                            | Time      |
| ----- | ---------------------------------------------------------------- | --------- |
| A1    | Multipart `parameters` on `/api/query{,/stream}` (server)        | 0.5 day   |
| A2    | Attachments panel + drag-drop staging + `$name` auto-complete    | 0.75 day  |
|       | **Phase A subtotal**                                             | ~1.25 day |
| B1    | Schema seed (idempotent CREATE TABLE on first boot)              | 0.25 day  |
| B2    | Chat-templates functions (separate plan, **already shipped**)    | —         |
| B3    | Image-attachment + auto-caption front-end flow (reuses A2)       | 0.25 day  |
| B4    | Right-docked panel UI + streaming + image-attach shortcut        | 1.0 day   |
| B5    | IDB conversation cache                                           | 0.5 day   |
| B6    | Cursor-context auto-inject (`messages` insert)                   | 0.25 day  |
|       | **Phase B subtotal**                                             | ~2.25 day |
|       | **Total**                                                        | ~3.5 day  |

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

4. **Wire format for binary data — resolved.**
   Multipart from day one (see "Core architecture decision"). Binary
   parameters ship as raw multipart parts referenced by `ref`, not
   base64-in-JSON, so audio + video work the moment the corresponding
   `DataKind`s do. The 5 MB JSON-parsing cliff that motivated v2
   multipart in earlier drafts of this plan no longer exists, and there
   is no encoder/decoder layer to maintain.

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

- **FK constraint enforcement** on `messages.conversation_id` /
  `messages.upload_id`. Currently convention only.
