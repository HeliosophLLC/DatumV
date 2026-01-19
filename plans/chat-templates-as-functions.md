# Chat templates as SQL functions

## Goal

Expose per-LLM-family chat templating as ordinary scalar functions so that the
assistant turn (and any user-written multi-turn prompt) can be assembled in
plain SQL — no new aggregate, no model-aware machinery in the engine. The
[ai-assistant-and-direct-model-invocation plan](ai-assistant-and-direct-model-invocation.md)
collapses to "real `messages` table + `string_agg` + a per-message templating
function" once these functions exist.

The key shape this enables:

```sql
SELECT models.llama_3_2(
  templates.llama31_open()
  || string_agg(templates.llama31_msg(role, content)) WITHIN GROUP (ORDER BY turn_index)
  || templates.llama31_assistant_turn(),
  templated => true
)
FROM messages
WHERE conversation_id = $conv_id;
```

Three primitives per family — open / per-message / assistant-turn — plus a
`templated => true` override on the model so it doesn't double-wrap.

## Namespace and naming

`templates.X` as a sibling of `models.X`. Reasoning:

- `models.X` reads as "the model itself — you can run it." Mixing template
  helpers under that prefix muddles the dispatch surface (`system_models`
  enumeration, completion ranking, residency manager, etc.).
- `templates.` is shorter and more specific than `model_utils.`.
- Per-family naming (`templates.llama31_msg`, `templates.phi3_msg`, …) keeps
  the function statically resolvable for completion / typing / signature help.
  A single generic `templates.format_chat(family TEXT, role, content)` would
  work but would defeat that.

Function inventory per family:

| Function                              | Returns | Notes |
|---------------------------------------|---------|-------|
| `templates.{fam}_open()`              | TEXT    | BOS / preamble. Empty for most families because llama.cpp auto-adds BOS, but the function still exists for symmetry and future per-family preambles (system-prompt slots, etc.). |
| `templates.{fam}_msg(role, content)`  | TEXT    | Wraps one message in the family's role-header / turn-end syntax. Roles: `'user'`, `'assistant'`, `'system'`, `'tool'` (where the family supports them). |
| `templates.{fam}_assistant_turn()`    | TEXT    | The suffix appended after the last historical message that prompts the model to speak — e.g. Llama 3.1's `<|start_header_id|>assistant<|end_header_id|>\n\n`. |

Families covered (matches existing `LlamaChatTemplate` registry): `llama31`,
`phi3`, `zephyr`, `gemma`, `chatml`, `mistral`, `granite`. Seven families × 3
functions = 21 entries — implemented as one parameterized C# class registered
once per family, not 21 hand-coded scalar functions.

## Prerequisite refactor — `LlamaChatTemplate` becomes data

Today [LlamaChatTemplate.cs](../src/DatumIngest/Models/Llama/LlamaChatTemplate.cs)
exposes only `Format: Func<string, string>` — a single function that wraps a
user message and *bakes in* the assistant-turn trigger. That shape can't
serve per-role templating. Restructure it as data:

```csharp
public sealed record LlamaChatTemplate(
    string Open,                              // BOS / preamble (often "")
    Func<string, string, string> WrapMessage, // (role, content) -> chunk
    string AssistantTurn,                     // suffix that prompts the model
    IReadOnlyList<string> StopSequences)
{
    // Backwards-compatible single-message helper for one-shot inference:
    public string Format(string userMsg)
        => Open + WrapMessage("user", userMsg) + AssistantTurn;

    public static readonly LlamaChatTemplate Identity = new(
        Open: "",
        WrapMessage: (_, content) => content,
        AssistantTurn: "",
        StopSequences: []);
    // ... per-family entries updated to the new shape
}
```

The `Identity` template is the "raw mode" that lets `LlamaModel` accept
already-templated text without double-wrapping (see Phase 4).

## Phases / PRs

### PR1 — `LlamaChatTemplate` data shape + per-role support (~0.5d)

- Refactor record fields: `Open`, `WrapMessage(role, content)`,
  `AssistantTurn`, `StopSequences`.
- Update all seven family entries. Verify each per-role wrap against published
  reference template strings — Mistral folds `system` into `user`, Gemma uses
  `model` not `assistant`, etc. Document family-specific quirks where the
  encoding diverges from the obvious pattern.
- Add `LlamaChatTemplate.Identity`.
- Keep the legacy `Format(string userMsg)` as a thin helper so existing
  one-shot callsites in `LlamaModel` keep working unchanged.
- Tests: per-family round-trips against fixed reference strings (one
  multi-turn conversation per family, hand-verified once).

### PR2 — `LlamaModel` raw-mode (~0.25d)

- Add an `OptionalArg` `templated BOOLEAN` (default `false`). When `true`,
  skip the `_template.Format(promptText)` call at
  [LlamaModel.cs:355](../src/DatumIngest/Models/Llama/LlamaModel.cs#L355) and
  pass the prompt directly to the executor.
- This is preferred over registering an `llama_3_2_raw` sibling model entry
  because it avoids doubling every catalog row and keeps residency accounting
  unified (one set of weights, two dispatch modes).
- Tests: identical output for `models.llama_3_2(@p)` and
  `models.llama_3_2(templates.llama31_open() || templates.llama31_msg('user', @p) || templates.llama31_assistant_turn(), templated => true)`
  with seed pinned.

### PR3 — `templates.X` scalar functions (~0.5d)

- One C# class (`ChatTemplateFunctions` or similar under
  `src/DatumIngest/Functions/Templates/`) parameterized over a
  `LlamaChatTemplate` instance, exposing three `IScalarFunction`
  implementations. Registered once per family at catalog boot.
- `_open` and `_assistant_turn` are zero-arg, return `TEXT`, deterministic.
- `_msg` is `(TEXT, TEXT) -> TEXT`, deterministic; rejects unknown roles
  with a clear error rather than silently producing malformed output.
- Discoverable via `system.functions` (already exists) and namespace-prefix
  completion (already supported by `models.X`).
- Tests: each family's three functions exercised in a single SQL query that
  reproduces a known-good multi-turn prompt.

### PR4 — Docs + sample (~0.25d)

- New `docs/sql/chat-templates.md` with the canonical assistant-turn shape
  (the SQL at the top of this plan), the per-family function table, and a
  worked tool-call round-trip example.
- Cross-link from `docs/models.md` and the assistant plan.

**Total: ~1.5 days** to unblock the assistant. Combined with the
`parameters` HTTP wire-up (~0.5d, tracked in the assistant plan), the v1
assistant is ~2 days of engine work plus the front-end panel.

## What this plan does NOT include

- **Aggregate sugar** (`templates.llama31_chat(role, content)` that emits the
  full prompt in one call). v2 once the scalar primitives have shaken out.
  Worth waiting because the right ergonomics depend on whether users actually
  reach for the building blocks individually (e.g., to inject few-shot
  examples between history and the assistant trigger).
- **Tool-call template support per family.** Some families have dedicated
  tool-message roles with structured envelopes (Llama 3.1's `ipython` role,
  Granite's tool blocks). v1 ships the four common roles
  (`user`/`assistant`/`system`/`tool`) with `tool` rendered as a plain user
  message where the family lacks a native slot. Revisit when the LLM-initiated
  tool-call loop in the assistant plan goes in.
- **Token-budget trimming.** Conversation history will eventually need to
  drop oldest pairs to fit `model.max_context_tokens`. That's a SCAN-shaped
  problem (running token count, drop until under budget) and is the natural
  follow-on after the basic assistant works end-to-end. Tracked in the
  assistant plan's open issues.
- **Per-family preamble overloads with system prompt.** `templates.llama31_open(system_prompt TEXT)` returning the system block is a near-future addition once the no-arg form is in. Keeping v1 zero-arg keeps the function table small.

## Open issues

1. **System-prompt placement for Mistral.** Mistral has no native system
   role; the convention is to prepend system text to the first user message
   inside the same `[INST]` block. v1 `templates.mistral_msg('system', x)`
   options: (a) error — force the SQL author to fold manually; (b) emit as a
   bare prefix string with no wrapping, expecting concatenation with the next
   user message. (a) is more honest about the family limitation; (b) is more
   ergonomic. Lean (a) — surfacing the asymmetry is better than papering over
   it. Decide during PR3.

2. **Gemma's `assistant` → `model` role rename.** `templates.gemma_msg('assistant', x)`
   should accept `'assistant'` and emit `model` internally — users shouldn't
   need to know Gemma's quirk. Document in the family-quirks doc section.

3. **Stop-sequence exposure.** The model already strips stops from output, but
   if a future SQL pattern needs the per-family stop list (e.g., to validate a
   manually-built prompt) we'd add `templates.{fam}_stops()` returning
   `Array<TEXT>`. Not in v1.

4. **Whether `Identity` becomes a publicly registered template family.** If
   yes (`templates.identity_msg`, etc.), users could pass arbitrary text
   structured however they want and document the contract. Probably not worth
   it in v1 — `templated => true` on the model already covers the use case
   without bloating the function table.

5. **Per-row override for `templated`.** Already covered by the existing
   `OptionalArgKinds` mechanism in `LlamaModel` (cf. `temperature`,
   `max_tokens` at [LlamaModel.cs:347-352](../src/DatumIngest/Models/Llama/LlamaModel.cs#L347-L352)).
   Reuse the same pattern.
