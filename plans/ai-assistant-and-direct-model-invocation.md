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

## Conversation history as a regular table

The assistant's chat history is staged the same way as any other input:
as a regular table named `conversation` with columns `(speaker, text,
turn_index)` (or similar). The front-end uploads the prior turns each
request, then runs:

```sql
SELECT llama_3_2(chat_format(speaker, text) WITHIN GROUP (ORDER BY turn_index))
FROM conversation;
```

The key constraint the user articulated: **feeding history into the model
must work via existing SQL primitives — no different from what a user
could write themselves**. So the templating step is a SQL aggregate
(`chat_format(speaker, text)`), not a special model invocation shape.
The model itself stays a normal single-string-input function.

This means:

- **No multi-turn `LlamaChatTemplate` refactor in v1.** The aggregate
  produces the formatted prompt; the model receives a single string and
  uses its existing single-message template.
- The aggregate is just another scalar / aggregate function in the registry
  (likely needs a `chat_format` per template family, or one variadic with
  a model-name argument that pulls the template).
- Users in SQL-land can construct prompts the same way the assistant does.
  They could write their own filtering / reordering / few-shot injection
  before the aggregate without any new machinery.

## v1 scope

The smallest slice that delivers (a) image-upload model demos, (b) a
working docked chat assistant, and (c) the per-request catalog overlay
that the production inference service depends on.

### In scope

1. **Per-request child `TableCatalog` overlay.**
   Builds a child catalog with `Parent = global`, registers an
   `InMemoryTableProvider` per `tables` entry, returns an `IDisposable`
   that the request handler scopes with `using`. The query planner sees
   a unified view; the global catalog is never mutated.

2. **Parameter binding + table staging on `/api/query{,/stream}`.**
   Request gains `parameters: { name: { kind, value } }` and
   `tables: { name: { columns: [...], rows: [...] } }`. JSON-to-DataValue
   mapper handles primitives + base64 for Image / UInt8 array.
   `ParameterBinder` runs before planning (already exists). Tables are
   registered in the per-request catalog overlay before planning.

3. **`chat_format` aggregate function.**
   Takes (speaker, text) ordered by some key, produces a single formatted
   prompt string for the target model's chat template. Uses the existing
   `LlamaChatTemplate.Format` per template family. Model-aware variant or
   per-family functions — exact shape to be decided during implementation.

4. **Right-docked AI assistant panel UI.**
   Sibling to `#group-container`, toggleable from the top toolbar. Markdown
   rendering for assistant messages, streaming token display, input box,
   send button. Reuses existing CSS variables / layout patterns / NDJSON
   reader. Submits a SQL query over a `conversation` table that it stages
   from IDB on each turn.

5. **Per-workspace conversation history in IndexedDB.**
   Reuses the result-IDB pattern. Schema:
   `conversations[workspaceId][convId] = [{speaker, text, timestamp}]`.
   Single conversation per workspace for v1.

6. **Cursor-context auto-injection.**
   On submit, the front-end prepends a system row (or constructs a
   pre-context row in the conversation table) with active tab name,
   cursor line/col, and ±10 lines around the cursor. No model-side tool
   call needed for v1.

### Out of scope (deferred, see "v2 / later")

- Tool calling / agentic loop.
- OmniParser integration (separate track).
- Multi-conversation per workspace.
- Vision / multimodal input to the assistant LLM itself.
- Multi-turn `LlamaChatTemplate` refactor — superseded by `chat_format`
  aggregate.

### Effort estimate

| Piece                                                                 | Time     |
| --------------------------------------------------------------------- | -------- |
| Per-request child `TableCatalog` overlay + `IDisposable` scope        | 0.5 day  |
| Extend `/api/query{,/stream}` with `parameters` + `tables` + base64   | 0.5 day  |
| `chat_format` aggregate function (single template family for v1)      | 0.5 day  |
| Right-docked panel UI + streaming token render                        | 1.0 day  |
| IDB conversation history                                              | 0.5 day  |
| Cursor-context auto-inject                                            | 0.5 day  |
| **Total**                                                             | ~3.5 days |

## Open issues / things to work through

1. **Per-request catalog disposal correctness.**
   The child catalog must be disposed only after the streaming response
   completes — operators may still be scanning the in-memory tables when
   the first events have already flushed to the wire. Verify the lifecycle
   ties to the response stream's completion, not to the planner returning.

2. **Wire format for binary data.**
   Base64 inside JSON is the obvious choice. If image sizes get large
   (>5 MB) we'll hit JSON parsing latency and want multipart instead. For
   v1, 512×512 PNGs at base64 are fine. Decision to revisit when actual
   image sizes become a problem.

3. **Cancellation.**
   `/api/query/stream` already supports cancellation via the existing
   AbortController plumbing. Assistant panel's "stop" button just aborts
   the in-flight fetch. Verify the `LlamaModel` honours the cancellation
   token mid-stream.

4. **Concurrent assistant requests.**
   Per-workspace serial-only? Or allow multiple in-flight (e.g. user fires
   off a depth-map call from a tab while the assistant is streaming)?
   Each request is a separate query-stream so they don't collide at the
   transport level, but a single GGUF model serialises at the executor
   level. Decide whether the panel UI guards against concurrent submits.

5. **Where the front-end gets the "current model name."**
   Probably a workspace-level setting + a small picker in the panel. The
   default is the first available LLM in `system_models` filtered by
   category=text. Enumeration via the existing catalog/models endpoint.

6. **Error shape the assistant panel needs.**
   `/api/query/stream` error events are JSON lines today; assistant UI
   just renders them as a red bubble. Make sure model load failures
   (CUDA missing, file not found) surface readably and don't kill the
   panel.

7. **Prompt size guards.**
   Conversation history grows unbounded. Either the front-end trims
   before staging (drop oldest user/assistant pair until the formatted
   prompt fits `model.max_context_tokens / 2`), or `chat_format` itself
   does the trimming with a token-budget argument. Front-end-side is
   simpler for v1.

8. **`chat_format` API shape.**
   Open: one function per template family vs one variadic with a model
   name vs the aggregate looking up the template via the model catalog.
   Resolve during implementation when the existing `LlamaChatTemplate`
   wiring is in front of us.

9. **Multi-statement transactions / DDL.**
   The per-request child catalog is read-only-by-default for resolving
   parent tables. If a request submits multi-statement SQL that includes
   DDL, where does the DDL go? For v1, restrict request SQL to a single
   query expression — multi-statement is a v2 question.

## v2 / later

- **Tool calling** with GBNF-constrained JSON output.
  Tools: `get_cursor_context`, `get_active_tab_sql`, `get_schema(table?)`,
  `run_query(sql)`, `read_results(tab_id)`. Tool dispatch is JS-side; each
  tool call becomes another `/api/query/stream` round-trip; model gets the
  tool result and continues. ~2-3 days extra.

- **Production inference service deployment.**
  Container image with UDFs registered at boot from a startup script or
  bundle file. Same `/api/query/stream` endpoint as the dev assistant.
  Auth/rate-limiting added at the edge. The catalog-overlay mechanism is
  the same — just no DevWeb client, just direct API consumers.

- **Multi-conversation per workspace** + conversation switcher in the
  panel.

- **Native multimodal** assistant when LLaVA or similar lands in the
  catalog (per the vision roadmap). Lets the assistant *see* a result
  cell directly instead of relying on captioning.

- **OmniParser as a tool** the assistant can call, paired with a captioner
  for non-UI image content. Per the discussion that motivated this plan,
  this is the right shape — not the primary vision path.

- **Multipart upload** for large binary tables when base64-in-JSON gets
  expensive.

- **Multi-statement / DDL submissions** if a use case justifies it.
  Today everything that needs DDL is done outside the request boundary
  (UDFs registered at boot; data staged via `tables`).
