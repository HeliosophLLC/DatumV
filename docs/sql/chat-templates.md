---
title: Chat Templates
---

# Chat Templates

Per-LLM-family chat-template scalar functions in the `templates.X`
namespace. They expose the role-header / turn-end primitives each
instruction-tuned model expects so a multi-turn prompt can be assembled
in plain SQL — no aggregate, no model-aware machinery in the engine.

The canonical pattern, paired with `string_agg` over a `messages` table:

```sql
SELECT models.llama31_8b(
  templates.llama31_open()
  || string_agg(templates.llama31_msg(role, content))
       WITHIN GROUP (ORDER BY turn_index)
  || templates.llama31_assistant_turn(),
  NULL, NULL, true)              -- temperature, max_tokens, templated
FROM messages
WHERE conversation_id = $conv_id;
```

The trailing `true` is the `templated` positional override that tells
the model "this prompt is already wrapped — don't wrap it again." Pass
`NULL` for any earlier opt-args you don't want to override.

## Why it's three primitives

Every supported family encodes a turn-by-turn prompt with three pieces:

| Function | Purpose |
|----------|---------|
| `templates.{family}_open()` | BOS / system header. Often empty — llama.cpp auto-prepends BOS based on model metadata. Kept as a function for symmetry and future per-family preambles. |
| `templates.{family}_msg(role, content)` | Wraps one message in the family's role-header / turn-end syntax. Roles: `'user'`, `'assistant'`, `'system'`, `'tool'` where the family supports them. |
| `templates.{family}_assistant_turn()` | Suffix appended after the last historical message that prompts the model to speak. |

Concatenating an open + N messages + an assistant-turn produces the
fully-templated prompt the model expects. SQL's `string_agg` aggregates
the per-message wraps over a conversation table, ordered by turn index.

## Supported families

| Family | Models in this catalog | Tool role | System role |
|--------|------------------------|-----------|-------------|
| `llama31` | `llama31_8b`, `llama_3_2_3b` | `ipython` (auto-mapped from `'tool'`) | yes |
| `phi3` | `phi3_mini`, `phi35_mini` | folded into `'user'` | yes |
| `zephyr` | `tinyllama_chat` | folded into `'user'` | yes |
| `gemma` | `gemma_2b` | folded into `'user'` | folded into `'user'` |
| `chatml` | `qwen25_7b`, `qwen25_coder_7b` | yes (native) | yes |
| `mistral` | `mistral_7b` | rendered as a user turn | **errors** — fold into first user message |
| `granite` | `granite_3b` | yes (native) | yes |

### Family quirks

- **Llama 3.1**: tool-call return role is `ipython` on the wire. SQL
  authors pass `'tool'` and the template handles the rename. Other
  roles (`'user'` / `'assistant'` / `'system'`) pass through unchanged.
- **Gemma**: the assistant role is keyed as `model` inside the
  template. SQL authors pass `'assistant'` — no special-casing
  required. Gemma has no native system role, so system messages fold
  into user turns.
- **Mistral**: no native system role. `templates.mistral_msg('system',
  ...)` raises an error rather than silently folding the system text
  into the next user message — concatenate the system prompt into the
  first user content explicitly, e.g.
  `templates.mistral_msg('user', 'You are helpful.\n\n' || user_text)`.

## Worked example: persisted conversation

Combined with the conversation tables described in the
[AI assistant plan](../../plans/ai-assistant-and-direct-model-invocation.md),
a chat completion is one SQL query per assistant turn:

```sql
-- Build the templated prompt and dispatch it to the model
SELECT models.llama_3_2_3b(
  templates.llama31_open()
  || string_agg(templates.llama31_msg(role, content))
       WITHIN GROUP (ORDER BY turn_index)
  || templates.llama31_assistant_turn(),
  NULL, NULL, true)
FROM messages
WHERE conversation_id = $conv_id;
```

`templated => true` (positional `NULL, NULL, true` until keyword args
land in the parser) skips `LlamaModel`'s built-in single-message
`Format()` so the explicit `templates.X` composition isn't
double-wrapped.

## Tool-call round-trip (Llama 3.1)

Tool calls are an ordinary `'tool'` message inserted between the
assistant's request and the next user/assistant turn. The template
handles the role mapping; the SQL author treats `'tool'` like any
other role:

```sql
INSERT INTO messages (conversation_id, turn_index, role, content)
VALUES
  ($conv, 1, 'user',      'What is 6 * 7?'),
  ($conv, 2, 'assistant', '<call: multiply(6, 7)>'),
  ($conv, 3, 'tool',      '{"result": 42}'),  -- Llama 3.1 emits as ipython
  ($conv, 4, 'assistant', '');                -- assistant-turn slot

-- The dispatch from the assistant-turn row reads turns 1–3 and asks
-- the model to produce the final answer:
SELECT models.llama_3_2_3b(
  templates.llama31_open()
  || string_agg(templates.llama31_msg(role, content))
       WITHIN GROUP (ORDER BY turn_index)
  || templates.llama31_assistant_turn(),
  NULL, NULL, true)
FROM messages
WHERE conversation_id = $conv AND turn_index BETWEEN 1 AND 3;
```

The `'tool'` role surfaces in the wire as
`<|start_header_id|>ipython<|end_header_id|>` per Llama 3.1's spec —
the SQL author doesn't need to know the family quirk.

## What this is NOT

- **Not an aggregate.** The `templates.X` functions are scalar; they
  produce one string per row. The aggregation happens via SQL's
  `string_agg`, which already orders, deduplicates, and concatenates
  text. This keeps the engine free of model-aware machinery and lets
  users compose templates with any scalar SQL primitives.

- **Not a token-budget trimmer.** Conversation history grows
  unbounded as `messages` rows accumulate. When a conversation
  exceeds the model's context window, the front-end is responsible for
  trimming oldest user/assistant pairs before invoking the templated
  query. A SCAN-shaped "running token count, drop oldest while over
  budget" expression is the natural SQL-side alternative; not yet in
  the engine.

- **Not a prompt cache.** Two textually-identical templated prompts
  hash the same in the planner, but model invocation is
  nondeterministic (LLM with sampling temperature) and the planner
  marks model functions accordingly — CSE collapses repeated calls
  within a single query, not across queries.

## See Also

- [Models reference](../models.md) — the `models.X` namespace and
  per-family weights setup.
- [AI assistant plan](../../plans/ai-assistant-and-direct-model-invocation.md)
  — how the assistant panel uses these primitives end-to-end.
