---
title: Models
---

DatumV ships with a catalog of pre-trained ML models — object detectors, depth estimators, segmenters, captioners, embedders, transcribers, image generators, and LLMs. Every model is invoked the same way: as a SQL function in the `models.X` namespace.

```sql
SELECT models.yolox_s(image)        FROM photos;
SELECT models.midas_small(image)    FROM photos;
SELECT models.whisper_base(clip)    FROM audio_clips;
```

This page covers the conceptual surface — how `models.X(...)` dispatches, the shapes its outputs come in, the install lifecycle, and what's in the catalog at a category level. For the live list of models in your installation — versions, sizes, licenses, install status — open the **Model Catalog** tab.

## How `models.X(...)` dispatches

A `models.<name>(args)` call resolves to a registered model in the `models` schema, then runs the model's body for each row. Two registration paths land entries in the same schema:

- **Catalog models** — pre-curated entries installed through the Model Catalog tab. Each install runs the entry's `installSql` to register the model; the result is a `system.models` row with `kind = 'declared'`.
- **User-declared models** — registrations a user wrote with [`CREATE MODEL`](sql/create-model.md). Same `models.X(...)` call shape, same dispatch path.

The call is row-by-row from the user's perspective but **batched at the engine layer** — the planner groups model invocations into batches, runs each batch through the model in a single pass, and scatters the results back to their row positions. Manual batching is never needed in SQL.

## Output shapes

Models return ordinary DataValues. The shape depends on the model's task.

| Output shape | Used by | Example |
|---|---|---|
| `Image` | classifiers, depth estimators (most), image generators | `models.midas_small(img)` → depth Image |
| `Float32[]` | embedders, classifiers (logits / probabilities) | `models.all_minilm_l6_v2(text)` → 384-vec |
| `String` | speech-to-text, captioning, LLMs | `models.whisper_base(clip)` → transcript |
| `Array<Struct>` | object detectors, multi-instance models | `models.yolox_s(img)` → bbox + label per detection |
| `Struct<…>` | depth estimators that expose confidence / pose | `models.depth_anything_v3_large_full(img)` → depth + confidence + extrinsics + intrinsics |

Struct and array outputs slot into normal SQL: `CROSS JOIN unnest(detections)` expands a detection array into one row per detection; `struct.depth` pulls a single field out. See [Person crops with YOLOX](examples/yolox-person-crops.md) and [Same input, four depth models](examples/depth-comparison.md) for worked examples.

### `IMPLEMENTS` and task interfaces

Many models declare an `IMPLEMENTS <TaskName>` clause that names the role they play — `DepthEstimator`, `Detector`, `Classifier`, `Embedder`, `Transcriber`, and so on. Models implementing the same task expose the same input/output shape, so they can be swapped at the SQL level without rewriting downstream code. Swapping `models.midas_small` for `models.dpt_large` is a column rename, not a pipeline change.

## Lifecycle

The lifecycle of a catalog model:

1. **Discover** — browse the Model Catalog tab. Each entry shows description, size, license, and supported tasks.
2. **License** — if the model's license requires acceptance (SDXL, Llama, anything else flagged `requiresAcceptance`), the full license text appears in a modal before install. Most catalog models are MIT or Apache 2.0 and install without prompting.
3. **Download** — weights stream to the models directory. Progress shows in a chip in the bottom-left corner; an OS notification fires when each completes. Failures need to be restarted manually from the chip.
4. **Register** — the catalog runs the entry's `installSql` (a `CREATE MODEL` statement that points at the downloaded file). A row appears in `system.models`.
5. **Use** — call `models.<name>(args)` from any query. The inference session loads lazily on the first call, so a freshly installed model only pays its session-load cost the first time it's used.
6. **Uninstall** — remove from the Model Catalog tab. The on-disk files are deleted and the `system.models` row goes away.

While a model is loaded, the engine tracks resident inference sessions and evicts least-recently-used ones under memory pressure. Eviction is transparent — the next call rehydrates from disk.

For the models directory's location and resolution order, see [CREATE MODEL — Setup](sql/create-model.md#setup).

## What's in the catalog

The catalog spans the standard model-zoo categories. The Model Catalog tab is authoritative for the live list; the categories below are stable, the specific model names are illustrative.

| Category | What it does | Representative |
|---|---|---|
| **Object detection** | Bounding boxes and labels for objects in an image | YOLOX family |
| **Segmentation** | Per-pixel masks (foreground, classes, instances) | MobileSAM, U²-Net |
| **Image classification** | Single label per image | MobileNetV2 |
| **Depth estimation** | Depth map per pixel (relative or metric) | Depth Anything v2/v3, MiDaS, DPT, ZoeDepth |
| **OCR** | Text regions and transcriptions from images | PaddleOCR |
| **Captioning** | Natural-language description of an image | Florence-2 |
| **Embeddings** | Fixed-length vectors for text or images | all-MiniLM-L6-v2, CLIP |
| **Speech-to-text** | Transcription from audio clips | Whisper family |
| **Text-to-speech** | Audio synthesis from text | Bark |
| **Image generation** | Diffusion-based image synthesis | Stable Diffusion variants |
| **LLMs** | Token generation, chat completion | Llama 3.1 family |

Open the Model Catalog tab for the exact set installed in your catalog, including all variants and version-specific entries.

## Adding your own models

Any ONNX or GGUF file can be registered as a model with [`CREATE MODEL`](sql/create-model.md). The user-declared model lands in the same `models.X` namespace as catalog entries and dispatches identically — use this when you have a model that isn't in the catalog or when you're iterating on something you trained yourself.

A Python bridge is in development for PyTorch / safetensors workflows; Bark currently runs through this bridge as an early experiment.

## LLMs and streaming

LLM dispatch uses two scalars, both callable only from inside a `CREATE MODEL` body:

- **`llama_chat(session, messages, …)`** — multi-turn chat with an `Array<ChatMessage>`. The GGUF's embedded template (Llama 3, Mistral, Qwen, …) is applied automatically — no manual prompt-templating glue.
- **`llama_generate(session, prompt, …)`** — single-prompt token generation.

LLM token chunks stream live. When an LLM model is invoked from a procedural body via `CALL`, each token chunk forwards to the host as it arrives, so the front end can render tokens progressively rather than waiting for the full completion. See [Procedural Statements](sql/procedural.md) for the `CALL` surface.

The catalog ships every LLM entry as a pair: a `<name>_chat` registration that implements `ChatCompleter`, plus a `<name>` registration implementing `TextGenerator` that delegates to the chat form with a single user message. Use the chat form for multi-turn flows and the generator form for one-shot completions.

## Introspection

The `system.models` virtual table exposes every registered model in the current catalog:

```sql
SELECT name, kind, parameters, license, status
FROM system.models
ORDER BY category, name;
```

`kind` distinguishes registration sources:

- `builtin` — engine-baked entry from the model catalog (legacy code-path models).
- `declared` — a SQL-defined registration. Includes both your own `CREATE MODEL` entries and catalog-installed models whose entries carry an `installSql` field.
- `discovered` — The engine is aware that the model exists in the static catalog but it has not yet been downloaded or installed.

See [Schema Introspection](sql/schema-introspection.md) for the full set of `system.*` tables.

## See also

- [CREATE MODEL](sql/create-model.md) — register your own ONNX or GGUF model.
- [Examples](examples/index.md) — worked queries using catalog models.
- [Schema Introspection](sql/schema-introspection.md) — `system.models`, `system.functions`, and related views.
