# Model task types — declared contracts + inferred size facts

## Goal

Make every `CREATE MODEL` programmatically classifiable by the frontend and
by other engine layers (a future `tasks.X` dispatch namespace, a "run all
classifiers" UI, etc.) without forcing users to declare anything the
catalog can compute from the file itself.

Two complementary changes:

1. **Declared semantics** (`IMPLEMENTS <TaskType>` clause) — what kind of
   model this is. Authoritatively answers "is this a classifier? an
   embedder? a detector?" The catalog enforces signature match against
   the contract.
2. **Inferred facts** (size, precision, parameter count) — read from
   the ONNX file at `CREATE MODEL` time. Catalog populates them
   automatically; the user doesn't type a number.

Together: minimum declaration burden (just the task name), maximum
queryability via `system.models`.

## Non-goals

- User-declared size / parameter count. Hand-written numbers drift from
  reality; protobuf-walked numbers don't.
- Open tag system. Defer until there's a concrete use case the
  contract-typed approach can't cover.
- Cross-task automatic conversion (e.g. "run this `ImageClassifier`
  through a `TextGenerator` to caption it"). Out of scope.
- Reasoning about a model's *quality* — accuracy, calibration, drift.
  Beyond the catalog's purview.

## Required prerequisite: named-type vocabulary

The SQL surface today supports `RETURNS Float32`, `RETURNS Int32[]`,
etc. It does **not** support struct return types — neither inline
(`RETURNS Struct<x: Float32, y: Float32, w: Float32, h: Float32>`) nor
named (`RETURNS BoundingBox`).

The named form is what we want — bare named-type identifiers in
`RETURNS`, parameters, and DECLARE annotations, resolving through a
`NamedTypeRegistry`. This sidesteps inline-struct parsing entirely:

```sql
RETURNS ScoredClass                                    -- named, resolves via registry
RETURNS Array<ScoredDetection>                         -- nested in Array<>
RETURNS Array<Point2D>                                 -- existing typed primitive, no registry hit
```

Anonymous-struct syntax (`RETURNS { x: Float32 }` or
`RETURNS Struct<x: Float32>`) is deferred indefinitely. The
engine-defined vocabulary covers ~95% of what models need; the rare
genuinely-one-off shape can either request a vocabulary addition or
wait for a future `CREATE TYPE` user-defined-type surface. See the
[named-type vocabulary appendix](#appendix-named-type-vocabulary)
for the initial inventory.

The vocabulary is **code-defined**, mirroring `TaskTypeRegistry`. Users
discover what's available via `system.types`; the language server
surfaces them in completion / hover / signature help so authoring
feels native.

## Tiered scope

### Tier 1a — `NamedTypeRegistry` + `IMPLEMENTS` clause + full contract vocabulary

Ships the declaration mechanism, the named-type registry, and the bulk
of the task vocabulary (the part that doesn't need typed media or new
input kinds the engine lacks today).

**Syntax**:

```sql
CREATE MODEL cat_or_dog(img Image) RETURNS ScoredClass
    IMPLEMENTS ImageClassifier
    USING 'file:///path/to/model.onnx'
AS BEGIN
    DECLARE logits Float32[] = infer(image_to_tensor_chw(img, [224, 224], imagenet_mean(), imagenet_std()));
    DECLARE probs  Float32[] = softmax(logits);
    DECLARE top    Int32     = argmax(probs);
    RETURN {class: top, score: probs[top]}
END
```

The clause is **optional**. Existing models keep working without it;
they show up as `task = NULL` in `system.models`.

The named-type registry surfaces ~16 composite types
(`ScoredClass`, `BoundingBox`, `ScoredDetection`, `Keypoint`,
`TimedText`, etc. — see [vocabulary appendix](#appendix-named-type-vocabulary))
that the task contracts reference. Users `RETURNS ScoredClass` rather
than spelling out the struct shape inline; the engine resolves the
identifier through `NamedTypeRegistry`.

**Enforcement**: catalog rejects `CREATE MODEL` when the declared
parameters / return type don't match the contract. Clear error message:
`"IMPLEMENTS ImageClassifier requires (Image) → ScoredClass, got (Image) → Float32[]."`

**Contracts are exact, not lenient.** If your model returns logits
instead of `ScoredClass`, don't declare `IMPLEMENTS ImageClassifier` —
leave it opaque. A lenient contract that accepts "anything score-ish"
makes the frontend's job harder (it still has to introspect to know
what it got), not easier.

**Shape vs semantics are orthogonal.** Named types describe a value's
*shape* (`RegionScore = Struct<bbox: BoundingBox, score: Float32>`).
Task types describe a model's *semantics* (`TextDetector` = "this model
finds text regions in images"). The same shape can serve multiple
semantics — a text-region detector and a generic edge-region proposer
both emit `Array<RegionScore>` but declare different `IMPLEMENTS`
clauses. A renderer like `image_draw_bounding_boxes` that consumes
"things with bboxes" cares only about shape; a dispatch layer choosing
"the best text detector for this query" cares only about semantics.
This separation is load-bearing — don't conflate.

See the [comprehensive task vocabulary](#appendix-comprehensive-task-vocabulary)
for the full list of contracts that ship in this tier.

### Tier 1b — inferred size facts on `system.models`

Walk the ONNX protobuf header at `CREATE MODEL` time and populate
size-related columns automatically. The user doesn't declare anything.

**Inferred facts**:

| Column | Source | Notes |
|---|---|---|
| `file_size_bytes` | `FileInfo.Length` | Already on `system.models`. |
| `parameter_count` | Sum of `product(dims)` over every `GraphProto.initializer` | Initializers are the weight tensors; shapes live in the protobuf header (no weights read). |
| `precision` | Dominant `data_type` across initializers | `Float16`, `Float32`, `Int8`, `Int4`, mixed → `Mixed`. |
| `est_vram_mb` | Already heuristic via `1.2 × file_size` | Could refine using parameter count × bytes-per-element + activation budget. |

**Implementation**: extend
[`OnnxFileMetadataReader`](../src/DatumV/Inference/OnnxRuntime/OnnxFileMetadata.cs)
with a `GraphProto.initializer` walk. Each initializer is a
`TensorProto` carrying `data_type` (enum: `FLOAT = 1`, `FLOAT16 = 10`,
`INT8 = 3`, `INT4 = 22`, …) plus `dims`. ~80 LOC of protobuf walking
extending the existing reader. Then ~30 LOC threading the new fields
through `ModelCatalogEntry` / `ModelDescriptor` / `ModelsTableProvider`.

**Cost**: file-size-bounded — even a 70B-param ONNX inspects in ~tens
of milliseconds (initializer count is bounded by graph node count, not
weight bytes). Negligible per `CREATE MODEL`.

### Tier 1c — anonymous-struct return syntax (deferred indefinitely)

With the named-type registry covering the contract vocabulary, the
inline-struct-parsing path is no longer load-bearing. Defer until a
real workload demands an anonymous shape that doesn't fit the
registry. When it does land, the syntax choice is between
`RETURNS { x: Float32 }` (TS-style) and `RETURNS Struct<x: Float32>`
(generics-style); pick based on what feels right with the rest of the
SQL surface at that time.

### Tier 2 — `tasks.X` dispatch namespace

With the `task` column on `system.models`, the existing
[`project_tasks_namespace_and_cascade.md`](../memory/project_tasks_namespace_and_cascade.md)
plan collapses to a one-line lookup:

```sql
SELECT name FROM system.models WHERE task = 'ImageClassifier' AND est_vram_mb < @budget
ORDER BY parameter_count DESC LIMIT 1;
```

`tasks.classify(@img)` resolves the model name via this query (using
defaults / user preferences for the WHERE / ORDER BY) then dispatches.
No signature introspection guessing needed.

### Tier 3 — frontend / generative-UI hooks

Once `task` + the size facts are on `system.models`, the frontend can:

- **"Open With…" menu**: given a cell of kind `Image`, list all
  `ImageClassifier` / `ImageEmbedder` / `ObjectDetector` models.
- **Auto-select**: cheapest model that fits a runtime VRAM budget.
- **Comparison harness**: run every classifier of a given task on the
  same input, present results side-by-side. Each row is one model;
  every model's output has the same schema by construction.

None of this needs new engine work beyond the `task` column existing
and being filterable.

## Schema additions

`system.models` gains four columns:

| Column | Type | Source | Nullable |
|---|---|---|---|
| `task` | `String` | `IMPLEMENTS` clause (or `NULL` when absent) | yes |
| `parameter_count` | `Int64` | Inferred at load time | yes (built-in non-ONNX models) |
| `precision` | `String` | Inferred at load time | yes |
| `est_vram_mb` | `Int32` | Already exists | no (heuristic when not declared) |

`system.tasks` (new virtual view) exposes the registered task
vocabulary so users can `SELECT * FROM system.tasks` and see
what `IMPLEMENTS` accepts:

```
name              | input_kinds       | return_kind  | description
ImageClassifier   | [Image]           | Int32        | Top-1 class id for the input image.
ImageEmbedder     | [Image]           | Float32[]    | Image feature vector, L2-normalized.
…
```

## Implementation notes

- **Task vocabulary is code-defined**, not SQL-defined. A
  `TaskTypeRegistry` (mirrors `FunctionRegistry`) holds the contracts.
  Adding a task type requires a small PR; users can't add their own.
  This is deliberate — task types are the *interop layer*; if every
  user can mint one, the frontend can't rely on anything.
- **Enforcement lives in `ApplyCreateModelAsync`.** After parsing the
  signature + `IMPLEMENTS` clause, the registrar looks up the contract,
  compares input kinds / return kind / array-ness, and throws a
  structured `FunctionArgumentException` on mismatch. Same error shape
  as parameter-default validation.
- **Built-in models can declare `IMPLEMENTS` too.** `ModelCatalogEntry`
  gets a `TaskType?` field; `BuiltinModels.AttachStandardModels`
  declares `IMPLEMENTS ImageClassifier` on MobileNetV2, etc. The same
  `task` column surfaces both built-ins and declared models — the
  frontend doesn't care which is which.
- **Inferred-fact computation is one-shot at `CREATE MODEL` time** —
  results stored on the descriptor / catalog row, not recomputed on
  every `system.models` query. `OR REPLACE` re-reads the file and
  updates the columns; `DROP MODEL` clears them.
- **Multiple precisions in one model = `'Mixed'`**. Mixed-precision
  ONNX (LLM with int8 weights but fp32 layer norms) is common. Don't
  try to be clever about "dominant" — `Mixed` is honest and the
  frontend can show it as-is.

## Decisions locked in (2026-05-17)

Resolved during planning conversation; documented here so they don't
re-litigate during implementation.

- **Shape vs semantics are orthogonal axes.** Named types describe the
  *shape* of a value (`RegionScore = Struct<bbox: BoundingBox, score: Float32>`).
  Task types describe the *semantics* of a model (`TextDetector` = "this
  model finds text regions"). The same shape can serve multiple
  semantics — PP-OCR-det and a generic edge-region proposer both emit
  `Array<RegionScore>` but declare different `IMPLEMENTS` clauses. A
  renderer that consumes "things with bboxes" only cares about shape; a
  dispatch layer choosing "the best text detector" only cares about
  semantics.
- **Field matching at signature boundaries is name-aware, not
  order-strict.** When checking a RETURN expression's inferred shape
  against a declared named-type return, compare the field sets by name
  + kind regardless of declaration order. Avoids "expected `[class,
  score]`, got `[score, class]`" friction for users (and for Claude
  authoring new models).
- **`IMPLEMENTS` enforcement runs at CREATE MODEL time, not run time.**
  Walk the RETURN expression, infer its struct shape from struct-literal
  field names + scalar kinds, compare against the contract's declared
  return type. Errors surface at registration, not on the first row
  scanned. Same pass also enforces parameter-type match.
- **Parameter naming is type-only.** `IMPLEMENTS ImageClassifier`
  requires `(Image) → ScoredClass`; the parameter can be named `img`,
  `picture`, `input`, whatever. C#-interface convention — names are
  documentation, not contract.
- **Versioning is additive-only.** Contracts never change shape; when
  one must, mint a new name. Named types help by capturing common shapes
  upfront — adding `latency_ms` to every classifier's output is a
  registry-level extension (`ScoredClassDetailed`), not a contract V2.
- **`IMPLEMENTS` in `OR REPLACE` equality.** Same-name + different-task
  is suspicious but legal; emit a `WARNING` and proceed.
- **`x::ScoredClass` is the canonical user-facing cast surface.**
  Postgres-style `::` operator is already implemented in the parser.
  `CAST(x AS ScoredClass)` works through the same code path; both are
  deferred until the first model actually needs an explicit struct cast.

## Open questions

- **Should the task vocabulary be in a separate registry that ships
  independently?** Same shape as `Microsoft.ML.Tokenizers` —
  task definitions in a small NuGet that the engine + clients can
  depend on without dragging in the full runtime. Future consideration;
  not blocking.
- **Dispatch policy for `tasks.X` (Phase 2 question).** Lean:
  user-configurable via a small config row; default is "cheapest model
  that fits VRAM budget, lexicographic tie-break." Not Phase 1a's
  problem.
- **Hybrid task composition recipes** (Phase 2). `TextOCR = TextDetector ⋊ TextRecognizer`
  scatter-gather pattern. Architecture sketched in conversation; concrete
  shape lands when Phase 2 starts.

## Phasing & sequencing

### Phase 1a — execution order

Bundled into one shippable PR (~700 LOC + tests). Each step depends on
the previous one's foundations, in order:

1. **`NamedTypeRegistry`** (~100 LOC). Static process-wide vocabulary
   of name → `TypeDescriptor` template. Populated at module init with
   the 16 entries from the [named-type appendix](#appendix-named-type-vocabulary).
   Registration order is topological: primitives → composites →
   composites-of-composites (e.g. `BoundingBox` before `ScoredDetection`).
2. **`TypeRegistry` constructor pre-interns from `NamedTypeRegistry`** (~30 LOC).
   Adds a name → TypeId index alongside the existing structural-intern.
   Every per-query registry has the 16 types pre-interned at IDs that
   are deterministic per fresh registry — useful for `system.types`
   row stability and debugging.
3. **`TypeAnnotationResolver` extension** (~30 LOC). When the bare
   identifier doesn't match a `DataKind` enum value, fall back to the
   per-query `TypeRegistry`'s name index. Drives `RETURNS ScoredClass`,
   `DECLARE x ScoredClass`, parameter type annotations, etc.
4. **`system.types` virtual table provider** (~150 LOC). Mirrors
   `ModelsTableProvider` shape. Columns: `name`, `kind` (`primitive` /
   `named-struct` / `named-array` / `user-defined` if/when CREATE TYPE
   lands), `definition` (canonical string form), `used_by_count` (task
   contracts referencing this type).
5. **`ImageDrawBoundingBoxesFunction` update** (~30 LOC). Walk `bbox`
   field as a nested struct when present (`det.bbox.x` / `.y` / `.w` /
   `.h`); fall back to flat `det.x` etc. for backward compat. Unlocks
   every named type whose bbox is nested (`RegionScore`, `ScoredDetection`,
   `LabeledDetection`, `FaceDetection`, `OcrLine`).
6. **`TaskTypeRegistry` + `IMPLEMENTS` clause** (~250 LOC). Parser
   extension for `IMPLEMENTS` as a contextual identifier (same pattern
   as `USING`). Vocabulary pre-registered (the contracts from the
   comprehensive appendix). `ApplyCreateModelAsync` validates the model's
   signature against the contract.
7. **CREATE-MODEL-time RETURN typecheck** (~100 LOC). New pass walks
   the procedural body's RETURN expression(s), infers their struct
   shape from struct-literal field names + scalar kinds, compares
   against the declared return type using name-aware field matching.
   Errors surface at `CREATE MODEL` time with both shapes printed.
8. **`system.models.task` column** (~30 LOC). Threads `TaskType?`
   through `ModelDescriptor` and `ModelCatalogEntry`; `ModelsTableProvider`
   surfaces it as a nullable `String`.
9. **Language-server integration** (~80 LOC). Completion includes
   named types in type-position contexts; hover surfaces the struct
   shape; the `IMPLEMENTS` keyword surfaces task names in completion.
10. **Retrofit the two existing SQL-defined models** (~30 LOC):
    - **PP-OCR-det**: change `dbnet_postprocess` to emit `Array<RegionScore>`
      (drop the always-"text" label string; nest bbox as `BoundingBox`).
      Update `models/sql/paddleocr-v4-det.sql` to `RETURNS Array<RegionScore>`
      `IMPLEMENTS TextDetector`. Existing `ImageDrawBoundingBoxesFunction`
      regression test pulls the new shape through end-to-end.
    - **all-MiniLM-L6-v2**: add `IMPLEMENTS TextEmbedder` to the SQL.
      Return shape already fits (`Float32[]`); zero body changes.

### Phase 1b — inferred size facts (parallel with 1a)

~150 LOC + tests. Independent of Phase 1a.

- Extend `OnnxFileMetadataReader` with a `GraphProto.initializer` walk.
- Thread `parameter_count` / `precision` through `ModelCatalogEntry` / `ModelDescriptor`.
- New columns on `system.models`.
- Follow-up: `IModel.ParameterCount` declaration for non-ONNX built-ins
  (Llama GGUF) so the column isn't NULL for everything that isn't a
  CREATE MODEL or ONNX built-in.

### Phase 1c — anonymous-struct returns

Deferred indefinitely. Land when a real model contract demands a shape
the named-type vocabulary can't express. Syntax choice (`{x: Float32}`
vs `Struct<x: Float32>`) decided at landing time based on what feels
right with the rest of the SQL surface.

### Phase 2 — `tasks.X` dispatch namespace (~80 LOC + tests)

Resolves through `system.models.task` with user-configurable selection
policy. Optional composition recipes for hybrid tasks (`TextOCR`
synthesized from `TextDetector` + `TextRecognizer` when no end-to-end
model is registered) — scatter-gather pattern emitted as a planned
subquery.

### Phase 3 — frontend (no engine work)

"Open With…" menu, auto-select-by-budget, comparison harness. Lands
incrementally once `system.models.task` and `system.types` are
populated.

### Built-in migration follow-up

Not gated on Phase 1a — but the ~20 built-in `IModel` implementations
need `TaskType` declarations on their `ModelCatalogEntry` for `tasks.X`
dispatch to consider them. Mechanical audit:

- `MobileNetV2` → `ImageClassifier`
- `YOLOX` → `ObjectDetector`
- `SCRFD` / `RetinaFace` → `FaceDetector`
- `Whisper` family → `AudioToText`
- `TrOCR` → `TextRecognizer`
- `Florence-2` (OCR mode) → `TextOCR`; (caption mode) → `ImageCaptioner`
- `BLIP` → `ImageCaptioner`
- `MiDaS` / `DPT` / `Depth-Anything` → `DepthEstimator`
- `Real-ESRGAN` / `SwinIR` → `ImageUpscaler`
- `U²-Net` / `BRIA-RMBG` → `BackgroundRemover`
- `MobileSAM` → `PointSegmenter` / `BoxSegmenter` (per call signature)
- `Llama` family → `TextGenerator`
- `Phi-3.5-vision` / `Moondream2` → `VisualQA`
- `Kokoro` / `Bark` → `TextToAudio`
- `SDXL-Turbo` / `SD-Turbo` → `TextToImage`

Some outputs may need a small adjustment to match named-type shapes
(e.g. detector outputs returning flat `x/y/w/h` should restructure to
`bbox: BoundingBox` nested — same change `ImageDrawBoundingBoxesFunction`
gains support for in step 5 above). Audit when the migration lands.

---

## Appendix: Comprehensive task vocabulary

The full enumeration of tasks we'd want to declare. Output space is
bounded by the engine's data types (Image, String, Audio, Video,
primitive arrays, Struct combinations), so this list approximates the
realistic ceiling — not exhaustive, but covers every model family
DatumV ships or plans to ship today, plus the obvious extensions.

**Design choices baked into the vocabulary:**

- **Always include score/confidence on classifiers and detections.** A
  caller can project `.class` to recover the bare scalar; they can't
  recover a discarded score. `ImageClassifier` returns
  `Struct{class: Int32, score: Float32}`, not bare `Int32`. Avoids the
  "V2 with score" mint-a-new-task forest.
- **Specialized variants over generic supertypes** when they materially
  differ in shape. `FaceDetector` carries `landmarks: Array<Point2D>`
  alongside the bbox — folding it into a generic `ObjectDetector` would
  drop the landmarks or force every detector to carry an empty array.
- **Pipeline-decomposed tasks stay decomposed.** `TextDetector` +
  `TextRecognizer` are separate contracts because real OCR systems
  often pair an off-the-shelf detector with a domain-trained recognizer.
  `TextOCR` exists for end-to-end packaged systems (Florence-2-OCR-region)
  but doesn't replace the decomposition.
- **LLMs and diffusion models live as built-in `IModel`s, not SQL bodies.**
  Tokenization, chat templating, sampling, KV cache, and scheduler state
  don't have a natural SQL representation; a SQL body for an LLM is just
  `RETURN infer(prompt)` with no preprocessing to express. Built-ins
  still declare `IMPLEMENTS` via `ModelCatalogEntry.TaskType` so
  `system.models.task` and `tasks.X` dispatch work uniformly across
  `kind = 'builtin'` and `kind = 'declared'`.

Tier column key:
- **1a** — ships in Phase 1a (named-type registry + `IMPLEMENTS` clause).
- **Future** — needs additional engine support (new input kind, new typed media, etc.).

With the named-type registry in place, anonymous-struct returns are no
longer a Phase gate; every contract below that takes a registered named
type in its signature ships in 1a.

### Vision — input: `Image`

| Task | Signature | Tier | Example models |
|---|---|---|---|
| `ImageClassifier` | `(Image) → ScoredClass` | 1a | MobileNetV2, ResNet, EfficientNet |
| `ImageMultiClassifier` | `(Image) → Array<ScoredClass>` | 1a | Multi-label ResNet, top-k ImageNet |
| `BinaryImageClassifier` | `(Image) → BinaryScore` | 1a | NSFW filter, defect/no-defect |
| `ImageTagger` | `(Image) → Array<ScoredLabel>` | 1a | RAM++, WD tagger, CLIP-tag |
| `ImageEmbedder` | `(Image) → Float32[]` | 1a | CLIP image-tower, DINOv2 |
| `ObjectDetector` | `(Image) → Array<ScoredDetection>` | 1a | YOLOX, YOLOv8, RT-DETR |
| `RegionLocalizer` | `(Image) → Array<RegionScore>` | 1a | PP-OCR-det, edge-box proposals |
| `KeypointDetector` | `(Image) → Array<Keypoint>` | 1a | OpenPose, MoveNet |
| `FaceDetector` | `(Image) → Array<FaceDetection>` | 1a | SCRFD, RetinaFace, BlazeFace |
| `TextDetector` | `(Image) → Array<RegionScore>` | 1a | PP-OCR-det (paired with a recognizer) |
| `TextRecognizer` | `(Image) → String` | 1a | TrOCR, PP-OCR-rec (one crop → one line) |
| `TextOCR` | `(Image) → Array<OcrLine>` | 1a | Florence-2 OCR-region, end-to-end OCR bundles |
| `ImageCaptioner` | `(Image) → String` | 1a | BLIP, Florence-2 caption, GIT |
| `SemanticSegmenter` | `(Image) → Image` | 1a | DeepLab, SegFormer (class-id-as-grayscale convention) |
| `InstanceSegmenter` | `(Image) → Array<ScoredMask>` | 1a | Mask R-CNN, Mask2Former |
| `PointSegmenter` | `(Image, Point2D) → Image` | 1a | MobileSAM with point prompt |
| `BoxSegmenter` | `(Image, BoundingBox) → Image` | 1a | MobileSAM with box prompt |
| `DepthEstimator` | `(Image) → Image` | 1a | MiDaS, DPT, Depth Anything |
| `ImageUpscaler` | `(Image) → Image` | 1a | Real-ESRGAN, SwinIR |
| `ImageRestorer` | `(Image) → Image` | 1a | denoise, dehaze, deblur models |
| `ImageColorizer` | `(Image) → Image` | 1a | DeOldify, ChromaGAN |
| `BackgroundRemover` | `(Image) → Image` | 1a | U²-Net, BRIA RMBG |
| `ImageStyleTransfer` | `(Image, Image) → Image` | 1a | AdaIN, fast-style |
| `ImageEditor` | `(Image, String) → Image` | 1a | InstructPix2Pix, MagicBrush — likely built-in (diffusion) |

### Text — input: `String`

| Task | Signature | Tier | Example models |
|---|---|---|---|
| `TextClassifier` | `(String) → ScoredClass` | 1a | DistilBERT sentiment, MiniLM intent |
| `TextMultiClassifier` | `(String) → Array<ScoredClass>` | 1a | Multi-label classification |
| `BinaryTextClassifier` | `(String) → BinaryScore` | 1a | Spam, toxicity, polarity |
| `TokenClassifier` | `(String) → Array<ScoredToken>` | 1a | NER (BERT-NER), POS tagging |
| `TextEmbedder` | `(String) → Float32[]` | 1a | all-MiniLM-L6-v2 ✅, BGE, GTE, Jina |
| `TextPairScorer` | `(String, String) → Float32` | 1a | Cross-encoder sentence similarity |
| `TextReranker` | `(String, Array<String>) → Array<ScoredIndex>` | 1a | bge-reranker, Cohere rerank |
| `TextGenerator` | `(String) → String` | 1a — **built-in** | Llama, Mistral, Phi (GGUF/ONNX) |
| `Translator` | `(String) → String` | 1a — built-in | MarianMT, NLLB (one model = one direction; multi-lingual via call params) |
| `TextSummarizer` | `(String) → String` | 1a — built-in | LLM-backed (BART, Llama-summary prompt) |
| `TextEditor` | `(String, String) → String` | 1a — built-in | Instruct-tuned LLM (text + instruction → edited text) |

### Audio — input: `Audio`

| Task | Signature | Tier | Example models |
|---|---|---|---|
| `AudioClassifier` | `(Audio) → ScoredClass` | 1a | YAMNet, AudioSet classifier |
| `AudioMultiClassifier` | `(Audio) → Array<ScoredClass>` | 1a | Sound event detection |
| `AudioEmbedder` | `(Audio) → Float32[]` | 1a | OpenL3, CLAP audio-tower |
| `AudioToText` | `(Audio) → String` | 1a | Whisper, Distil-Whisper |
| `AudioToTextTimed` | `(Audio) → Array<TimedText>` | 1a | Whisper word-level timestamps |
| `TextToAudio` | `(String) → Audio` | 1a — **built-in** | Kokoro, Bark, Coqui TTS |
| `VoiceCloner` | `(Audio, String) → Audio` | 1a — built-in | XTTS, voice-sample + text |
| `AudioRestorer` | `(Audio) → Audio` | 1a | Denoise / dereverb |

### Video — input: `Video`

| Task | Signature | Tier | Example models |
|---|---|---|---|
| `VideoClassifier` | `(Video) → ScoredClass` | 1a | Action recognition (Kinetics) |
| `VideoSegmentClassifier` | `(Video) → Array<TimedClass>` | 1a | Temporal action localization |
| `VideoEmbedder` | `(Video) → Float32[]` | 1a | VideoMAE, X-CLIP video-tower |
| `TextToVideo` | `(String) → Video` | Future — built-in | AnimateDiff, CogVideoX |
| `VideoUpscaler` | `(Video) → Video` | Future | RealBasicVSR (per-frame today; native video tomorrow) |

### Multimodal

| Task | Signature | Tier | Example models |
|---|---|---|---|
| `VisualQA` | `(Image, String) → String` | 1a | Phi-3.5-vision, Moondream2, LLaVA |
| `ImageTextSimilarity` | `(Image, String) → Float32` | 1a | CLIP, SigLIP scoring |
| `ZeroShotImageClassifier` | `(Image, Array<String>) → Array<ScoredClass>` | 1a | CLIP, SigLIP picking from candidate labels |
| `ZeroShotObjectDetector` | `(Image, Array<String>) → Array<LabeledDetection>` | 1a | OWL-ViT, GroundingDINO |
| `VideoQA` | `(Video, String) → String` | Future | VideoLLaMA, Video-LLaVA |
| `TextToImage` | `(String) → Image` | 1a — **built-in** | SDXL, SD-Turbo, Flux |
| `ImageToImage` | `(Image, String) → Image` | 1a — built-in | SD img2img, ControlNet |

### Structured — input: primitive arrays

| Task | Signature | Tier | Example models |
|---|---|---|---|
| `TabularClassifier` | `(Float32[]) → ScoredClass` | 1a | XGBoost / LightGBM exported via ONNX |
| `TabularRegressor` | `(Float32[]) → Float32` | 1a | Same, regression head |
| `TimeSeriesClassifier` | `(Float32[]) → ScoredClass` | 1a | ROCKET, InceptionTime |
| `TimeSeriesForecaster` | `(Float32[]) → Float32[]` | 1a | DeepAR, N-BEATS |
| `TimeSeriesAnomalyDetector` | `(Float32[]) → Float32` | 1a | Iso-forest, autoencoder anomaly |

### Future / specialized (parked)

| Task | Why parked |
|---|---|
| `PointCloudClassifier`, `PointCloudSegmenter` | Need `PointCloud` as a typed media kind (the [typed-media memo](../memory/project_typed_media.md) sketches it; not landed). |
| `MeshClassifier`, `MeshSegmenter` | Same — need `Mesh` typed kind. |
| `GraphClassifier`, `GraphEmbedder` | Need a Graph typed kind. Niche enough to defer until a real workload demands it. |
| `JointEmbedder` family marker | CLIP's image and text encoders embed into the *same* vector space. Worth an attribute (`embedding_space: 'clip-vit-b-32'`?) so the frontend can pair them. Not a separate task type; closer to a metadata-tag on the embedder rows. |
| `Stylizer(Image, Array<Float32>) → Image` | Latent-space control (sliders for "more cartoon-y", etc.). Real but speculative; revisit when a real workload arrives. |

### Summary stats

- **~50 distinct task contracts** across vision, text, audio, video, multimodal, structured.
- **Phase 1a covers ~all of it** once named types land — every contract whose return is a primitive, an array of primitives, a named type, or an Array<named-type> ships immediately. Almost nothing remains gated on a future "anonymous struct" syntax.
- **~10** are explicitly built-in (LLMs, diffusion, TTS) — declare `IMPLEMENTS` via `ModelCatalogEntry.TaskType`, never via `CREATE MODEL`.
- **Most domains** have a 3-5 task pattern: classify / embed / generate / locate-or-segment / restore. The vocabulary is large but the *kinds* of tasks are few — frontends can group rows by template.

---

## Appendix: Named-type vocabulary

Composite struct types pre-registered in `NamedTypeRegistry` at engine
startup. Picked to cover the task vocabulary above; rejected aliases
that reduce trivially to `Array<existing>` (Polygon / PointCloud /
Path / Trajectory all express as `Array<Point2D>` or `Array<Point3D>`
— no entry needed).

### Field-name conventions

Locked in once across the whole vocabulary so consumers don't bikeshed:

- **`class: Int32`** — class id from a finite, ordered label set (ImageNet, COCO, etc.). Use when the model emits an index against a known label table.
- **`label: String`** — human-readable label when the label set is open-vocabulary or the model emits the text directly (CLIP tag, OWL-ViT box label, NER tag).
- **`score: Float32`** — confidence / probability. Convention is [0, 1]; not enforced.
- **`confidence: Float32`** — synonym for score reserved for *keypoints* (where the field is conceptually different from a class probability). Yes, this is a bit inconsistent — but `Keypoint{x, y, confidence}` is the published name in every pose-estimation library; matching that wins over uniformity.
- **`bbox: BoundingBox`** — nested named type; `x, y, w, h` in pixel space (origin at top-left, w/h the size). The xywh convention is more common in detection model outputs than xyxy; pick one and stick.
- **`mask: Image`** — segmentation mask as an Image (grayscale, values in [0, 255] or thresholded binary).
- **`start_ms / end_ms: Int64`** — temporal spans in milliseconds since the start of the input. Int64 because audio/video can exceed 2^31 ms (~25 days), and milliseconds is finer than necessary but standard.
- **Score field goes last in every struct.** Consumers projecting just `.class` or `.bbox` see the natural-order layout; the score is an annotation, not a primary field.

### Vocabulary

| Name | Definition | Used by (task contracts) |
|---|---|---|
| `ScoredClass` | `Struct<class: Int32, score: Float32>` | `ImageClassifier`, `TextClassifier`, `AudioClassifier`, `VideoClassifier`, `TabularClassifier`, `TimeSeriesClassifier` |
| `ScoredLabel` | `Struct<label: String, score: Float32>` | `ImageTagger` (open-vocabulary tagging) |
| `BinaryScore` | `Struct<value: Boolean, score: Float32>` | `BinaryImageClassifier`, `BinaryTextClassifier` |
| `ScoredIndex` | `Struct<index: Int32, score: Float32>` | `TextReranker` |
| `ScoredToken` | `Struct<token: String, label: String, score: Float32>` | `TokenClassifier` (NER, POS) |
| `BoundingBox` | `Struct<x: Float32, y: Float32, w: Float32, h: Float32>` | nested into the detection types below |
| `RegionScore` | `Struct<bbox: BoundingBox, score: Float32>` | `RegionLocalizer`, `TextDetector` |
| `ScoredDetection` | `Struct<bbox: BoundingBox, class: Int32, score: Float32>` | `ObjectDetector` |
| `LabeledDetection` | `Struct<bbox: BoundingBox, label: String, score: Float32>` | `ZeroShotObjectDetector` |
| `FaceDetection` | `Struct<bbox: BoundingBox, landmarks: Array<Point2D>, score: Float32>` | `FaceDetector` |
| `Keypoint` | `Struct<x: Float32, y: Float32, confidence: Float32>` | `KeypointDetector` |
| `OcrLine` | `Struct<bbox: BoundingBox, text: String, score: Float32>` | `TextOCR` (end-to-end packaged) |
| `ScoredMask` | `Struct<mask: Image, class: Int32, score: Float32>` | `InstanceSegmenter` |
| `TimedSpan` | `Struct<start_ms: Int64, end_ms: Int64>` | (primitive temporal — composed into the timed types below) |
| `TimedText` | `Struct<text: String, start_ms: Int64, end_ms: Int64>` | `AudioToTextTimed` (word-level transcription) |
| `TimedClass` | `Struct<class: Int32, start_ms: Int64, end_ms: Int64, score: Float32>` | `VideoSegmentClassifier` |

**16 named types.** Covers every task contract in the comprehensive
appendix that needs a struct return.

### What was deliberately *not* added

| Considered | Rejected because |
|---|---|
| `Polygon`, `PointCloud`, `Path`, `Trajectory` | `Array<Point2D>` / `Array<Point3D>` reads as well or better. No new type needed. |
| `BoundingBoxXyXy` (corner-pair variant) | One bbox convention is enough; picking xywh and converting at the model body boundary keeps the surface narrow. |
| `BoundingBoxNormalized` (0–1 normalized coords) | Same — a Float32 is a Float32; the *interpretation* is the model body's job. Frontend renders against image dims regardless. |
| `Confidence` as a distinct type | A Float32 is a Float32; wrapping it as `Struct<confidence: Float32>` is noise. |
| `Color`, `Pixel`, `Tensor`, `Matrix` | These are flat primitive arrays at the storage layer; named-type wrappers add ceremony without payoff. |
| Domain-specific composites (`StockPrice`, `WeatherReading`, …) | Belong in user-declared `CREATE TYPE` when that lands. Engine-defined vocabulary stays domain-neutral. |

### Language-server integration

The named types resolve through the same path as built-in scalar
kinds — `TypeAnnotationResolver` consults the `NamedTypeRegistry`
when a bare identifier doesn't match a `DataKind`. The completion
provider includes them in type-position suggestions (e.g. when the
cursor is after `RETURNS ` or in a parameter type slot). Hover surfaces
the struct shape; goto-definition opens a synthetic `system.types`
row.

`system.types` (new virtual view) exposes the registered named types
for users to discover at the SQL surface:

```sql
SELECT name, kind, definition, used_by_count
FROM system.types
WHERE kind = 'named-struct'
ORDER BY used_by_count DESC, name;
```

`used_by_count` is the number of registered task contracts referencing
this named type — useful for "which types matter?" sorting.

### Implementation cost

- `NamedTypeRegistry` (~100 LOC): code-defined registry mirroring `TaskTypeRegistry`. Holds `name → TypeDescriptor` plus a back-reference for `used_by_count`. Pre-registers the 16 types above at engine startup.
- `TypeAnnotationResolver.TryParse` extension (~20 LOC): consult the registry when the bare identifier path misses on the `DataKind` enum.
- `system.types` virtual table provider (~150 LOC): mirrors `ModelsTableProvider` shape.
- Language-server completion / hover (~80 LOC): another schema-aware completion zone for type-position contexts.

**About 350 LOC + tests.** Ships before — or alongside — Phase 1a so
the `IMPLEMENTS` clause has named types to point at on day one.

### Future extensions (not blocking)

- **Width-subtyping for forward compatibility.** When `ScoredClass` needs a fourth field for richer model output, adding `ScoredClassDetailed = Struct<class, score, latency_ms, model_id>` doesn't break callers of `ScoredClass` — the structural-prefix relation lets the richer type stand in. Implement as a one-pass check in signature matching when a real use case demands it.
- **Typed-primitive promotion** for hot detection shapes. `BoundingBox` is `Vector4`-shaped; `Array<BoundingBox>` is `Array<Struct>` today (more expensive than `Array<Float32>`). Same pattern that promoted `Point2D` to a typed primitive could apply to `BoundingBox` / `Keypoint`. Wait for measured profile pressure.
- **User `CREATE TYPE`**. Engine-defined registry stays the canonical interop layer; user types are additive. Probably driven by UDF / procedure return-record needs, not models.
