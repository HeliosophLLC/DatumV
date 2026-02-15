# Inference toolkit — model introspection + integration helpers

## Goal

Make integrating an arbitrary ONNX (or other backend) model into a `CREATE MODEL` body a 5-minute task instead of a 50-minute reverse-engineering exercise. Introspect the file from SQL, generate a starter skeleton, and supply the preprocessing / postprocessing helpers every integration would otherwise re-implement by hand.

The product framing: if the headline is *"the app for running ML models at scale,"* a SQL editor that can't introspect a model file is the same shape of broken as a SQL editor that can't `\d table`. The introspection TVFs are on the critical path for the user-facing story, not a nicety.

## Non-goals (for the first cut)

- Training, fine-tuning, quantizing, or otherwise modifying a model.
- Auto-discovering models on disk or downloading them — that's the model-zoo / catalog layer, separate plan.
- Any backend other than ORT in v1; OpenVINO and friends fold in later.

## Tiered scope

The tiers are prioritization, not phases — pick from the top until you hit diminishing returns for whatever model integration is in front of you.

### Tier 1 — blocking when authoring a CREATE MODEL

You can't sensibly write the body without these.

- **`onnx_inspect(path) → table`** — one row per input + output tensor, columns `(kind STRING, name STRING, dtype STRING, shape INT[], is_dynamic BOOLEAN)`. `kind` is `'input'` or `'output'`. Path resolution mirrors `CREATE MODEL USING` (`file://` absolute, otherwise relative to `Models.ModelDirectory`).
- **`inference_devices() → table`** — one row per `(backend, device)`, columns `(backend STRING, device STRING, available BOOLEAN, reason STRING, estimated_vram_mb INT)`. Drives the editor's "what hardware can I target" affordance and answers `WHERE available = true` for scripted dispatch decisions.

### Tier 2 — major productivity

Without these, you guess + iterate.

- **`onnx_inspect_meta(path) → table`** — single row, columns `(producer_name, producer_version, opset, required_ops STRING[], file_size_bytes, declared_resident_bytes)`. Answers "what is this thing and is it new enough to expect from a current ORT?"
- **`infer_compatibility(path) → table`** — one row per registered backend, columns `(backend, supported BOOLEAN, unsupported_ops STRING[], opset_required INT, opset_supported INT, notes STRING)`. Answers "will ORT take this? OpenVINO?" without an actual load attempt.
- **`model_skeleton(path) → STRING`** — returns a `CREATE MODEL` template string with parameters, return type, USING, and a body stub pre-filled from `onnx_inspect`. Paste-and-edit beats look-up-and-type by a wide margin once a user has integrated their second model.

### Tier 3 — preprocessing / postprocessing helpers

Every integration repeats this code; centralizing it is the difference between a 5-line and a 50-line `CREATE MODEL`. Audit first — some of these already exist under `FunctionCategory.Activation` and `FunctionCategory.Vector`.

- **Vision in**:
  - `image_to_tensor(img, target_size INT[2], mean FLOAT32[3], std FLOAT32[3]) → FLOAT32[]` — produces a flat NCHW Float32 vector with normalize. Most common ONNX-vision input shape.
  - Standard preset constants: `imagenet_mean()`, `imagenet_std()`, `clip_mean()`, `clip_std()`. Returning Float32[3] arrays so they slot directly into `image_to_tensor`.
- **Vision out**:
  - `tensor_to_image(tensor FLOAT32[], height, width, denormalize_mean, denormalize_std) → IMAGE`.
- **LLM / embedding in**:
  - `tokenize(text, tokenizer_path) → INT64[]` — uses HuggingFace tokenizer.json (or sentencepiece, or whatever the file announces).
  - `decode(ids INT64[], tokenizer_path) → STRING`.
- **Outputs / activations** (audit which already exist):
  - `softmax`, `argmax`, `topk`, `l2_normalize`, `cosine_similarity`, `sigmoid`.
- **Detection postprocess**:
  - `nms(boxes FLOAT32[], scores FLOAT32[], iou_threshold FLOAT32) → INT32[]` — returns kept indices.
- **Segmentation postprocess**:
  - `mask_to_polygons(mask FLOAT32[], width, height, threshold FLOAT32) → STRUCT[]`.

### Tier 4 — editor / generative-UI hooks

The Kaggle-workflow target. Wants Tier 1 to exist first.

- Open a `.onnx` file in the editor → side-panel auto-runs `onnx_inspect` + `infer_compatibility` and renders a model card.
- "Insert CREATE MODEL skeleton" code action driven by `model_skeleton(path)`.
- Inline diagnostic: *"your `infer()` arg shape doesn't match session input `[1, 3, 224, 224]`"* — needs the planner to consult the bound session at lint time. Closes the loop where today the user hits the runtime error and bounces.

### Tier 5 — documentation

Every introspection function is a chance to teach. Land docs alongside the function itself — `onnx_inspect` ships with the tutorial that demonstrates it.

- *Your first ONNX model in 5 minutes* — download a public ONNX, `onnx_inspect`, `CREATE MODEL`, `SELECT`. The end-to-end demo for new users.
- Recipe pages: vision classification, embeddings, OCR, LLM, segmentation. Each a CREATE MODEL pattern that uses Tier 3 helpers.

## Where the leverage is

For a first-time integrator, **Tier 1 + Tier 3 (vision + LLM essentials)** is the smallest set that cuts iteration time in half. Tier 2 is huge for *"should I even try this model?"* triage but you can survive without it. Tier 4 wants Tier 1 done first.

Order I'd ship in if I were doing it:

1. Tier 1 (`onnx_inspect`, `inference_devices`) — both are TVFs that read static metadata, no per-row dispatch. Small, self-contained.
2. Tier 3 vision-in essentials (`image_to_tensor`, mean/std presets) and LLM-in (`tokenize` / `decode`).
3. Tier 2 (`onnx_inspect_meta`, `infer_compatibility`, `model_skeleton`) — needed once you're integrating > 3 models and feeling the friction.
4. Tier 4 editor hooks — pays off as soon as Tier 1 is real.
5. Tier 5 docs — folded in alongside each function as it lands.

## Implementation notes

- All TVFs live under the `system` schema (or a new `inference` schema if we want the namespacing). `system.onnx_inspect`, `system.inference_devices`, etc.
- TVFs are a known surface (see `RangeFunction` for the pattern). Each function is one class implementing `ITableValuedFunction`.
- `onnx_inspect` reuses the existing `OnnxRuntimeBackend.LoadAsync` path (or the lighter `Inspect` step) — it doesn't need its own ONNX parser.
- `inference_devices` reads `IInferenceDispatcher.Backends[].AvailableDevices` — already a lazy-evaluated property.
- Tier 3 functions are ordinary scalar functions; some (`tokenize`) need an external tokenizer dependency (probably HuggingFace.Tokenizers NuGet).
- The shape solver in `InferFunction.ResolveShape` needs an extension when a real model has multiple dynamic dims (typical: `[batch, N]`). Likely default — leading dim defaults to 1 unless explicitly set, then absorb the remaining dynamic dim from element count. Validate against real models before committing to the rule.

## Open questions

- Schema for introspection TVFs — `system.*` (consistent with current built-ins) or a new `inference.*` (clearer namespacing as the surface grows)? Lean toward `system` until there are ≥ 4 functions.
- Tokenizer file resolution — same `file://` / relative-to-models-dir convention as `USING`?
- `model_skeleton` output — return as a STRING (user pastes into editor) or expose via a separate code-action endpoint that the language server invokes? STRING is universally consumable; code-action is editor-only but UX is better.
- Generative-UI panel for `.onnx` files — does this go in the existing system.panels framework or is it a special "file viewer" surface?
