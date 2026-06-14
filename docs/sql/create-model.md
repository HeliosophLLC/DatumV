---
title: CREATE MODEL
---

`CREATE MODEL` registers a model file (ONNX **or** GGUF) under the
`models` schema and binds it to a SQL-defined body that calls a dispatch
scalar like [`infer()`](#infer) (ONNX tensor I/O) or
[`llama_chat()` / `llama_generate()`](#llm-dispatch-scalars)
(GGUF text generation). Once registered, the model is callable as
`models.<name>(...)` from any query — exactly the same call shape as
the engine's [built-in models](../models.md).

The DDL is the user-facing surface for the inference layer described in
[`design-docs/onnx-inference.md`](../design-docs/onnx-inference.md).
Use it when you have an ONNX file (image / audio / structured models) or
a GGUF file (text-generation LLMs) you want to query without writing C#:
download the file (or install via the in-app Model Manager), point
`USING` at it, write a one-line body, and
`SELECT models.<name>(col) FROM table`.

## Syntax

```sql
CREATE [OR REPLACE] MODEL [IF NOT EXISTS] [models.]name(
    param TYPE [IS NOT NULL]
          [= default]
          [CHECK (boolean_expr)]
          [STEP decimal_literal]
          [UNIT 'string_literal']
          [COMMENT 'string_literal']
          [, ...]
) RETURNS TYPE [IS NOT NULL]
  [IMPLEMENTS TaskName]
  [USING 'path' [AS alias] [, 'path2' AS alias2, ...]]
[AS] BEGIN
    [DECLARE var TYPE [= expr] ;]
    [SET var = expr ;]
    [IF predicate statement [ELSE statement]]
    [WHILE predicate statement]
    RETURN infer(param)         -- ONNX
        -- OR llama_chat('s', messages, ...) — GGUF
        -- OR a call to models.<other>(...) — delegating model
END

DROP MODEL [IF EXISTS] [models.]name;

SELECT models.name(arg1, arg2) FROM ...;
```

A few non-obvious rules covered in detail below:

- The body is **always procedural** (`BEGIN … END`). There is no expression-body form.
- `RETURNS` is **required** — the planner needs a known scalar shape.
- The model **always** lives in the `models` schema — `CREATE MODEL my_model(...)` and `CREATE MODEL models.my_model(...)` are equivalent; any other schema qualifier is rejected.
- `USING` is **optional**: omitting it produces a *delegating* model that loads no weights of its own; the body produces its result by calling another model (or a UDF). See [Delegating models](#delegating-models).
- `USING` supports multi-session bundles via aliased entries (`USING 'a' AS x, 'b' AS y`) — the body dispatches by alias.
- Body-scoped scalars (`infer()`, `llama_chat()`, `llama_generate()`, `decode_decoder_only()`, …) are only callable from inside a model body. The planner refuses every other call site.
- **Straight-line bodies unlock batched dispatch.** When the body is a sequence of `DECLARE` and `SET` statements ending in a single `RETURN`, the engine runs each batch of rows through the model in one columnar pass — `infer()` and friends get their packed cross-row dispatch. Bodies that use top-level control flow (`IF`, `WHILE`, `BEGIN`/`END` blocks, `BREAK`/`CONTINUE`) fall back to per-row execution because columnar evaluation can't pick a single branch for an N-row column when different rows would take different branches. Nested expressions inside `DECLARE` / `SET` / `RETURN` — `CASE`, function calls, lambda invocations — don't break the property; only top-level control flow does.

## A complete example

```sql
-- Register a softmax model from disk.
CREATE MODEL softmax(x Float32[]) RETURNS Float32[]
    USING 'file:///opt/datum/models/softmax.onnx'
AS BEGIN
    RETURN infer(x)
END;

-- Call it.
SELECT models.softmax(
    [CAST(1.0 AS Float32), CAST(2.0 AS Float32), CAST(3.0 AS Float32)])
FROM (VALUES (1)) t(d);
-- yields approximately [0.09003, 0.24473, 0.66524]
```

Because the body is procedural, the same shape supports preprocessing or
postprocessing alongside the model dispatch:

```sql
CREATE MODEL classify(logits Float32[]) RETURNS Float32[]
    USING 'file:///opt/datum/models/classifier.onnx'
AS BEGIN
    DECLARE scores Float32[] = infer(logits);
    RETURN scores
END;
```

## The USING clause

`USING` declares the on-disk path of the model file. Two forms:

- **`file://<absolute>`** — the prefix is stripped and the rest is treated
  as an absolute path. Useful for tests and for files outside the host's
  models directory.
- **Relative path** — resolved against the host's [models directory](#models-directory).

```sql
-- Absolute (Linux/macOS):
CREATE MODEL m(x Float32[]) RETURNS Float32[]
    USING 'file:///home/me/onnx/m.onnx'
AS BEGIN RETURN infer(x) END;

-- Absolute (Windows):
CREATE MODEL m(x Float32[]) RETURNS Float32[]
    USING 'file://C:/onnx/m.onnx'
AS BEGIN RETURN infer(x) END;

-- Relative (resolves to <models-dir>/onnx/m.onnx):
CREATE MODEL m(x Float32[]) RETURNS Float32[]
    USING 'onnx/m.onnx'
AS BEGIN RETURN infer(x) END;
```

The file must exist at `CREATE MODEL` time — the registrar resolves and
stats the path during registration. The **inference session itself loads
lazily** on the first call from the body (via `LazyModelSessions`) so
process startup that rehydrates many SQL-defined models pays only
path-resolution + AST cost, not per-model session-load cost. Missing
files surface immediately as `FileNotFoundException`; invalid ONNX /
GGUF content surfaces on the first invocation that touches the session.

The runtime currently uses ONNX Runtime 1.25.1, which supports up to
opset 22 (for the `ai.onnx` domain). Newer ONNX exports may need to be
re-saved with an older `ir_version` / `opset_import` to load. A small
Python script will do it:

```python
import onnx
m = onnx.load("model.onnx")
m.ir_version = 10
m.opset_import[0].version = 22
onnx.save(m, "model.onnx")
```

GGUF files load via LlamaSharp / llama.cpp — no opset constraints; the
backend handles its own version detection from the file's metadata.

## Setup

### Models directory

DatumV reads model files from a single models directory. The resolution order is:

1. The path set in the **Settings** tab.
2. The `DATUMV_MODELS` environment variable.
3. Per-user default — `%LOCALAPPDATA%\Heliosoph.DatumV\models` on Windows, `~/.local/share/Heliosoph.DatumV/models` on Linux/macOS.

Pick a directory on NVMe or SSD storage with sufficient free space for the catalog you intend to install — model files run into the multi-GB range, and the first call to each model pays the session-load read cost.

### Directory layout

Each catalog-installed model lives in a per-id subfolder under the
models directory — single-file and multi-file models follow the same
pattern. Folder contents vary by model: most ship as a single
`.onnx` or `.gguf`, while multi-input bundles (Florence-2, ViT-GPT2,
encoder-decoder pairs) ship the encoder, decoder, and tokenizer
alongside the main weights. For multi-file entries the catalog's
`RelativePath` points at one anchor file inside the folder; the model
loader derives the rest from the parent directory.

```
<models-dir>\
  yolox_s\
    yolox_s.onnx
  Meta-Llama-3.1-8B-Instruct-Q4_K_M\
    Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf
  vit-gpt2-image-captioning\
    encoder_model.onnx
    decoder_model.onnx
    tokenizer.json
    ...
  florence-2-base-ft-fp16\
    vision_encoder_fp16.onnx
    embed_tokens_fp16.onnx
    encoder_model_fp16.onnx
    decoder_model_fp16.onnx
    tokenizer.json
    ...
```

Catalog-driven Python models follow the same per-id convention
(`bark-small/`, `bark/`, ...); the engine downloads the HuggingFace
repo's files directly into that folder and points the worker at it
via the `DATUMV_MODELS` env var, so there's no `~/.cache/huggingface/`
indirection.

### Catalog versioning

Catalog-installed models add one more layer: each model lives under a
per-`<catalog-id>` folder, with a per-version subfolder inside.

```
<models-dir>\
  all-minilm-l6-v2\
    2026-05-29\
      model.onnx
      vocab.txt
```

The catalog's installSql files write the explicit version segment in
their USING paths:

```sql
USING 'all-minilm-l6-v2/2026-05-29/model.onnx'
```

When the catalog ships a new version, the new installSql writes the
new segment. Each catalog row records its `(catalog_id, version)`
pair, so a registered model always names exactly one on-disk weight
file.

### Python toolchain (auto-managed)

Python-backed models (`bark_small`, `bark`, and anything else with
`kind: "python"` in the catalog) don't require any manual Python
install. The first time you Install one through the **Model Catalog**
tab, the engine does the following, in order, surfacing each step
inline on the model card:

1. **Download `uv`** from `github.com/astral-sh/uv/releases` into
   `%LOCALAPPDATA%\Heliosoph.DatumV\uv\`. One-time, ~15 MB.
2. **Install the requested CPython** (e.g. 3.11) via
   `uv python install` into `%LOCALAPPDATA%\Heliosoph.DatumV\python\`.
   One-time per major.minor version, ~30-60 MB.
3. **Create a per-model venv** at
   `%LOCALAPPDATA%\Heliosoph.DatumV\venvs\<catalog-id>\` and `uv pip install`
   the model's declared requirements (`torch`, `transformers`, etc.).
   uv hardlinks wheels from its global cache across venvs, so the
   second Python model installs in seconds.
4. **Download weights** through the same `HuggingFaceSource` pipeline
   ONNX models use, into `<models-dir>\<catalog-id>\`. The worker
   reads `os.environ["DATUMV_MODELS"]` at startup and loads via
   `from_pretrained(MODEL_DIR)` — no `~/.cache/huggingface/`
   indirection.

The engine never modifies your `PATH`, never touches your system
Python, and writes only to `%LOCALAPPDATA%\DatumV\` and the models
directory. Uninstall is a directory delete.

To see current state from SQL:

```sql
SELECT * FROM system.python_paths;          -- uv binary, python install root, venvs root
SELECT * FROM system.python_environments;   -- one row per provisioned venv
```

## Schema lockdown

CREATE MODEL **always** lands in the `models` schema. Both forms below
register the same `models.classify`:

```sql
CREATE MODEL classify(x Float32[]) RETURNS Float32[]
    USING 'file://...' AS BEGIN RETURN infer(x) END;

CREATE MODEL models.classify(x Float32[]) RETURNS Float32[]
    USING 'file://...' AS BEGIN RETURN infer(x) END;
```

Any other qualifier is rejected:

```sql
CREATE MODEL public.classify(x Float32[]) RETURNS Float32[]
    USING 'file://...' AS BEGIN RETURN infer(x) END;
-- error: CREATE MODEL public.classify: models must live in the
--        'models' schema. Use 'CREATE MODEL classify' or
--        'CREATE MODEL models.classify'.
```

Built-in models also live in `models.X`. The lockdown means every model
in your system is reachable as `models.<name>(...)` regardless of where
it came from.

## Struct return types

Models that emit multiple values can declare a structural return type
inline in `RETURNS`. The most common case is a model that surfaces depth
plus camera intrinsics plus a confidence map from a single inference
dispatch — historically that meant `RETURNS Struct` (opaque) and the
caller had to remember every field name. The structural form names
every field up front:

```sql
CREATE OR REPLACE MODEL depth_full(img Image)
  RETURNS Struct<
    depth      Array<Float32>(518, 518),
    confidence Array<Float32>(518, 518),
    intrinsics Array<Float32>(1, 1, 3, 3),
    extrinsics Array<Float32>(1, 1, 3, 4)
  >
USING 'depth-full.onnx'
AS BEGIN
    DECLARE outputs Struct = infer_outputs(/* ... */);
    RETURN {
        depth:      outputs['predicted_depth'],
        confidence: outputs['confidence'],
        intrinsics: outputs['intrinsics'],
        extrinsics: outputs['extrinsics']
    }
END;
```

See [Structs](struct.md) for field-access syntax (dot, bracket, `LET`-destructuring) and the catalog of named shapes (`BoundingBox`, `ScoredClass`, `Keypoint`, …) that can replace inline `Struct<...>` declarations.

### Annotation syntax

The body inside the angle brackets is a comma-separated list of
`name Type` pairs (PG-style) or `name: Type` pairs (curly-brace-style).
Both forms parse identically and canonicalise to the colon form for
display:

```sql
-- Either is accepted; the manifest stores the colon form.
RETURNS Struct<depth Array<Float32>>
RETURNS Struct<depth: Array<Float32>>
```

Field types are recursive — any annotation the type system understands
works as a field type, including array wrappers with explicit dim
suffixes (`Array<Float32>(518, 518)`) and nested struct shapes
(`Struct<inner: Struct<x: Int32, y: Int32>>`).

The bare `RETURNS Struct` form (no field list) keeps working for any
model where the field shape isn't worth committing to — runtime
semantics are identical.

### Runtime vs design-time

The annotation is **design-time metadata only**. At runtime, every
`Struct<…>` annotation collapses to the opaque `DataKind.Struct` —
the field shape itself isn't registered as a long-lived `TypeId` tied
to the model's catalog lifetime (see [Limitations](#limitations)).
What the structural form *does* unlock:

- **Language server hover** on the call site reads back the struct
  shape: `models.depth_full(img: Image) → Struct<depth: Array<Float32>(518, 518), …>`.
- **Field-access hovers** resolve to the declared field's kind —
  `LET d = models.depth_full(img); d.intrinsics` hovers as
  `Array<Float32>(1, 1, 3, 3)`.
- **Completion** after `d.` suggests `depth`, `confidence`,
  `intrinsics`, `extrinsics`.

The body's `RETURN { depth: …, intrinsics: … }` literal continues to
define the actual struct shape at execution; the annotation and the
return literal should agree, but the engine doesn't strictly enforce
it (a field declared in `RETURNS` but missing from the return literal
won't be reachable at runtime). Treat the annotation as the user-
facing contract; treat the return literal as the implementation.

## Body-scoped dispatch surface

The scalars in the table below are the model body's interface to the
loaded ONNX / GGUF sessions. Every one of them is **body-scoped** —
the planner's plan-time gate refuses any call site outside a
`CREATE [OR REPLACE] MODEL ... AS BEGIN ... END` body and surfaces the
constraint through `system.functions.body_scope = 'modelbody'`. See
the dedicated sections (and the linked function pages) for full
argument shapes and worked examples.

| Scalar | Backend | Surface | Documented in |
|---|---|---|---|
| `infer(arg [, shape])` <br/> `infer(alias, arg [, shape])` <br/> `infer(struct [, shapes])` | ONNX | Single-output tensor dispatch — single-input, multi-input (struct), and multi-session (aliased) forms. | [`§ infer()`](#infer) below |
| `infer_outputs(struct [, shapes])` <br/> `infer_outputs(alias, struct [, shapes])` | ONNX | Multi-output ONNX dispatch; returns a struct keyed by ONNX output names. | [`§ infer()`](#infer) below |
| `llama_generate(alias, prompt [, template] [, max_tokens] [, temperature])` | GGUF | Single-prompt text generation; implements `TextGenerator`. | [`§ LLM dispatch scalars`](#llm-dispatch-scalars) below |
| `llama_chat(alias, messages [, template] [, max_tokens] [, temperature])` | GGUF | Multi-turn chat; `messages: Array<ChatMessage>`; implements `ChatCompleter`. | [`§ LLM dispatch scalars`](#llm-dispatch-scalars) below |
| `decode_seq2seq(decoder, encoder_features, mask, prefix, eos, max_tokens, use_kv_cache [, embed_alias] [, suppress_above])` | ONNX | Encoder-decoder greedy decode (Whisper, T5, BART, TrOCR). | [`docs/functions/inference.md → decode_seq2seq`](../functions/inference.md#decode_seq2seq) |
| `decode_decoder_only(decoder, embed_tokens, prefix_embeds, eos, max_tokens)` | ONNX | KV-cached greedy decode for decoder-only LLMs spliced behind a vision/audio prefix (Moondream2-style VQA). | [`docs/functions/inference.md → decode_decoder_only`](../functions/inference.md#decode_decoder_only) |
| `mesh_from_triplane(nerf_alias, triplane, shape, resolution, isolevel, radius, chunk_size)` | ONNX | Chunked NeRF query loop + Marching Cubes; TripoSR-style triplane → `Mesh`. | [`docs/functions/inference.md → mesh_from_triplane`](../functions/inference.md#mesh_from_triplane) |

Helpers that are *not* body-scoped — tokenizers
([`tokenizer.encode_bert`](../functions/tokenization.md#tokenizerencode_bert),
[`tokenizer.encode_bpe`](../functions/tokenization.md#tokenizerencode_bpe), ...), image
preprocessors
([`image_to_tensor_chw`](../functions/image.md#image_to_tensor_chw),
[`imagenet_mean`](../functions/image.md#imagenet_mean), ...), audio
feature extraction
([`audio_to_log_mel`](../functions/audio.md#audio_to_log_mel)), and
postprocessors
([`yolox_postprocess`](../functions/inference.md#yolox_postprocess),
[`dbnet_postprocess`](../functions/inference.md#dbnet_postprocess),
[`mask_nms_planes`](../functions/inference.md#mask_nms_planes),
[`depth_map_to_image`](../functions/inference.md#depth_map_to_image),
...) — are callable from any `SELECT`, but appear almost exclusively
inside model bodies. Browse them in
[`docs/functions/`](../functions/string.md).

## `infer()`

`infer(arg)` dispatches the model's bound inference session and returns
the result. It is **only callable from inside a CREATE MODEL body** — a
plan-time gate refuses every other call site:

```sql
SELECT infer(1.0) FROM dual;
-- error: infer() is only valid inside a CREATE [OR REPLACE] MODEL
--        body. ...
```

For discoverability, `system.functions.body_scope` records which
functions have this constraint:

```sql
SELECT function_name, body_scope
FROM system.functions
WHERE body_scope <> 'none';
-- function_name | body_scope
-- infer         | modelbody
```

### Argument shape

The argument's runtime kind must match the session's input tensor's
declared element kind. Three element kinds are wired today:

| Element kind | Scalar accepted | Array accepted |
|---|---|---|
| `Float32` | yes | `Float32[]` |
| `Int32` | yes | `Int32[]` |
| `Int64` | yes | `Int64[]` |

A scalar argument is wrapped into a length-1 tensor; a primitive-array
argument flows through as a 1-d tensor matching the array length. For
sessions with a single dynamic dim (`[?, 224, 224, 3]` for example),
the dynamic dim absorbs whatever element count the input provides.

### Explicit shapes for multi-dynamic-dim inputs

When the session's input shape has **more than one dynamic dim**
(e.g. PaddleOCR's `[-1, 3, -1, -1]` where batch + H + W are all
dynamic), `infer()` can't pick a unique shape from the flat tensor
length alone. The 2-arg form passes the shape explicitly:

```sql
DECLARE prob Float32[] = infer(tensor, [CAST(1 AS Int32), CAST(3 AS Int32), rh, rw]);
```

The shape array's product must equal the input tensor's element count.

### Multi-input models

For sessions with more than one input tensor (BERT-family transformers,
multi-modal encoders), pass a **struct of tensors** with field names
matching the ONNX input names case-insensitively:

```sql
-- 1-arg form: shapes resolved from each input's TensorSpec (works when
-- each spec has at most one dynamic dim).
DECLARE result Float32[] = infer({
    input_ids:      ids,
    attention_mask: mask,
    token_type_ids: types
});

-- 2-arg form: parallel struct of explicit shapes per input. Required
-- when every input has multiple dynamic dims, as is typical for
-- BERT-family encoders ([batch, seq_len] on every input).
DECLARE result Float32[] = infer(
    {input_ids: ids, attention_mask: mask, token_type_ids: types},
    {input_ids:      [CAST(1 AS Int32), n],
     attention_mask: [CAST(1 AS Int32), n],
     token_type_ids: [CAST(1 AS Int32), n]});
```

The shipped `models.all_minilm_l6_v2`
model body is the canonical worked example — tokenize → multi-input
`infer` → mean-pool → L2-normalize.

### Return shape

The return value matches the session's output tensor's element kind.
The body's `RETURN` coerces the value to the model's declared
`RETURNS TYPE` if the kinds differ.

Sessions with **more than one output** are accepted; `infer()` returns
the **first** output. The convention HuggingFace optimum and most
transformer-to-ONNX exporters follow is to list the primary output
first (e.g. `last_hidden_state` ahead of `pooler_output` for BERT-family
encoders). To surface every output of a multi-output session, use
`infer_outputs(...)` (returns a struct of tensors keyed by ONNX output
name) and assemble the model's declared return shape with a struct
literal — see [Struct return types](#struct-return-types) for the
declared-shape contract and the `depth_full` example.

### What's deferred

- **Float16, Bool, UInt8** element kinds (the second is common for
  preprocessing flags; the third is the typical image-tensor kind).
  Each is one new branch when needed.

If you hit one of these limits, the runtime error names the limit
explicitly so you know what's missing.

(Multi-output sessions are *not* on this list — use `infer_outputs(...)`
plus a struct return shape; see [Struct return types](#struct-return-types).
Multi-session bundles are *not* on this list either — declare each
file with an alias via `USING 'a' AS x, 'b' AS y` and dispatch with
`infer('alias', value, [shape])`.)

## LLM dispatch scalars

GGUF-backed text generation has its own body-scoped dispatch surface,
parallel to `infer()` for ONNX:

| Scalar | Surface | Task contract |
|---|---|---|
| `llama_generate(alias, prompt, [template], [max_tokens], [temperature])` | `(String) → String` | `TextGenerator` |
| `llama_chat(alias, messages, [template], [max_tokens], [temperature])` | `(Array<ChatMessage>) → String` | `ChatCompleter` |

`alias` is the session alias declared in the model's `USING ... AS s`
clause. `messages` is an array of `ChatMessage` structs
(`{role, content}` where `role` is one of `'user'`, `'assistant'`,
`'system'`, `'tool'` per the chosen template's vocabulary).

The `template` argument **selects stop-sequences** (a safety net beneath
llama.cpp's native EOG check). Supported values: `'llama31'`, `'phi3'`,
`'zephyr'`, `'gemma'`, `'chatml'`, `'mistral'`, `'granite'`. The
actual prompt-formatting bytes come from llama.cpp's
`llama_chat_apply_template`, which reads the GGUF's embedded chat
template — this guarantees role markers tokenize as their special-token
ids, the way the model was trained to recognise them.

Canonical pilot example — Qwen 2.5 Coder 1.5B, two registrations sharing
one GGUF session:

```sql
-- ChatCompleter: owns the GGUF session via USING, accepts multi-turn input.
CREATE OR REPLACE MODEL qwen25_coder_1_5b_chat(
  messages    Array<ChatMessage>,
  max_tokens  Int32   = 1024 CHECK (max_tokens BETWEEN 1 AND 32768),
  temperature Float32 = 0.7  CHECK (temperature BETWEEN 0.0 AND 2.0)
) RETURNS String
  IMPLEMENTS ChatCompleter
  USING 'qwen2.5-coder-1.5b-instruct-gguf/2026-06-01/Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf' AS s
AS BEGIN
  RETURN llama_chat('s', messages, 'chatml', max_tokens, temperature)
END;

-- TextGenerator: a delegating view (no USING — see "Delegating models"
-- below). Single-prompt convenience surface.
CREATE OR REPLACE MODEL qwen25_coder_1_5b(
  prompt      String,
  max_tokens  Int32   = 1024 CHECK (max_tokens BETWEEN 1 AND 32768),
  temperature Float32 = 0.7  CHECK (temperature BETWEEN 0.0 AND 2.0)
) RETURNS String
  IMPLEMENTS TextGenerator
AS BEGIN
  RETURN models.qwen25_coder_1_5b_chat(
    [{ role: 'user', content: prompt }],
    max_tokens, temperature)
END;
```

Both scalars run on `LlamaSharpSession` resolved from the alias. A
session bound to an ONNX file would surface an `InvalidCastException`
at the call site — use `infer()` for tensor-graph models, the
`llama_*` scalars for GGUF.

## Delegating models

A `CREATE MODEL` *without* a `USING` clause is a **delegating model**:
the body produces its result entirely by calling into another model or
a UDF, with no weights of its own. Zero VRAM cost — the delegated
model's session is the only loaded copy.

Pattern: a small `TextGenerator` view over a richer `ChatCompleter`
underneath, illustrated by the `qwen25_coder_1_5b` example above.

Body validation works the same way as for USING-bound models: every
control-flow path must end in `RETURN`, the return shape must match
`RETURNS`, and any body that references a session alias raises a
clear error at runtime (`Session alias 'X' is not declared: this
model has no USING clause...`). Use a session-aliasing scalar
(`infer`, `llama_chat`, …) only from bodies that declare a `USING`
clause.

## Body statements

Inside a `BEGIN … END` block:

| Statement | Description |
|---|---|
| `DECLARE var TYPE [= expr]` | Declares a local variable, optionally initialised. Without `= expr`, the variable is a typed NULL. |
| `SET var = expr` | Reassigns an existing variable. |
| `IF predicate stmt [ELSE stmt]` | Conditional branch. Either arm can be a single statement or a `BEGIN … END` block. |
| `WHILE predicate stmt` | Loops while the predicate holds. |
| `BEGIN … END` | Block, primarily useful as the arm of `IF`/`WHILE`. |
| `RETURN expr` | Terminates the body and returns the expression's value. |

These are the same statement shapes [procedural UDFs](udf.md#procedural-udfs)
support, with two restrictions:

- `BREAK`/`CONTINUE` are not supported inside model bodies. Use
  `IF`/`RETURN` to short-circuit.
- A model body cannot contain a `SELECT` or other query statement —
  only direct expressions, `infer()`, and other function calls.

Every control-flow path through the body must end in `RETURN`.

## Shared syntax details

Most modifiers behave exactly the same as on `CREATE FUNCTION`. The
short version:

### OR REPLACE

```sql
CREATE OR REPLACE MODEL classify(x Float32[]) RETURNS Float32[]
    USING 'file:///opt/models/v2.onnx'
AS BEGIN RETURN infer(x) END;
```

Re-loads the new model file, registers the new descriptor, and disposes
the previously-bound session. In-flight queries that already captured
a reference to the old descriptor keep running on the old session
until they finish.

### IF NOT EXISTS

`CREATE MODEL IF NOT EXISTS` is a no-op when a model with that name
already exists. The original definition wins; the new file is **not**
loaded.

### IS NOT NULL on parameters

Append `IS NOT NULL` to a parameter to require a non-null argument. A
NULL at the call site throws an error before the body runs.

```sql
CREATE MODEL classify(x Float32[] IS NOT NULL) RETURNS Float32[]
    USING 'file:///opt/models/classify.onnx'
AS BEGIN RETURN infer(x) END;
```

### Default parameter values

A parameter declared with `= expr` becomes optional at the call site.
Defaults must sit at the tail of the parameter list (same rule as UDFs).

```sql
CREATE MODEL classify(x Float32[], threshold Float32 = 0.5)
    RETURNS Float32[]
    USING 'file://...'
AS BEGIN RETURN infer(x) END;
```

### Parameter constraints (CHECK)

A parameter may carry a `CHECK (expr)` clause that's enforced on every
call. The expression's free variable is the parameter name; passing a
value that fails the check raises a `FunctionArgumentException` with
the parameter name and the constraint description before the body runs.

```sql
CREATE MODEL clamp01(t Float32 = CAST(0.25 AS Float32)
    CHECK (t BETWEEN 0.0 AND 1.0)
    STEP 0.05
    UNIT '%'
    COMMENT 'Sigmoid-space threshold.')
    RETURNS Float32
    USING 'file:///opt/datum/models/clamp.onnx'
AS BEGIN RETURN infer(t) END;

-- t = 1.5 — rejected before any inference work happens:
-- clamp01(): parameter '@t': value 1.5 is outside [0.0, 1.0].
SELECT models.clamp01(CAST(1.5 AS Float32)) FROM (VALUES (1)) v(d);
```

The engine canonicalises recognised expression shapes into a typed
constraint that's both enforced at runtime and surfaced through the
function-catalog endpoint (`GET /api/functions/scalar`) as a
discriminated `kind` field — front-end forms can pick a slider, a
dropdown, or a generic text input without re-implementing the SQL
grammar:

| Expression                          | `kind`         | Renders as           |
|-------------------------------------|----------------|----------------------|
| `t BETWEEN 0.0 AND 1.0`             | `between`      | Range slider         |
| `t > 0` / `t >= 0`                  | `greaterThan`  | Bounded spinbox      |
| `t < 1024` / `t <= 1024`            | `lessThan`     | Bounded spinbox      |
| `v IN ('small','medium','large')`   | `in`           | Dropdown             |
| Anything else                       | `custom`       | Text + server-validate |

The `custom` shape is the escape hatch — any boolean expression that
doesn't match a canonical form lands here. It carries the original
expression AST, and the engine evaluates it at runtime against a
scope-bound evaluator so the parameter name (and any earlier parameter)
resolves to its bound value. Disjunctive predicates (`t = 7 OR t = 42`),
multi-parameter constraints (`max >= min`), and function-call predicates
(`is_finite(t)`) all work; the front-end falls back to a free-text input
plus server-side validation.

`NULL` always passes a `CHECK` (mirrors SQL `CHECK`-constraint semantics);
use `IS NOT NULL` on the parameter declaration if NULL should be rejected.

The optional `STEP`, `UNIT`, and `COMMENT` clauses carry UI hints that
don't affect runtime behaviour — they ride along on the catalog payload
so the executor form can pick a granularity, append a suffix, and show a
per-parameter tooltip.

**Defaults are validated at registration time.** A parameter that
declares both a default and a `CHECK` has its default evaluated and
checked once when `CREATE MODEL` runs — a default that already violates
the constraint fails the registration with a recognisable error, before
the ONNX file is even opened. This catches authored typos (`= CAST(0.025 AS Float32)`
intended as `0.25`) at install time instead of at the first call that
omits the override.

### DROP MODEL

```sql
DROP MODEL classify;
DROP MODEL IF EXISTS classify;
DROP MODEL models.classify;          -- explicit schema, equivalent
```

Dropping a model unregisters the descriptor, removes the scalar adapter
(so subsequent `models.classify(...)` calls fail with "not registered"),
and disposes the bound session. `DROP MODEL` rejects any non-`models`
schema qualifier with the same lockdown error as `CREATE MODEL`.

## Calling a model

A registered model is callable from any query as
`models.<name>(arg, ...)`. The argument count must match the declared
arity (or fall within the `min–max` range when defaults are present).

```sql
SELECT models.classify(image)            FROM photos;
SELECT models.classify(image, 0.7)       FROM photos;        -- override default
SELECT models.classify(image, 0.7) AS cls FROM photos LIMIT 10;
```

Because the body is procedural and `infer()` is non-deterministic from
the planner's perspective (an inference call can have subtle scheduler
non-determinism), the engine treats each call site as its own
evaluation — there is no CSE of `models.X(col)` across two call sites
in the same query. If you need to evaluate once and reuse, bind via
[`LET`](let-bindings.md):

```sql
SELECT classification, classification[0] AS top
FROM (
    SELECT models.classify(image) AS classification FROM photos
);
```

## Performance

Both built-in and SQL-defined models route through
`ModelInvocationOperator`; SQL bodies execute inside a
`ProceduralModelAdapter` that interprets the DECLARE/RETURN body once per
row. Dispatch chunk size comes from the engine's `BatchSizePolicy`,
which by default consults the model's per-host VRAM calibration curve to
pick the largest batch that fits in current free VRAM. Models without a
calibration curve dispatch at `batch=1` until one is recorded.

Multi-model queries (`SELECT a(file), b(file)` or `a(b(file))`) collapse
into a single multi-invocation `ModelInvocationOperator` so models run
one-at-a-time with leases released between invocations — preventing the
residency contention that otherwise lets ORT spill activations into
shared GPU memory. See
[`design-docs/onnx-inference.md`](../design-docs/onnx-inference.md#batch-sizing)
for the full calibration + dispatch model.

## Persistence

SQL-defined models persist across process restarts via the catalog
file (`.datum-catalog.json`, schema v6). Two row shapes:

- **Catalog-installed rows** (registered by an installSql shipped in
  `models/catalog.json`) persist `(catalog_id, catalog_version)`
  pointers. Rehydrate on the next process start resolves the install
  SQL by `(catalog_id, catalog_version)` against the live manifest, so
  edits to the on-disk SQL file flow through to the next restart
  without catalog surgery.
- **User-authored rows** (your own `CREATE MODEL` with no catalog
  parent) persist the verbatim SQL source text and re-execute it on
  startup.

`IInferenceSession` instances are not persisted (they hold native
handles) — the rehydrator replays the registration; sessions reload
lazily on the first call from each body.

## Introspection

The `system.models` virtual table surfaces every registered model — both
engine built-ins and your `CREATE MODEL` declarations:

```sql
SELECT name, kind, backend, file_size_bytes, status
FROM system.models
ORDER BY kind, name;
```

The schema-stable origin discriminator is the **`kind`** column:

| `kind`       | Meaning |
|--------------|---------|
| `builtin`    | Engine-baked entry from the model catalog (e.g. MobileNetV2, Llama, YOLOX). See [Models](../models.md). |
| `declared`   | A user-written `CREATE MODEL` registration. |

For declared rows, the metadata-rich columns (`license`,
`license_holder`, `category`, `modalities`, `source_url`,
`display_name`, `parameters`) are NULL — the engine has no upstream
metadata to surface. The columns that *are* populated for declared
rows: `name`, `backend = "sql"`, `file_name` (the raw `USING` path),
`file_size_bytes` (stat the resolved path), `status` (always
`"available"` because the session is bound at CREATE-MODEL time).

## Limitations

- **Single output from `infer()`.** A multi-output session can still
  be surfaced via `infer_outputs(...)` (returns a struct of tensors) +
  a [structural return shape](#struct-return-types); the limitation
  here is only that the 1-arg `infer()` form picks the first output.
- **Element kinds Float32 / Int32 / Int64 only** at the `infer()` boundary.
  Float16, Bool, UInt8, and the rest are runtime errors today; each is
  one branch to add.
- **Calibration auto-triggering is not yet wired** — curves must be
  populated by an explicit code-path caller of
  `CalibrationCoordinator.EnsureCalibratedAsync`. Until that exists,
  uncalibrated models dispatch at `batch=1`. See [Performance](#performance).
- **Schema is fixed.** Models always live under `models.X` — no
  user-chosen schema.
- **Body restrictions.** No `BREAK`/`CONTINUE`; no nested `SELECT`.
- **Streaming.** LLM token streaming via `CALL` (per the procedural-UDF
  follow-up) is on the roadmap but not in the SQL `llama_chat` /
  `llama_generate` path today — these scalars return the full
  response after generation completes.

## See Also

- [User-Defined Functions](udf.md) — the closest sibling DDL surface; same body shape, no `infer()` and no schema lockdown.
- [Procedural Functions and Procedures](procedural.md) — the broader procedural-body family this shares syntax with.
- [Models](../models.md) — the engine's built-in model zoo (`kind = 'builtin'` rows in `system.models`).
- [Schema Introspection](schema-introspection.md) — how `system.models` and `system.functions` fit into the broader catalog surface.
- [`design-docs/onnx-inference.md`](../design-docs/onnx-inference.md) — the design rationale for the inference layer, including the hoister Option A trade-off and why `infer()` is body-scoped.
