---
title: Integrating an ONNX Model
---

# Integrating an ONNX Model

This guide walks you from "I have an ONNX file" to "I'm calling it from
SQL." It assumes no prior ONNX experience.

For the engine internals that power this surface, see
[`technical/onnx-inference.md`](technical/onnx-inference.md). For
the built-in models DatumV ships with, see [`models.md`](models.md).
The full SQL reference for the `CREATE MODEL` DDL lives at
[`sql/create-model.md`](sql/create-model.md).

## What is ONNX?

**ONNX** (Open Neural Network Exchange) is a portable file format for
inference graphs. A `.onnx` file contains:

- The *graph* — the operations the model performs (Conv, MatMul, Softmax, ...).
- The *weights* — the numeric values the operations multiply / add against.
- A *signature* — the names, types, and shapes of the inputs and outputs.

DatumV loads the file via [ONNX Runtime (ORT)](https://onnxruntime.ai)
and exposes it to SQL as a function under the `models` schema. The
runtime handles the math; your job is to:

1. Tell DatumV where the file is.
2. Describe what arguments the SQL function should take.
3. Write the body that transforms those SQL arguments into the tensors
   ORT wants, calls `infer()`, and (optionally) post-processes the
   result.

The whole flow runs through `CREATE MODEL`. The rest of this guide
walks you through the steps with diagnostic SQL between each.

## Prerequisites

- DatumV is installed.
- The models directory is configured. By default DatumV uses
  `%LOCALAPPDATA%\Heliosoph.DatumV\models` on Windows or
  `~/.local/share/Heliosoph.DatumV/models` on Linux/macOS. Change the location
  from the **Settings** tab if you want a directory with more free
  space; the `DATUMV_MODELS` environment variable is honoured as a
  fallback. See [CREATE MODEL — Setup](sql/create-model.md#setup) for
  the full resolution order.

## Step 1 — Get a model file

Most users get ONNX files from one of three places:

| Source | When to use | How |
|---|---|---|
| **HuggingFace** | Anything with a Python implementation | `optimum-cli export onnx --model <hf-id> ./out` |
| **ONNX Model Zoo** | Classic vision/audio models | Download `.onnx` directly from [github.com/onnx/models](https://github.com/onnx/models) |
| **Your own training** | Custom model | `torch.onnx.export(...)`, `tf2onnx`, etc. |

Place the `.onnx` file under your models directory in a per-id
subfolder — the same convention catalog models follow (see
[CREATE MODEL — Directory layout](sql/create-model.md#directory-layout)).
For this guide we'll assume:

```
<models-dir>/
└── my-model/
    └── model.onnx
```

If your model needs a tokenizer (LLMs, embedding models), the model dir
should also contain `tokenizer.json` (or `vocab.json` + `merges.txt`).
HuggingFace's `optimum-cli` puts these in the same output directory by
default.

**Troubleshooting Step 1**

- *"Where do I find the model on HuggingFace?"* — Look for a repo with
  an `onnx/` subfolder or files ending in `.onnx`. If only PyTorch
  weights are published, you'll need to run `optimum-cli` to convert.
- *"The export produced two files: `model.onnx` and `model.onnx_data`."*
  — Normal for models >2GB. ONNX splits weights into a sidecar
  `.onnx_data` file. Keep them together in the same directory; ORT
  reads them automatically.

## Step 2 — Inspect the file

Before writing any `CREATE MODEL`, look at what the file actually
contains. Two introspection TVFs read the file's protobuf header
without loading weights, so this works in milliseconds even on
multi-GB models.

### 2a. Signature: what are the inputs and outputs?

```sql
SELECT * FROM inference.onnx_inspect('my-model/model.onnx');
```

```
 kind   | name        | dtype   | shape          | is_dynamic
--------+-------------+---------+----------------+------------
 input  | pixel_values| Float32 | [-1, 3, 224, 224] | true
 output | logits      | Float32 | [-1, 1000]     | true
```

What the columns tell you:

- **`kind`**: `'input'` or `'output'` — which side of the model the tensor sits on.
- **`name`**: the ONNX-internal tensor name. You'll need it if the model has multiple inputs.
- **`dtype`**: the per-element type (`Float32`, `Int64`, etc.). Determines what kind of SQL value `infer()` expects.
- **`shape`**: the tensor shape. `-1` means *dynamic* — the model accepts any value for that dimension. The leading dim is usually batch size.
- **`is_dynamic`**: `true` if any dim is dynamic.

The example above is a classic ImageNet classifier — takes a 224×224 RGB
image as `[batch, channels, height, width]`, returns a 1000-class
probability vector per row.

### 2b. Metadata: what kind of file is this, and is it new enough?

```sql
SELECT * FROM inference.onnx_inspect_meta('my-model/model.onnx');
```

```
 producer_name | producer_version | opset | ir_version | required_ops | file_size_bytes
---------------+------------------+-------+------------+--------------+-----------------
 pytorch       | 2.5.1            | 19    | 10         | [Conv, ...]  | 12345678
```

- **`producer_name` / `producer_version`**: which tool exported the file (useful for tracking down quirks).
- **`opset`**: the ONNX operator-set version the file requires.
- **`ir_version`**: the ONNX IR (intermediate representation) version.
- **`required_ops`**: every operator type the graph uses, alphabetised. Helps you anticipate compatibility issues.
- **`file_size_bytes`**: on-disk size; useful for sanity-checking a download.

### 2c. Will my runtime accept it?

```sql
SELECT * FROM inference.infer_compatibility('my-model/model.onnx');
```

```
 backend     | supported | opset_required | opset_supported | notes
-------------+-----------+----------------+-----------------+-------
 OnnxRuntime | true      | 19             | 22              |
```

If `supported = false`, the `notes` column tells you why. The most
common cause is *"File opset N exceeds backend ceiling M"* — your
model was exported against a newer ONNX opset than your installed ORT
supports. Fixes:

- Re-export with an older opset: `torch.onnx.export(..., opset_version=17)`.
- Re-save existing files with a lower IR/opset using the repo's
  `exort.py` script.

### 2d. What hardware can I target?

```sql
SELECT * FROM inference.devices();
```

```
 backend     | device   | available | reason                 | estimated_vram_mb
-------------+----------+-----------+------------------------+--------------------
 OnnxRuntime | Cpu      | true      |                        | 0
 OnnxRuntime | Cuda     | true      |                        | 0
 OnnxRuntime | DirectMl | true      |                        | 0
 OnnxRuntime | CoreMl   | false     | CoreML is macOS-only.  | 0
```

`available = true` rows are runnable on this machine. `WHERE available = false`
shows you what's missing and why. ORT's CPU EP is always available;
GPU EPs require the matching driver / runtime.

**Troubleshooting Step 2**

- *"`File not found`"* — `inference.onnx_inspect` resolves the path the
  same way `CREATE MODEL USING` does: `file://` prefix → absolute,
  otherwise relative to `$DATUMV_MODELS`. If you typed
  `'my-model/model.onnx'` and the file is at `E:\models\my-model\model.onnx`
  and `$DATUMV_MODELS = E:\models`, that's the right form. Try with the
  full path prefixed by `file:///` to bypass the relative resolution.
- *"`is_dynamic = true` for the batch dim, what do I do?"* — Nothing;
  this is normal. DatumV handles dynamic batch dims at the
  `infer()` boundary; you don't need to pad / pin them.
- *"`opset_required` is -1"* — Malformed file (no opset declared).
  Re-export the model.

## Step 3 — Generate a `CREATE MODEL` skeleton

Now that you know the model's signature, ask DatumV to draft a
starter `CREATE MODEL` body for you:

```sql
SELECT inference.model_skeleton('file:///e:/models/my-model/model.onnx');
```

(Note: `model_skeleton` is a scalar function and requires an absolute
path — it doesn't have access to the relative-path resolver yet.)

The output is a multi-line SQL string you can paste into your editor:

```sql
-- Generated CREATE MODEL skeleton. Rename `your_model_name` and edit the body before running.
--   source: e:/models/my-model/model.onnx
--   input  0: pixel_values  Float32[-1, 3, 224, 224]
--   output 0: logits  Float32[-1, 1000]
CREATE MODEL your_model_name(
    @pixel_values Float32[]
) RETURNS Float32[]
USING 'file:///e:/models/my-model/model.onnx'
AS BEGIN
    RETURN infer(@pixel_values)
END
```

The skeleton makes a few assumptions that you'll typically want to
revise:

- The parameter name (`@pixel_values`) is the raw ONNX tensor name. Rename to whatever your callers will want to type.
- For vision models, the input parameter is rarely a flat `Float32[]` — users usually pass an `Image`. You'll add preprocessing inside the body.
- The output type might want post-processing too (`softmax` for class
  probabilities, `argmax` for the predicted class id, etc.).

## Step 4 — Customise the body

Several common patterns cover most of the catalog. Each composes three families of helper functions inside the `CREATE MODEL` body:

- The **body-scoped dispatch surface** — `infer`, `infer_outputs`, `llama_chat`, `llama_generate`, and the `decode_*` family — calls into the loaded session. See [CREATE MODEL — body-scoped dispatch surface](sql/create-model.md#body-scoped-dispatch-surface).
- The **inference helpers** — preprocess (`yolox_preprocess`, `sam_preprocess`), pooling (`mean_pool_masked`), postprocess (`nms`, `mask_to_polygon`, `rtdetr_postprocess`) — turn raw ONNX tensors into typed outputs. See [Inference Functions](functions/inference.md). Image-tensor conversion (`image_to_tensor_chw`, `imagenet_mean`, ...) lives one page over in [Image Functions](functions/image.md).
- The **tokenization helpers** — `tokenizer.encode_bert`, `tokenizer.encode_bpe`, and the rest of the `tokenizer.*` family — turn `String` into the integer-id sequences transformer ONNX exports consume. See [Tokenization Functions](functions/tokenization.md).

### Vision classification (single class label)

```sql
CREATE MODEL classify_image(img Image) RETURNS Int32
USING 'file:///e:/models/my-model/model.onnx'
AS BEGIN
    DECLARE tensor Float32[] = image_to_tensor_chw(img, [224, 224], imagenet_mean(), imagenet_std());
    DECLARE probs  Float32[] = softmax(infer(tensor));
    RETURN argmax(probs)
END
```

What's happening:
1. `image_to_tensor_chw` resizes the image to 224×224 and converts to NCHW Float32, applying the ImageNet normalization the model was trained on. (For NHWC graphs, use `image_to_tensor_hwc` — same signature, interleaved output layout.)
2. `infer` runs the inference, returning the raw logits.
3. `softmax` converts the logits to a probability distribution.
4. `argmax` returns the index of the most-likely class.

### Image embedding (vector for similarity search)

```sql
CREATE MODEL embed_image(img Image) RETURNS Float32[]
USING 'file:///e:/models/clip-image/model.onnx'
AS BEGIN
    DECLARE tensor Float32[] = image_to_tensor_chw(img, [224, 224], clip_mean(), clip_std());
    RETURN l2_normalize(infer(tensor))
END
```

L2-normalising the output is the convention for embedding models — it
lets `cosine_similarity(a, b)` reduce to a simple dot product, and
makes cross-model distance comparisons fair.

### Text embedding (BERT-family encoder)

BERT-family encoders take three inputs (`input_ids`, `attention_mask`,
`token_type_ids`) and emit per-token hidden states. The sentence
embedding is the attention-mask-weighted mean over the seq_len axis,
L2-normalised:

```sql
CREATE OR REPLACE MODEL embed_text(text String) RETURNS Float32[]
USING 'all-minilm-l6-v2/model.onnx'
AS BEGIN
    -- WordPiece tokenize → Struct{input_ids, attention_mask, token_type_ids}
    -- with field names matching the ONNX input names.
    DECLARE encoded Struct = tokenizer.encode_bert(text, 'vocab.txt');
    DECLARE n Int32 = cardinality(encoded['input_ids']);

    -- Multi-input infer: struct of tensors + parallel struct of shapes.
    -- Every input has shape [1, seq_len] — two dynamic dims, so explicit
    -- shapes are required.
    DECLARE last_hidden_state Float32[] = infer(encoded, {
        input_ids:      [CAST(1 AS Int32), n],
        attention_mask: [CAST(1 AS Int32), n],
        token_type_ids: [CAST(1 AS Int32), n]
    });

    -- Average per-token embeddings along seq_len, weighting by mask, then
    -- project to the unit sphere. 384 = MiniLM-L6's hidden size.
    DECLARE pooled Float32[] = mean_pool_masked(
        last_hidden_state, encoded['attention_mask'], CAST(384 AS Int32));
    RETURN l2_normalize(pooled)
END
```

The shipped `models.all_minilm_l6_v2` SQL body is this exact shape. Key pieces:

- `tokenizer.encode_bert` returns the canonical BERT input bundle as one
  struct so multi-input `infer()` can match by field name. Relative
  paths inside a `CREATE MODEL` body resolve against the model's
  directory; pass `'vocab.txt'` and the engine finds the sibling file.
- The 2-arg `infer({values}, {shapes})` form is required for every BERT
  input — both dims are dynamic, so the per-input shape can't be
  inferred from a 1-d array length.
- `mean_pool_masked` is the standard sentence-transformers pooler.
  All-zero mask returns a zero vector instead of NaN.

For BPE-tokenized models (GPT-family, RoBERTa-derived) use
`tokenizer.encode` (unified `tokenizer.json`) or `tokenizer.encode_bpe`
(separate `vocab.json` + `merges.txt`).

### LLM (text-to-text)

```sql
CREATE MODEL chat(prompt String) RETURNS String
USING 'file:///e:/models/llama-3b/model.onnx'
AS BEGIN
    DECLARE ids   Int64[]  = tokenizer.encode(prompt, 'file:///e:/models/llama-3b/tokenizer.json');
    DECLARE logits Int64[] = infer(ids);
    RETURN tokenizer.decode(logits, 'file:///e:/models/llama-3b/tokenizer.json')
END
```

For multi-turn chat, prefer the SQL-defined LLM surface
(`llama_chat(session, messages, ...)` documented in
[CREATE MODEL § LLM dispatch scalars](sql/create-model.md#llm-dispatch-scalars))
over hand-rolling role-aware tokenization here. The chat scalar's body
calls llama.cpp's native `llama_chat_apply_template` against the GGUF's
embedded chat template, which is the only reliable way to tokenize
special role markers across quants.

### Object detection (bounding boxes)

```sql
CREATE MODEL detect(img Image,
                    conf_thresh Float32 = CAST(0.25 AS Float32)
                        CHECK (conf_thresh BETWEEN 0.0 AND 1.0)
                        STEP 0.05
                        COMMENT 'Per-box confidence floor.',
                    iou_thresh  Float32 = CAST(0.45 AS Float32)
                        CHECK (iou_thresh BETWEEN 0.0 AND 1.0)
                        STEP 0.05
                        COMMENT 'NMS IoU overlap threshold.')
    RETURNS Int32[]
USING 'file:///e:/models/yolo/model.onnx'
AS BEGIN
    DECLARE tensor Float32[] = image_to_tensor_chw(img, [640, 640]);
    DECLARE raw    Float32[] = infer(tensor);
    -- Assume the model returns [N_boxes * 4] flat + [N_boxes] scores
    -- (model-specific layout; check inference.onnx_inspect output).
    RETURN nms(raw, [...scores...], conf_thresh, iou_thresh)
END
```

`nms` returns the indices of boxes that survive non-maximum suppression.
The `CHECK` clauses surface as range sliders in the function-executor
UI and reject out-of-band call sites at runtime before any inference
work runs — see
[`docs/sql/create-model.md`](sql/create-model.md#parameter-constraints-check)
for the full clause grammar.

### Segmentation (mask to polygon)

```sql
CREATE MODEL outline(img Image) RETURNS Array<Point2D>
USING 'file:///e:/models/mask-rcnn/model.onnx'
AS BEGIN
    DECLARE tensor Float32[] = image_to_tensor_chw(img, [256, 256]);
    DECLARE mask   Float32[] = infer(tensor);
    RETURN mask_to_polygon(mask, 256, 256, CAST(0.5 AS Float32))
END
```

`mask_to_polygon` uses Marching Squares + Douglas-Peucker
simplification to convert a probability mask into a smooth polygon
contour — ready to overlay on the source image.

### Multi-output models (RT-DETR, RoBERTa QA, BlazeFace)

`infer()` returns the **first** declared output of the session — the
convention most ONNX exports follow (e.g. HuggingFace optimum lists
`last_hidden_state` ahead of `pooler_output`, U²-Net lists its final
fused saliency map ahead of the six deep-supervision aux outputs). For
models where you need more than just the first output — RT-DETR's
`logits` + `pred_boxes`, RoBERTa extractive-QA's `start_logits` +
`end_logits`, BlazeFace's `boxes` + `scores` — use **`infer_outputs()`**,
which returns every declared output as a `Struct` keyed by ONNX output
name:

```sql
CREATE MODEL detect(img Image) RETURNS Array<LabeledDetection>
USING 'file:///e:/models/rtdetr/model.onnx'
AS BEGIN
    DECLARE tensor Float32[] = image_to_tensor_chw(img, [640, 640]);
    DECLARE outputs Struct = infer_outputs(tensor);
    DECLARE logits Float32[] = outputs['logits'];
    DECLARE boxes  Float32[] = outputs['pred_boxes'];
    RETURN rtdetr_postprocess(logits, boxes, img)
END
```

Positional access (`outputs[0]`) also works when the ONNX output names
are unstable across exports (PyTorch's numeric defaults like `"1992"`).
The struct's element kinds are heterogeneous — Int64 token outputs
alongside Float32 logits is fine. Single-output sessions stay on plain
`infer()`; reaching for `infer_outputs()` is the explicit "I need every
output" signal.

## Step 5 — Call your model

Once registered, the model is callable from any query under
`models.<name>`:

```sql
-- Single row
SELECT models.classify_image(img) FROM images WHERE id = 1;

-- Batch
SELECT id, models.embed_image(img) AS embedding FROM images;

-- Filter by similarity
SELECT id FROM images
WHERE cosine_similarity(models.embed_image(img), models.embed_image(@reference)) > 0.8;
```

The model name and signature also surface in `system.models`:

```sql
SELECT name, kind, backend, status FROM system.models WHERE name = 'classify_image';
-- kind = 'declared' for CREATE MODEL registrations, 'builtin' for engine-baked models.
```

## Troubleshooting `CREATE MODEL` errors

### `Model file not found`

Path resolution issue. Confirm:

- The file actually exists at the path you typed.
- `$DATUMV_MODELS` is set and matches what you think.
- Relative paths (`'my-model/model.onnx'`) resolve against `$DATUMV_MODELS`. Absolute paths need the `file://` prefix.

Cross-check with `inference.onnx_inspect` on the same path — if that
works, `CREATE MODEL` will too.

### `No InferenceDispatcher is configured`

The host process didn't wire an inference dispatcher onto the catalog.
In a normal `datum-shell` session this happens automatically. If you're
running from custom code, you need:

```csharp
catalog.InferenceDispatcher = new InferenceDispatcher(
    [new OnnxRuntimeBackend()],
    NullLogger<InferenceDispatcher>.Instance);
```

### `File opset N exceeds backend ceiling M`

Your ONNX file was exported with a newer opset than the installed ORT
supports. Fix options:

- Re-export with an older opset:
  `torch.onnx.export(model, args, "out.onnx", opset_version=17)`.
- For optimum-cli: `optimum-cli export onnx --opset 17 --model <hf-id> ./out`.
- Bump your ORT package version.

The repo's `exort.py` script can also reset the opset on an existing file.

### `Model 'X' declares N file(s) but K are missing`

The model's catalog entry expected sidecar files (e.g.
`model.onnx_data` for >2GB models, separate `tokenizer.json`) that
aren't on disk. Re-download the full model directory.

### `infer() can only be called inside a CREATE MODEL body`

You typed `infer()` in a regular query (not inside a `CREATE MODEL ... AS BEGIN ... END` block). `infer()` needs the body's bound session
context; the plan-time gate refuses calls outside that context.

If you want to call a registered model from a regular query, use
`models.<name>(...)` instead of `infer()`.

### `Element kind X is not supported by infer()`

The v1 `infer()` accepts Float32 / Int32 / Int64 element kinds. Other
kinds (Float16, Bool, UInt8) need a one-line addition to
`InferFunction.AddInputTensor` + `ReadOutputTensor`. File an issue with
the model's `onnx_inspect` output.

### `Dimension mismatch` / `Got X elements but expected Y`

The shape solver couldn't reconcile your argument with the model's
declared input shape. Common causes:

- The model expects an image-sized tensor (`[1, 3, 224, 224]` = 150,528 floats) but your argument is a smaller flat vector. Use `image_to_tensor_chw` (NCHW) or `image_to_tensor_hwc` (NHWC) to produce the right shape.
- The model has multiple dynamic dims (`[batch, sequence_length]`).
  Pass the shape explicitly via the 2-arg form:
  `infer(value, [CAST(1 AS Int32), n])`. The shape array's product
  must equal the tensor's element count.

### Multi-input model errors

- *"infer() inputs struct has no field matching session input 'X'"* —
  Field names on the struct must match the ONNX input names (case-
  insensitive). Inspect the model via
  `SELECT name FROM inference.onnx_inspect('my-model/model.onnx') WHERE kind = 'input'`
  to confirm the names. The shipped MiniLM body uses `input_ids /
  attention_mask / token_type_ids` because that's what HuggingFace
  optimum's BERT export declares.
- *"single-output sessions only"* — Was a v1 restriction; lifted. v1
  now picks the first output for multi-output sessions (which matches
  HuggingFace optimum convention of listing the primary output first).
  Struct-of-tensors return for multi-output is a follow-up.

### Parameter constraint errors

- *"parameter 'x': value 1.5 is outside [0.0, 1.0]"* — a call site passed
  a value that fails the parameter's declared `CHECK` clause. The check
  runs before any inference work — fix the call site or relax the
  declaration. NULL values always pass any `CHECK`; use `IS NOT NULL` on
  the parameter to reject NULL.

### Tokenizer errors

- *"`tokenizer.json` declares model.type='Unigram'"* — `tokenizer.encode`
  supports BPE only. For BERT/WordPiece models, use
  `tokenizer.encode_bert(text, vocab_path)` which loads `vocab.txt` and
  returns the canonical `Struct{input_ids, attention_mask, token_type_ids}`
  bundle. For SentencePiece/Unigram models, look for separate
  `spiece.model` files (re-export options vary by model family).
- *"'tokenizer.json' is a relative path"* — scalar `tokenizer.encode` /
  `encode_bpe` / `decode*` don't have model-directory access from the
  scalar frame; pass an absolute path or `file://` URI. Inside a
  `CREATE MODEL` body, `tokenizer.encode_bert` resolves relative paths
  against the model's `USING` directory automatically.

## Looking up what's available

- **All registered models on this host**: `SELECT * FROM system.models;`
- **All preprocessing / postprocessing helpers**: `SELECT * FROM system.functions WHERE category IN ('Image', 'Vector', 'Activation', 'Encoding');`
- **Built-in models documentation**: [`models.md`](models.md).
- **Full `CREATE MODEL` syntax reference**: [`sql/create-model.md`](sql/create-model.md).
- **Engine internals**: [`technical/onnx-inference.md`](technical/onnx-inference.md).
