---
title: Models
---

# Model Zoo Reference

DatumIngest invokes machine-learning models through SQL functions in the
`models.X` namespace — `models.llama31_8b(prompt)`,
`models.yolox_s(image)`, `models.florence2_caption(image)`. This page
documents what's registered out of the box, where each weights file
comes from, and how to set up the model directory on a fresh machine.

For multi-turn LLM prompts, see the per-family chat-template scalar
functions in [Chat Templates](sql/chat-templates.md) — they expose the
role-header / turn-end primitives so a prompt can be assembled in
plain SQL alongside `string_agg`, with a `templated` opt-arg on the
model that bypasses the built-in single-message wrapper.

To register your own ONNX file as a model — without writing C# — see
[CREATE MODEL](sql/create-model.md). User-registered models surface in
the same `models.X` namespace as the catalog entries documented here.
The `kind` column on `system.models` distinguishes engine-baked
implementations (`builtin`, e.g. LLMs via LlamaSharp, multi-session
diffusion bundles) from SQL-defined registrations (`declared` — both
your own `CREATE MODEL` entries and the catalog-installed ONNX models
on this page whose entries carry an `installSql` field).

For an introspectable view of the same information, query the
`system_models` virtual table:

```sql
SELECT name, category, parameters, license, status
FROM system_models
ORDER BY category, name;
```

## Setup

### Models directory

DatumIngest reads model files from a directory configured in this
order:

1. `--models <path>` flag on `datum-shell`
2. `DATUM_MODELS` environment variable
3. Per-user fallback (`%LOCALAPPDATA%\DatumIngest\models` on Windows,
   `~/.local/share/DatumIngest/models` on Linux/macOS)

The recommended setup is to pick a directory with sufficient free
space and set the env var once:

```powershell
[Environment]::SetEnvironmentVariable('DATUM_MODELS', 'E:\models', 'User')
```

Reopen your terminal so the new variable propagates.

### Directory layout

Single-file models live as a flat `.onnx` or `.gguf` file directly
inside the models directory:

```
$DATUM_MODELS\
  yolox_s.onnx
  mobilenetv2-12.onnx
  Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf
  ...
```

Multi-file models live in a subfolder (the catalog entry's
`RelativePath` points at one anchor file inside the folder; the model
loader derives the rest from the parent directory):

```
$DATUM_MODELS\
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

## Model catalog

The model catalog is a manifest-driven library that lets users discover,
license-accept, install, and remove models from the in-app Models panel
or via REST. It replaces "drop files in `$DATUM_MODELS` and edit a config"
with a curated, versioned list of models keyed to HuggingFace repos.

Catalog files live in [`models/`](../models/) at the repo root and ship
alongside the binary:

```
models/
  catalog.json              ← the manifest
  licenses/                 ← license texts (offline-readable)
    mit.txt
    apache-2.0.txt
    cc-by-4.0.txt
    creativeml-openrail-m.md
    creativeml-openrail-pp-m.md
    llama-3.1-community.md
  upload-plan.json          ← uploads from local exports to huggingface.co/Heliosoph
  upload-readmes/           ← model-card templates for each upload
```

### Manifest schema

[`models/catalog.json`](../models/catalog.json) has three top-level sections.

**`licenses`** — every license referenced by any model, defined once.

```json
"licenses": {
  "mit": {
    "title": "MIT License",
    "spdx": "MIT",
    "canonicalUrl": "https://opensource.org/license/mit",
    "textFile": "licenses/mit.txt",
    "summary": "Permissive. Allows commercial use, modification, redistribution.",
    "requiresAcceptance": false
  },
  "creativeml-openrail-m": {
    "title": "CreativeML OpenRAIL-M License",
    "spdx": "CreativeML-OpenRAIL-M",
    "canonicalUrl": "https://huggingface.co/spaces/CompVis/stable-diffusion-license",
    "textFile": "licenses/creativeml-openrail-m.md",
    "summary": "Open license with use-based restrictions...",
    "requiresAcceptance": true
  }
}
```

`textFile` is a path relative to `models/`. The app reads it from disk to
render the acceptance modal — the license text ships with the binary so
acceptance works offline.

**`tiers`** — named bundles for one-click multi-model installs.

```json
"tiers": {
  "starter":     ["all-minilm-l6-v2", "phi-3.5-mini-instruct-gguf", "toxic-bert"],
  "recommended": ["all-minilm-l6-v2", "bge-small-en-v1.5", "bge-reranker-base", ...]
}
```

**`models`** — the model entries. Each one:

```json
{
  "id": "absolute-reality-hyper",
  "displayName": "AbsoluteReality + Hyper-SD (4-step)",
  "description": "SFW general-purpose SD 1.5 fine-tune + 4-step distillation.",
  "task": "text-to-image",
  "tags": ["text-to-image", "sd-1.5", "hyper-sd", "photorealistic"],
  "licenseIds": ["creativeml-openrail-m"],
  "hardware": { "minRamMb": 2048, "minVramMb": 4096, "preferred": "gpu" },
  "source": {
    "type": "huggingface",
    "repo": "Heliosoph/absolute-reality-hyper-onnx",
    "revision": "57298a3ec4a333002f9b5fc127e0cc57fbe4d338",
    "include": ["**/*"]
  },
  "approxSizeMb": 4096
}
```

Field reference:

| Field | Type | Description |
|-------|------|-------------|
| `id` | string | Stable identifier. Used in API paths and as the local folder name. |
| `displayName` | string | Human-readable label for the UI. |
| `description` | string | One-line summary for list views. |
| `task` | string | Free-form category (`embeddings`, `llm`, `text-to-image`, `reranker`, `ner`, `toxicity`, `vision-language`, `object-detection`, `text-to-speech`, ...). |
| `tags` | string[] | Filter chips and search keywords. |
| `licenseIds` | string[] | References into `licenses`. Multiple = the model bundles multiple licenses. |
| `hardware.minRamMb` | int | Minimum system RAM, in MiB. |
| `hardware.minVramMb` | int | Minimum GPU VRAM, in MiB. `0` = CPU-only. |
| `hardware.preferred` | string | `cpu` \| `gpu` — informational, not enforced. |
| `source.type` | string | `huggingface`. (Only source type defined.) |
| `source.repo` | string | `<org>/<repo>` on HuggingFace. |
| `source.revision` | string | Pinned commit sha (40 hex chars) or `main`. Pinned shas give reproducibility. |
| `source.include` | string[] | Glob patterns filtering the HF repo tree. `**/*`, `*.onnx`, `onnx/*` all supported. |
| `approxSizeMb` | int | Disk-budget estimate. The authoritative size comes from the HF tree API at install time. |
| `placeholder` | bool | When `true`, the source repo has not been uploaded yet. The installer refuses to download placeholders. |
| `requiresHfLogin` | bool | The source repo is gated by HuggingFace; user must paste a token before download. Independent of license acceptance. |

### License acceptance

Two independent gates can block a download:

1. **App acceptance** — driven by the license's `requiresAcceptance` field.
   The app shows the license text and requires an explicit "I accept"
   click. State persists in `<catalogRoot>/license-acceptance.json` as a
   list of accepted license IDs.

2. **HuggingFace login** — driven by the model's `requiresHfLogin` field.
   The HF source repo itself is gated. The downloader detects 401/403 and
   prompts for an HF token.

A model may require neither, either, or both. MIT/Apache models pass
straight through. OpenRAIL-M variants require app acceptance only.
`meta-llama/*` weights (re-hosted with gating preserved) would require
both.

License acceptance is per-license, not per-model. Once a user accepts
OpenRAIL-M for AbsoluteReality, all five other Hyper-SD variants install
without re-prompting.

### Source resolution and verification

Installation pulls a model's files in three steps:

1. **Tree API**: `GET huggingface.co/api/models/<repo>/tree/<revision>?recursive=true`
   returns every file at the pinned revision with size and (for LFS
   files) `lfs.oid` = `sha256:<hex>`.
2. **Include filtering**: paths are matched against `source.include`
   using glob semantics (`**`, `*`, literal names). Directory entries
   are dropped.
3. **Streamed download** of each matched file:
   `GET huggingface.co/<repo>/resolve/<revision>/<path>` follows HF's
   redirect to S3. The downloader streams bytes into a `.part` file,
   computes SHA-256 incrementally, then atomically renames to the final
   path on success.

LFS files are verified against the tree's `lfs.sha256` field. Non-LFS
files (small JSON/text) are not checksummed — they're git blobs stored
inline and protected by HTTPS.

Files install into a per-model subdirectory of the resolved models
directory:

```
<modelsDirectory>/
  absolute-reality-hyper/
    model_index.json
    unet/
      model.onnx
      model.onnx_data
    ...
```

### Install lifecycle

The download orchestrator runs in the background and broadcasts events
over the SignalR stream hub:

| Event | When | Payload |
|-------|------|---------|
| `OnModelDownloadStarted` | After license + tree resolution succeed | `{ modelId, fileCount, totalBytes }` |
| `OnModelDownloadProgress` | Throttled to ~10 Hz during streaming | `{ modelId, currentFile, fileIndex, fileCount, bytesReadInFile, bytesTotalInFile, bytesReadTotal, bytesTotalAcrossModel }` |
| `OnModelDownloadComplete` | All files written and verified | `{ modelId }` |
| `OnModelDownloadFailed` | Any step throws | `{ modelId, error }` |

Events are broadcast to all connected clients. Concurrent installs of
the same model id are prevented; concurrent installs of different model
ids are allowed.

### REST API

The `/api/model-catalog` controller is the HTTP surface. All responses
are JSON unless noted.

| Method | Path | Returns | Notes |
|--------|------|---------|-------|
| `GET` | `/api/model-catalog` | `CatalogManifest` | Full manifest. Fetched once at app startup. |
| `GET` | `/api/model-catalog/licenses/{id}/text` | `text/plain` | Raw license text for the acceptance modal. |
| `POST` | `/api/model-catalog/licenses/{id}/accept` | `204` | Idempotent. |
| `GET` | `/api/model-catalog/licenses/accepted` | `string[]` | List of accepted license IDs. |
| `GET` | `/api/model-catalog/models/{id}/state` | `notInstalled` \| `partial` \| `installed` | Filesystem probe against HF tree sizes. |
| `POST` | `/api/model-catalog/models/{id}/install` | `202` | Kicks off background download. |
| `DELETE` | `/api/model-catalog/models/{id}` | `204` | Deletes the local model directory. |

Error codes on `install`:

| Status | Body | Meaning |
|--------|------|---------|
| `404` | — | Unknown model id. |
| `409` | `{ error: "install_blocked", message }` | Placeholder model or install already in progress. |
| `412` | `{ error: "license_not_accepted", licenseId, message }` | At least one required license is unaccepted. |

### Module layout

The C# implementation lives under
[`DatumIngest.Web/ModelLibrary/`](../src/DatumIngest.Web/ModelLibrary/):

| File | Role |
|------|------|
| `CatalogManifest.cs` | POCOs for the JSON shape, including the polymorphic `CatalogSource` hierarchy (`HuggingFaceSource` / `GithubReleaseSource` / `HttpsSource`). |
| `IManifestStore.cs` / `ManifestStore.cs` | Singleton loader for `catalog.json` + license texts. |
| `IModelSourceClient.cs` | Per-channel download contract. One implementation per source `type` discriminator. |
| `HuggingFaceSourceClient.cs` | `HttpClient`-based wrapper for the HF tree + resolve APIs. Streams + verifies LFS sha256s. |
| `GithubReleaseSourceClient.cs` | Fetches GitHub-release assets directly. No upstream checksum API — HTTPS trust. |
| `HttpsSourceClient.cs` | Escape hatch for one-off URLs (Qualcomm AI Hub S3, vendor mirrors). HTTPS trust. |
| `HttpFileDownloader.cs` | Shared resume/hash streaming core for the github + https clients. |
| `ILicenseAcceptanceService.cs` / `LicenseAcceptanceService.cs` | JSON-file persistence of accepted license IDs. |
| `IModelDownloadService.cs` / `ModelDownloadService.cs` | Orchestrator. Probe / install / uninstall + sequential source-fallback loop. Pushes events via SignalR. |
| `ModelDownloadEvents.cs` | Records broadcast over the hub. |

The controller lives at
[`DatumIngest.Web/Api/ModelCatalogController.cs`](../src/DatumIngest.Web/Api/ModelCatalogController.cs).
Hub events are declared on
[`IStreamHubClient`](../src/DatumIngest.Web/Hubs/IStreamHubClient.cs).

Manifest path resolution looks for `models/catalog.json` first under
`AppContext.BaseDirectory` (ship layout) and then walks up parent
directories (dev layout, where the working dir is the project folder).

### Re-hosting workflow

Models that DatumIngest converts itself (the SD Hyper variants, the
Florence quantizations, ViT-GPT2, YOLOX bundle, Kokoro voices) live
under `huggingface.co/Heliosoph/*`. The upload plan and per-model
README templates are tracked in
[`models/upload-plan.json`](../models/upload-plan.json) and
[`models/upload-readmes/`](../models/upload-readmes/).

For each upload:

1. Copy the matching template from `models/upload-readmes/<repo-suffix>.md`
   into the local model folder as `README.md`.
2. Drop the upstream `LICENSE` files into the same folder (sources are
   listed in the upload plan's `licenseFiles` array).
3. `huggingface-cli login` (one-time).
4. `huggingface-cli upload Heliosoph/<repo-suffix> <localFolder> --repo-type model`.
5. Copy the resulting commit sha into the model's `source.revision` in
   `catalog.json` and remove its `"placeholder": true` flag.

### Adding a new model

1. Identify the HuggingFace repo and pin a commit sha.
2. If the license isn't already in the `licenses` block, add it. Drop
   the canonical license text under `models/licenses/<id>.{txt,md}`.
3. Add a model entry under `models`. Reference the license by ID in
   `licenseIds`. Fill in `hardware` based on the upstream model card.
4. If the source repo doesn't exist yet (Heliosoph upload pending),
   mark the entry `"placeholder": true`. The installer will refuse to
   download it until the flag is removed.
5. Optionally add the new model id to `tiers.starter` or
   `tiers.recommended` if it belongs in a bundle.

The manifest is loaded once at app startup. Restart the host process
after editing `catalog.json`.

## Models reference

### `mobilenetv2` — image classifier

- **What it does**: Top-1 ImageNet-1k classification. Returns
  `ScoredLabel` (`Struct<label: String, score: Float32>`).
  `IMPLEMENTS LabeledImageClassifier`.
- **License**: Apache-2.0 (Google AI Research / ONNX Model Zoo)
- **Source**: [github.com/onnx/models](https://github.com/onnx/models/tree/main/validated/vision/classification/mobilenet)
  — re-hosted on HuggingFace at `Heliosoph/mobilenetv2-onnx` with the
  ImageNet-1k label JSON alongside the ONNX file.
- **Install**: catalog id `mobilenetv2`. Installs through the Model
  Manager — the downloader fetches `mobilenetv2-12.onnx` +
  `imagenet-classes.json` and runs
  [`models/sql/mobilenetv2.sql`](../models/sql/mobilenetv2.sql) to
  register `models.mobilenetv2`.
- **SQL body**: `image_to_tensor_chw` (224×224, ImageNet stats) →
  `infer` → `softmax` → `argmax` → `read_string_list('imagenet-classes.json')`
  → return `{label: labels[top], score: probs[top]}`.

### YOLOX — object detector ladder

Megvii's YOLOX detector family registered as seven sibling catalog
entries spanning the full speed/accuracy spectrum. Same architecture,
same COCO-80 vocabulary, different parameter counts. Each variant is a
SQL-defined model registered via its own `installSql` entry; all
declare `IMPLEMENTS LabeledObjectDetector RETURNS Array<LabeledDetection>`
(`Struct<bbox: BoundingBox, label: String, score: Float32>`).

- **License**: Apache-2.0 (Megvii)
- **Source**: [github.com/Megvii-BaseDetection/YOLOX](https://github.com/Megvii-BaseDetection/YOLOX)
  — re-hosted on HuggingFace at `Heliosoph/yolox-onnx` with
  `coco-classes.json` bundled alongside the ONNX files.
- **Install**: catalog ids `yolox-nano`, `yolox-tiny`, `yolox-s`,
  `yolox-m`, `yolox-l`, `yolox-x`, `yolox-darknet`. Each catalog entry
  carries an `installSql` field pointing at
  [`models/sql/yolox-*.sql`](../models/sql/) so the downloader registers
  the SQL-defined model after fetching weights.
- **SQL body**: `yolox_preprocess` (letterbox + BGR + raw 0-255 NCHW) →
  `infer` → `read_string_list('coco-classes.json')` → `yolox_postprocess`
  (decoder + class-aware NMS + reverse letterbox + label lookup).

| Catalog id | Model name (in SQL) | File | Params | Input size | Disk |
|---|---|---|---|---|---|
| `yolox-nano` | `yolox_nano` | `yolox_nano.onnx` | 0.91M | 416×416 | ~3 MB |
| `yolox-tiny` | `yolox_tiny` | `yolox_tiny.onnx` | 5.06M | 416×416 | ~20 MB |
| `yolox-s` | `yolox_s` | `yolox_s.onnx` | 9.0M | 640×640 | ~36 MB |
| `yolox-m` | `yolox_m` | `yolox_m.onnx` | 25.3M | 640×640 | ~98 MB |
| `yolox-l` | `yolox_l` | `yolox_l.onnx` | 54.2M | 640×640 | ~200 MB |
| `yolox-x` | `yolox_x` | `yolox_x.onnx` | 99.1M | 640×640 | ~378 MB |
| `yolox-darknet` | `yolox_darknet` | `yolox_darknet.onnx` | 63.7M | 640×640 | ~250 MB |

Nano and tiny use 416×416 input (smaller, faster); the others use
640×640. The `yolox_preprocess` + `yolox_postprocess` scalars pick up
the right anchor count from the `target_size` arg, so a single pair
of functions serves all seven variants.

### Face detection — see `mediapipe-face`

SCRFD-10G was previously registered here. **Removed 2026-05-17** after a
license review: InsightFace's repo is MIT for *code* only — the
pretrained ONNX weights aren't openly licensed for redistribution.
Same constraint applies to RetinaFace (sibling InsightFace project).

Use `mediapipe-face` (BlazeFace-based, Apache-2.0 throughout) from the
catalog for face detection — install it via the Model Manager.

### `paddleocr_v4_det` — text detector (pairs with TrOCR)

PaddleOCR's PP-OCRv4 detection model — DBNet-style segmentation
network that finds bounding boxes around lines of printed text.
Detection only; pair with a recognizer like `trocr_printed` for a
two-stage OCR pipeline.

- **What it does**: Returns `Array<RegionScore>`
  (`Struct<bbox: BoundingBox, score: Float32>`). Boxes are in
  original-image pixel coordinates. `IMPLEMENTS TextDetector`.
- **License**: Apache-2.0 (PaddlePaddle)
- **Source**: [github.com/PaddlePaddle/PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR)
  — re-hosted on HuggingFace at `Heliosoph/paddleocr-v4-det-onnx`.
- **Install**: catalog id `paddleocr-v4-det`. The catalog entry's
  `installSql` points at
  [`models/sql/paddleocr-v4-det.sql`](../models/sql/paddleocr-v4-det.sql);
  the downloader fetches `ch_PP-OCRv4_det.onnx` and registers
  `models.paddleocr_v4_det`.
- **SQL body**: `image_resize_to_stride` (longest side ≤ 960, multiples
  of 32) → `image_to_tensor_chw` (ImageNet stats) → `infer` →
  `dbnet_postprocess` (threshold + connected-components BFS + DBNet
  polygon unclip + scale back to original-image pixel space).
- **Hyperparameter defaults** (PaddleOCR canonical): `pixel_threshold=0.3`,
  `box_score_threshold=0.6`, `min_size=3` px, `unclip_ratio=1.5`. The
  defaults are baked into the SQL body; for different values, copy the
  SQL file and create your own `CREATE MODEL` variant.
- **Architecture**: DBNet — a U-Net-style backbone produces a
  single-channel sigmoid probability map at the input resolution.
  `dbnet_postprocess` thresholds the map, runs BFS connected-components
  (4-connectivity), accepts each component with mean probability ≥
  `box_score_threshold` and tight bbox ≥ `min_size` px on each side, and
  unclips each axis-aligned bbox by
  `distance = area × unclip_ratio / perimeter`.
- **Detect-only model.** This finds *where* the text is, not *what*
  it says. For receipt-style transcription, pair with `trocr_printed`
  / `trocr_printed_fp16` via `image_crop`:
- **Demos**:
  ```sql
  -- Two-stage OCR: detect text boxes, recognize each one.
  SELECT
    receipt_id,
    det.score AS detect_confidence,
    models.trocr_printed_fp16(
      image_crop(photo, det.bbox.x, det.bbox.y, det.bbox.w, det.bbox.h)) AS line
  FROM receipts
  CROSS APPLY UNNEST(models.paddleocr_v4_det(photo)) AS det
  WHERE det.score > 0.7
  ORDER BY receipt_id, det.bbox.y, det.bbox.x;

  -- Just the detector — visualise box counts per image.
  SELECT
    photo_id,
    array_length(models.paddleocr_v4_det(photo)) AS line_count
  FROM scanned_pages;
  ```

### `realesrgan_general_x4` — image super-resolution

- **What it does**: 4× super-resolution. Takes an `Image` of any size,
  returns an `Image` (PNG bytes) at 4× width and 4× height. Real-ESRGAN's
  Compact (SRVGGNet) "general-content" variant — trained on real-world
  degradations across diverse photographic content (not anime). The
  lightweight backbone keeps it fast enough to run per-row in a SQL
  pipeline without tiling for typical photo sizes.
- **License**: BSD-3-Clause (Xintao Wang)
- **Source**: [github.com/xinntao/Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN)
  — upstream ships PyTorch `.pth` weights only. The single-input ONNX
  export is mirrored at
  [huggingface.co/OwlMaster/AllFilesRope](https://huggingface.co/OwlMaster/AllFilesRope/blob/main/realesr-general-x4v3.onnx).
- **Files**:
  - `realesr-general-x4v3.onnx` (~10 MB) — single-input ONNX. The
    dual-weight (`dni_weight`-input) variant is **not** supported; the
    loader rejects it at construction with a clear error.
- **Setup**:
  ```powershell
  Invoke-WebRequest "https://huggingface.co/OwlMaster/AllFilesRope/resolve/main/realesr-general-x4v3.onnx" `
    -OutFile $env:DATUM_MODELS\realesr-general-x4v3.onnx
  ```
- **Memory**: V1 runs whole-image inference, no tiling. The float NCHW
  intermediates cost `3 × H × W × 4` bytes for input plus
  `3 × (H·4) × (W·4) × 4` bytes for output — a 1024×1024 input at 4×
  spends ~210 MB on intermediates. For typical phone-camera thumbnails
  (≤512×512), whole-image is fine. See the **tile_size follow-up** below
  for the planned escape hatch when this matters.
- **Architecture**: SRVGGNetCompact — a stack of 3×3 conv + ReLU blocks
  feeding a final pixel-shuffle 4× upsample. Preprocessing is raw
  `pixel / 255` in NCHW RGB; no per-channel mean/std. Output is also
  `[0, 1]` floats; the model class clips, scales to `[0, 255]`, and
  hands a live `SKBitmap` to the consumer (no PNG round-trip — the
  downstream renderer or follow-up model gets pixels directly).
- **Per-call overrides**:
  - `[0] outscale` (Float64) — output size relative to the input.
    Default is the native `4.0`. Valid range is `[1.0, 4.0]`; values
    above 4 are rejected because the model can't produce more pixels
    than its architecture supports. Internally the model always infers
    at native 4× and then SkiaSharp-resizes the result to match
    `outscale` when it isn't 4 — useful when you want a non-power-of-4
    target resolution (e.g. `outscale=2.5` to upscale a 512px thumbnail
    to 1280px). `outscale=1.0` runs the model purely for its baked-in
    denoising and returns the result at the input's original size.
- **Demo**:
  ```sql
  -- Upscale a folder of thumbnails to 4× their size.
  SELECT
    photo_id,
    models.realesrgan_general_x4(thumb) AS hi_res
  FROM thumbnails LIMIT 10;

  -- 2.5× upscale instead of the native 4×.
  SELECT models.realesrgan_general_x4(thumb, 2.5) AS hi_res
  FROM thumbnails LIMIT 10;
  ```
- **Follow-up — `tile_size`**: not yet implemented. Tile-based inference
  slices the input into overlapping `tile_size × tile_size` patches,
  runs the model on each, and stitches the upscaled tiles back into
  the full output. The motivating use case is multi-megapixel inputs
  where whole-image inference is impractical: a 3840×2160 4K photo at
  4× costs ~1.6 GB of intermediate floats and won't fit in typical
  consumer VRAM. With `tile_size=512, tile_pad=10`, the same input
  runs as ~40 independent 512×512 patches sharing one bounded working
  set. The reference is upstream's `RealESRGANer.tile_process`
  ([github.com/xinntao/Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN)).
  Until this lands, stick to inputs ≤2048×2048 for the safest VRAM
  budget on a 12 GB card.

### `u2net`, `u2netp` — salient-object segmentation (background masking)

Xuebin Qin's U²-Net detects the dominant ("salient") object in an image and
emits a single-channel mask sized to match the input. Two SQL-defined model
registrations sharing the same pipeline (only the ONNX file differs); both
declare `IMPLEMENTS BackgroundRemover RETURNS Image`.

| Catalog id | Model name (in SQL) | File | Params | Disk |
|---|---|---|---|---|
| `u2net` | `u2net` | `u2net.onnx` | 176M | ~170 MB |
| `u2netp` | `u2netp` | `u2netp.onnx` | 4.7M | ~4.7 MB |

- **What it does**: Returns an `Image` whose pixel intensity is the
  per-pixel saliency (white = foreground / object, black = background).
  Channels are equal (R = G = B = mask value, A = 255), so any colour-space
  consumer reads the same value — but the natural pairing is
  [`image_cutout(image, mask)`](#image_cutout--apply-a-mask-as-alpha) which
  uses the mask as an alpha channel.
- **License**: Apache-2.0 (Xuebin Qin et al.)
- **Source**: [github.com/xuebinqin/U-2-Net](https://github.com/xuebinqin/U-2-Net)
  — re-hosted on HuggingFace at `Heliosoph/u2net-onnx` (both variants
  live in the same repo, differentiated by file include).
- **Install**: catalog ids `u2net` and `u2netp`. Each entry's `installSql`
  points at [`models/sql/u2net.sql`](../models/sql/u2net.sql) /
  [`models/sql/u2netp.sql`](../models/sql/u2netp.sql).
- **SQL body**: `image_to_tensor_chw` (320×320, ImageNet stats) → `infer` →
  `depth_map_to_image` (per-image min-max normalize + grayscale-pack +
  bilinear resize to original image dims). **Zero new model-specific
  scalars** — the body is pure composition, sharing the
  `depth_map_to_image` post-processor with MiDaS / DPT.
- **Architecture**: Nested U-shaped encoder/decoder of "ReSidual U-blocks"
  (RSUs) — each level is itself a small U-Net, hence the U². The full and
  lite variants share the topology; `u2netp` swaps the heavy 3×3 conv stacks
  for cheaper depthwise-separable equivalents. The ONNX export emits seven
  deep-supervision tensors (d0..d6); `infer()` picks d0 (the final fused
  saliency map), matching the v1 "first output" convention.
- **Memory**: input bitmap + 320×320×3 float NCHW (~1.2 MB) + 320×320 float
  output (~0.4 MB) + a pair of mask bitmaps for the resize-back. Per-row
  cost is essentially fixed regardless of input resolution, since the
  internal pipeline always operates at 320×320.
- **`u2net` vs `u2netp`**: the lite variant matches the full one on
  obvious foreground subjects (a person against a plain background, a
  product photo) and starts to lose precision on fine boundaries (single
  hairs, leaf edges, jewelry filigree). For demos and interactive use,
  start with `u2netp` — it loads in <1s vs ~3s for the full model and
  inference is roughly 4× faster — and reach for `u2net` only when mask
  quality matters more than latency.
- **Demo**:
  ```sql
  -- Cut the salient subject out of each photo and write the result back as PNG.
  SELECT
    photo_id,
    image_cutout(photo, models.u2netp(photo)) AS subject_only
  FROM photos
  LIMIT 5;
  ```

### `mobilesam_prompted`, `mobilesam` — point-prompted and "everything" segmentation

Meta's Segment Anything (TinyViT-distilled MobileSAM variant). Two
sibling registrations driven by the same `MobileSamModel` class:

| Catalog name | SQL surface | Output |
|---|---|---|
| `mobilesam_prompted` | `models.mobilesam_prompted(image, x, y)` | `Image` — one mask of the object that contains the click |
| `mobilesam` | `models.mobilesam(image, [gridSize])` | `Array<Image>` — one mask per object the model finds |

Where U²-Net guesses the salient object for you, MobileSAM-prompted
lets you say "segment *this thing here*" — useful for cutting a
specific subject out of a photo with several candidates, or scripting a
multi-step pipeline that picks coordinates from another model
(face-detector → SAM on the face-box centre, etc.). MobileSAM-everything
sweeps the image with a grid of prompts and returns every distinct
object it finds, suitable for building a per-photo segment library.

- **What it does**:
  - **`mobilesam_prompted(image, x, y)`** — returns a single `Image`
    whose white pixels are the segmented object containing the click and
    black pixels are everything else.
  - **`mobilesam(image, [gridSize])`** — sweeps a `gridSize × gridSize`
    grid of foreground prompts across the image, runs the decoder for
    each, drops low-confidence and unstable candidates, NMS-deduplicates
    the rest, and returns the survivors as `Array<Image>`. Default
    `gridSize` is **32** (1024 prompts, the SAM canonical default);
    pass a smaller value for faster batches at the cost of missing
    small objects.

  Each mask is sized to match the input image. Equal-channel RGBA
  matches the U²-Net mask convention so the same `image_cutout(image,
  mask)` consumer applies.
- **License**: Apache-2.0 (Meta AI / Kyung Hee University)
- **Source**:
  [github.com/ChaoningZhang/MobileSAM](https://github.com/ChaoningZhang/MobileSAM)
  — TinyViT distillation of the original ViT-H SAM image encoder. Mask
  decoder weights are unchanged from upstream SAM, just re-exported to
  ONNX.
- **Files** — two ONNX files at the root of `$DATUM_MODELS`:
  - `mobile_sam_image_encoder.onnx` (~28 MB) — TinyViT image encoder.
    Outputs a `[1, 256, 64, 64]` embedding the decoder consumes.
  - `sam_mask_decoder_multi.onnx` (~16 MB) — multi-mask decoder. Emits
    four candidate masks per prompt plus a per-mask predicted-IoU
    score; the model class picks the highest-scoring candidate.
  - `sam_mask_decoder_single.onnx` (~16 MB) — single-mask decoder
    sibling. Either decoder file works at the registration site; the
    multi version is the default because the IoU-based best-of-4
    selection gives slightly better quality at no meaningful runtime
    cost.
- **Setup**: produced by the [vietanhdev/samexporter](https://github.com/vietanhdev/samexporter)
  pipeline. Pre-built ONNX exports are mirrored on Hugging Face under
  several community repos (search "mobilesam onnx"). Drop the encoder
  and one of the decoder files directly into `$DATUM_MODELS`.
- **Architecture**: TinyViT image encoder + a six-input prompt-conditioned
  mask decoder. The decoder's six inputs are `image_embeddings`,
  `point_coords`, `point_labels`, `mask_input`, `has_mask_input`, and
  `orig_im_size`; we hand zeros for the prior-mask channel
  (`has_mask_input=0` disables it) and feed the real prompt point plus a
  `(0, 0)` padding sentinel with label `-1` to satisfy the decoder's
  expected `≥ 2` point arity.
- **Coordinate convention**: `x` and `y` are in original-image pixel
  space, top-left origin. `(0, 0)` is the top-left corner; `x` grows
  right, `y` grows down. Both the input bitmap and the prompt are
  rescaled inside the model class by `scale = 1024 / max(W, H)` before
  the encoder pass — the encoder graph zero-pads to 1024×1024 but does
  not itself resize-longest-side, while the decoder's mask resize-back
  expects the resize to have happened. Doing the rescale on the C# side
  keeps the encoder's view and the decoder's view aligned.
- **Memory**: encoder pass dominates (~150 MB of intermediate
  activations on a 1024-square padded input); decoder is comparatively
  cheap. One encoder forward per row; everything-mode adds `gridSize²`
  decoder dispatches per row on top. Two ONNX sessions stay resident for
  the catalog entry's lifetime (~45 MB of weights total).
- **Cost (everything mode)**: encoder once, decoder `gridSize²` times.
  At the default `gridSize=32` that's ~1024 decoder dispatches per
  image, ~3-5 seconds on CPU. `gridSize=16` cuts that to ~250ms but
  misses small objects; `gridSize=8` is fast enough for interactive
  use but only finds large salient regions. Everything-mode also sets
  `PreferredBatchSize=1` so multi-row queries surface results
  row-by-row instead of waiting for the full upstream batch.
- **Quality filters (everything mode)**: SAM canonical defaults baked
  in — predicted-IoU ≥ 0.88, stability score ≥ 0.95 (IoU between mask
  thresholded at +δ vs −δ with δ=1.0), NMS overlap threshold 0.7. Not
  exposed as overrides today; tweak the consts in `MobileSamModel` if
  needed.
- **Demos**:
  ```sql
  -- Prompted: cut a face out of a photo using MediaPipe Face's
  -- bounding-box centre as the SAM prompt. The output is just the
  -- face region; everything else is masked away.
  SELECT
    photo_id,
    image_cutout(
      photo,
      models.mobilesam_prompted(
        photo,
        face.bbox.x + face.bbox.w / 2,
        face.bbox.y + face.bbox.h / 2)) AS face_only
  FROM photos
  CROSS APPLY UNNEST(models.mediapipe_face(photo)) AS face
  LIMIT 5;

  -- Everything mode: one row per detected segment, ~5-30 segments per
  -- photo at the default grid size.
  SELECT
    photo_id,
    ord                              AS segment_index,
    image_cutout(photo, mask)        AS object_only
  FROM photos
  CROSS APPLY UNNEST(models.mobilesam(photo)) WITH ORDINALITY AS m(mask, ord)
  LIMIT 50;

  -- Faster sweep with a smaller grid: 8×8 = 64 prompts instead of
  -- 1024. Misses small objects but finishes in a fraction of a second.
  SELECT
    photo_id,
    models.mobilesam(photo, 8) AS masks
  FROM photos LIMIT 5;
  ```

### `midas_small`, `dpt_large` — monocular depth estimation

Intel ISL's MiDaS / DPT depth-estimation family. Two SQL-defined model
registrations sharing the same pipeline shape but differing in input
resolution, channel order, and normalisation stats. Both declare
`IMPLEMENTS DepthEstimator RETURNS Image`.

| Catalog id | Model name (in SQL) | File | Params | Input | Disk |
|---|---|---|---|---|---|
| `midas-small` | `midas_small` | `midas_v21_small_256.onnx` | 21M | 256×256 | ~83 MB |
| `dpt-large` | `dpt_large` | `dpt_large_384.onnx` | 344M | 384×384 | ~1.4 GB |

- **What it does**: Returns an `Image` whose pixel intensity is **relative
  depth** — bigger value = closer to the camera. Single-channel grayscale
  encoded as RGBA-with-equal-channels (matching the `u2net` mask convention
  so any colour-space consumer reads the same value). The depth map is
  resized back to the input image's original dimensions; the network's
  internal working size (256 / 384) doesn't leak through.
- **License**: MIT (Intel ISL)
- **Source**: [github.com/isl-org/MiDaS](https://github.com/isl-org/MiDaS)
  — re-hosted on HuggingFace at `Heliosoph/midas-small-onnx` and
  `Heliosoph/dpt-large-onnx`.
- **Install**: catalog ids `midas-small` and `dpt-large`. Each entry's
  `installSql` points at
  [`models/sql/midas-small.sql`](../models/sql/midas-small.sql) /
  [`models/sql/dpt-large.sql`](../models/sql/dpt-large.sql).
- **SQL body**: `image_to_tensor_chw` (for DPT — RGB, 0.5/0.5 norm) or
  `image_to_tensor_chw_bgr` (for MiDaS — BGR, ImageNet stats) → `infer`
  → `depth_map_to_image` (per-image min-max normalise + grayscale-as-RGBA
  pack + bilinear resize to original image dims).
- **Architecture**: MiDaS-small v2.1 is an EfficientNet-Lite3 encoder + lightweight
  decoder; DPT-Large is a ViT-Large encoder + DPT (dense-prediction transformer)
  decoder. Both are trained on a mixture of depth datasets via the MiDaS
  loss (scale-and-shift invariant), so the output is **relative inverse depth
  in arbitrary units** — not metric. Preprocessing differs between the two:
  MiDaS-small uses BGR + ImageNet stats (`mean=[0.485,0.456,0.406]`,
  `std=[0.229,0.224,0.225]`); DPT uses RGB + half/half stats
  (`mean=[0.5,0.5,0.5]`, `std=[0.5,0.5,0.5]`). The two SQL bodies pick the
  matching preprocess scalar.
- **Memory**: input bitmap + (256² or 384²) × 3 float NCHW input + same-size
  float output — ~1 MB per side at 256×256, ~2 MB at 384×384. Per-row cost is
  fixed regardless of input resolution since the network always operates at
  its native size.
- **Aspect-ratio note**: the upstream Python pipeline preserves aspect ratio
  with `keep_aspect_ratio=True, multiple_of=32`; this implementation
  square-stretches to the network's input size. Cheaper and avoids
  letterboxing artefacts in the depth map; costs a small amount of accuracy
  on extreme aspect ratios (panoramas, tall portraits). Re-evaluate if depth
  quality matters more than throughput.
- **`midas_small` vs `dpt_large`**: DPT-Large has noticeably sharper depth
  discontinuities (cleaner foreground/background separation, better
  small-object boundaries) at ~16× the params and ~5–10× the inference
  latency. MiDaS-small loads in <1s and runs ~50ms per image on a consumer
  GPU; DPT-Large takes ~3s to load and ~300–500ms per image. For demos and
  interactive use, start with `midas_small` and reach for `dpt_large` only
  when boundary precision matters.
- **Not metric depth.** The output is *relative inverse depth*, normalised
  per image — absolute scale is discarded by the per-image min-max step, and
  even without it MiDaS produces values in arbitrary units. For metric depth
  (depth in metres), you need a different model family (ZoeDepth, Metric3D,
  Depth-Anything-Metric); they're not registered here yet.
- **Visualizing depth — false-colour palettes (`apply_colormap`)**.
  Grayscale depth is hard to *read*: the human visual system has poor
  luminance contrast sensitivity in mid-tones, so a scene that's actually
  three-dimensional often looks like a flat grey blob. The standard fix
  in computer vision and scientific visualisation is to map single-channel
  intensity through a perceptually-tuned colour palette (a *colourmap* or
  *false-colour LUT*) so depth differences register as hue differences.

  Use `apply_colormap(image, palette_name)` — a generic scalar function
  that accepts any single-channel-as-RGBA image (depth maps, U²-Net
  masks, attention maps, future heatmaps) and returns a fully-coloured
  `Image`. It reads the input's red channel as the scalar value and
  outputs RGB through the chosen palette. Currently shipped:

  | Name | Style | Best for |
  |---|---|---|
  | `'turbo'` | Google's perceptually-improved jet (blue → cyan → green → yellow → orange → red) | Depth maps — sharp hue progression, no green-yellow ambiguity. Computed via Mikhailov's degree-5 polynomial; agrees with the canonical 256-entry LUT to within a couple of byte values except right at the very endpoints. |
  | `'jet'` | Legacy MATLAB rainbow | Matching prior art; use `turbo` instead for new visualisations — `jet` has well-known perceptual flaws (banding at green/yellow, false edges at rainbow transitions). |
  | `'gray'` | Identity (R = G = B = input intensity) | Pass-through; debug / round-trip checks. |

  The matplotlib perceptually-uniform palettes (`viridis`, `inferno`,
  `magma`, `plasma`) follow the same shape — adding one is a 256-entry
  LUT plus a one-line entry in the palette dispatch table. They're not
  wired up yet; if you want one, file an issue or add it directly.

  Bigger input intensity → warmer output colour, which matches the
  near-is-bright convention the depth bodies produce — a
  `turbo`-coloured MiDaS depth map shows nearby objects in red and far
  objects in blue without any inversion.
- **Demo**:
  ```sql
  -- Side-by-side comparison on the same scene.
  SELECT
    photo_id,
    apply_colormap(models.midas_small(photo), 'turbo') AS depth_fast,
    apply_colormap(models.dpt_large(photo),   'turbo') AS depth_sharp
  FROM photos LIMIT 5;

  -- Compare two palettes on the same depth map.
  SELECT
    photo_id,
    apply_colormap(models.midas_small(photo), 'turbo') AS depth_turbo,
    apply_colormap(models.midas_small(photo), 'jet')   AS depth_jet
  FROM photos LIMIT 5;
  ```

### `vit_gpt2_caption` — image captioner

- **What it does**: Generates a single-sentence COCO-style caption for
  an image. Returns `String`.
- **License**: Apache-2.0 (nlpconnect)
- **Source**: [huggingface.co/nlpconnect/vit-gpt2-image-captioning](https://huggingface.co/nlpconnect/vit-gpt2-image-captioning)
- **Folder**: `vit-gpt2-image-captioning/`
- **Files** (all relative to the folder):
  - `encoder_model.onnx` (~330 MB) — ViT-base image encoder
  - `decoder_model.onnx` (~480 MB) — GPT-2 autoregressive decoder
  - `tokenizer.json`, `vocab.json`, `merges.txt`, `config.json`,
    `generation_config.json`, `tokenizer_config.json`,
    `special_tokens_map.json`
- **Setup**: requires Python and the `optimum` library to convert
  PyTorch → ONNX. The repo
  [scripts/export-vit-gpt-image-captioning.ps1](../scripts/export-vit-gpt-image-captioning.ps1)
  handles the full conversion in one command:
  ```powershell
  ./scripts/export-vit-gpt-image-captioning.ps1
  ```
  The script creates a Python 3.10 venv at `.venv/`, installs
  `optimum[onnxruntime] transformers`, and runs `optimum-cli export
  onnx`. ~5 minutes including download.

### `trocr_printed`, `trocr_printed_fp16` — printed-text OCR

Microsoft TrOCR — ViT-base 384×384 encoder feeding a 12-layer RoBERTa
autoregressive decoder. Single line of printed text in, transcription
out. Two registrations driven by the same `TrOcrModel` class, sharing
one folder and one tokenizer:

| Catalog name | Encoder | Decoder | Disk |
|---|---|---|---|
| `trocr_printed` | `encoder_model.onnx` (~330 MB) | `decoder_model_merged.onnx` (~990 MB) | ~1.3 GB |
| `trocr_printed_fp16` | `encoder_model_fp16.onnx` (~170 MB) | `decoder_model_merged_fp16.onnx` (~500 MB) | ~640 MB |

- **What it does**: Returns `String` — a single transcription per image.
  Works on receipts, signage, document lines, label scans. Region-aware
  output (`Array<Struct{text, bbox, score}>`) is not supported here;
  pair with a separate text-detector model if you need multi-line.
- **License**: MIT (Microsoft)
- **Source**: [huggingface.co/microsoft/trocr-base-printed](https://huggingface.co/microsoft/trocr-base-printed)
- **Folder**: `trocr-base-printed/` — both fp32 and fp16 ONNX files live
  in the same folder alongside the shared tokenizer + configs.
- **Files** (relative to the folder):
  - `encoder_model.onnx` + `encoder_model_fp16.onnx`
  - `decoder_model_merged.onnx` + `decoder_model_merged_fp16.onnx` —
    merged decoders (single ONNX file with a `use_cache_branch` switch
    that drives prefill vs incremental KV-cache steps)
  - `tokenizer.json`, `vocab.json`, `merges.txt` — RoBERTa byte-level BPE
  - `config.json`, `generation_config.json`, `preprocessor_config.json`,
    `tokenizer_config.json`, `special_tokens_map.json`
- **Setup**: produced by `optimum-cli export onnx --model
  microsoft/trocr-base-printed`. Pull both precisions in one pass:
  ```powershell
  ./.venv/Scripts/Activate.ps1
  optimum-cli export onnx `
    --model microsoft/trocr-base-printed `
    --task vision2seq-lm `
    $env:DATUM_MODELS\trocr-base-printed
  optimum-cli onnxruntime quantize `
    --onnx_model $env:DATUM_MODELS\trocr-base-printed `
    --fp16 -o $env:DATUM_MODELS\trocr-base-printed-fp16
  Move-Item $env:DATUM_MODELS\trocr-base-printed-fp16\*_fp16.onnx `
    $env:DATUM_MODELS\trocr-base-printed\
  ```
- **fp16 decoder patch (required)**: optimum-cli's merged fp16 export
  has two structural bugs ORT rejects:
  > *Subgraph output (logits) is an outer scope value being returned
  > directly.*
  > *Type Error: Type (tensor(float)) of output arg
  > (graph_output_cast_1) … does not match expected type
  > (tensor(float16)).*

  The subgraph's internal nodes already produce the right values
  (named `graph_output_cast_<i>`, matching the If's outer outputs),
  but the export wired the subgraph output declarations to outer-scope
  names like `logits`, AND the internal values are fp32 while the
  outer If output expects fp16. Patch in place by running
  [scripts/convert_decoder_model_merged_fp16.py](../scripts/convert_decoder_model_merged_fp16.py),
  which (a) renames each subgraph output declaration to point at the
  matching internal `graph_output_cast_<i>` and (b) inserts a
  `Cast(to=fp16)` between the internal value and the subgraph output.
  The script is idempotent — safe to re-run if you re-export. It
  writes `decoder_model_merged_fp16_converted.onnx`; rename it over
  the original so the registration picks it up:
  ```powershell
  python ./scripts/convert_decoder_model_merged_fp16.py
  Move-Item -Force `
    E:\Models\trocr-base-printed\decoder_model_merged_fp16_converted.onnx `
    E:\Models\trocr-base-printed\decoder_model_merged_fp16.onnx
  ```
  (Adjust the source/destination paths in the script if your
  `$DATUM_MODELS` is elsewhere.) The fp32 decoder doesn't need this —
  only the fp16 export hits the issues.
- **Architecture**: ViT-base 384×384 (1 + 24×24 patches × 768 hidden)
  feeds a 12-layer RoBERTa decoder with cross-attention. Merged
  decoder runs in two modes: **prefill** (full input_ids = `[</s>]`,
  empty caches, `use_cache_branch=false`) populates encoder + decoder
  K/V; **incremental** (single new token, prior `present.*` fed back
  as `past_key_values.*`, `use_cache_branch=true`) grows the decoder
  cache by one position per step and reuses the encoder cache
  unchanged. Greedy argmax decoding; stops on `</s>` (token 2) or
  `max_tokens=20` (matches `generation_config.json`). Preprocessing is
  `mean=[0.5,0.5,0.5], std=[0.5,0.5,0.5]` on RGB; tokens decoded with
  `BpeTokenizer.Decode` then `ByteLevelBpeDecoder` (shared with
  `vit_gpt2_caption`) to reverse the `Ġ → space` byte-level BPE
  mojibake.
- **fp32 vs fp16**: fp16 is ~2× smaller on disk and ~5× faster per
  image at negligible accuracy cost for printed text — start there.
  Reach for fp32 only if you observe transcription regressions on a
  particular document style.
- **Demo**:
  ```sql
  -- Transcribe a folder of receipt-line images.
  SELECT
    photo_id,
    models.trocr_printed_fp16(line_crop) AS text
  FROM receipt_lines
  LIMIT 20;

  -- A/B fp32 vs fp16 quality on the same crops.
  SELECT
    photo_id,
    models.trocr_printed(line_crop)      AS fp32,
    models.trocr_printed_fp16(line_crop) AS fp16
  FROM receipt_lines LIMIT 5;
  ```

### `sd_turbo` — text-to-image generator

- **What it does**: Generates 512×512 images from a text prompt in a
  single denoising step. Returns `Image` (PNG bytes).
- **License**: ⚠️ **Stability AI Community License** — free for personal
  use and commercial use under $1M ARR. Above that threshold an
  Enterprise license from Stability AI is required.
- **Source**: [huggingface.co/stabilityai/sd-turbo](https://huggingface.co/stabilityai/sd-turbo)
- **Folder**: `sd-turbo-onnx/` — diffusers-format layout
- **Files** (relative to the folder):
  - `text_encoder/model.onnx` (~1.4 GB) — CLIP ViT-H/14 text encoder
  - `unet/model.onnx` + `unet/model.onnx_data` (~3.5 GB) — UNet weights (split
    via ONNX external-data because total exceeds the 2 GB ONNX limit)
  - `vae_decoder/model.onnx` (~200 MB) — latent → RGB
  - `vae_encoder/model.onnx` (~140 MB) — only used by img2img; not used by
    DatumIngest's text-to-image path
  - `tokenizer/{vocab.json, merges.txt, special_tokens_map.json, tokenizer_config.json}`
  - `scheduler/scheduler_config.json`
  - `model_index.json`
- **Disk footprint**: ~5 GB total (FP32). FP16 builds (~half size) exist
  in some community repos; the optimum-cli conversion produces FP32 by
  default.
- **Setup**: requires conversion from PyTorch — pre-built ONNX repos
  (e.g. `tlwu/sd-turbo-onnxruntime`) are typically optimized for the
  DirectML execution provider and use Microsoft-specific NhwcConv
  operators that the standard CPU/CUDA EPs don't handle. The
  [scripts/export-sd-turbo.ps1](../scripts/export-sd-turbo.ps1) script
  handles the full conversion via `optimum-cli`:
  ```powershell
  ./scripts/export-sd-turbo.ps1
  ```
  Reuses the same `.venv` the ViT-GPT2 export created. ~5–10 minutes
  including download.

### SD 1.5 + Hyper-SD finetune ladder (`*_hyper`)

Six SD 1.5 finetunes paired with ByteDance's Hyper-SD 4-step LoRA, fused
into the UNet at export time. Same architecture as `sd_turbo`, same
wall-clock cost (~250–330ms per 512×512 image at 4 steps); each variant
fills a distinct aesthetic envelope. Loaded by the same
`StableDiffusionTurboModel` pipeline — only the weights differ.

- **License**: CreativeML OpenRAIL-M for both the SD 1.5 finetune bases
  and the Hyper-SD LoRA. Allows commercial use with usage restrictions
  on harmful content.
- **Folder**: `<variant>-hyper-onnx/` — diffusers-format layout
- **Files** (relative to the folder, identical across all six):
  - `text_encoder/model.onnx` (~500 MB) — CLIP-L (768 hidden dim)
  - `unet/model.onnx` + `unet/model.onnx_data` (~3.4 GB FP32, ~1.7 GB FP16) — fused UNet
  - `vae_decoder/model.onnx` — bundled VAE (`sd-vae-ft-mse` for Realistic Vision; finetune-bundled for the others)
  - `vae_encoder/model.onnx` — only used by img2img
  - `tokenizer/{vocab.json, merges.txt, ...}`
  - `scheduler/scheduler_config.json`, `model_index.json`
- **Disk footprint per variant**: ~5 GB FP32 (~2.5 GB FP16)
- **Setup**: each variant has its own export script under
  `scripts/export-*-hyper.ps1`. Each script downloads the base finetune,
  fuses the Hyper-SD LoRA via diffusers + peft, and runs `optimum-cli`
  to export to ONNX. ~5–10 minutes per export. Reuses the same `.venv`
  as the other diffusion exports. Pass `-Fp16` to halve the disk
  footprint:
  ```powershell
  ./scripts/export-realistic-vision-hyper.ps1 -Fp16
  ./scripts/export-dreamshaper-hyper.ps1 -Fp16
  # etc.
  ```

| Catalog name | Aesthetic | Base finetune | Notes |
|---|---|---|---|
| `realistic_vision_hyper` | Photoreal portraits, character-focused | [`SG161222/Realistic_Vision_V6.0_B1_noVAE`](https://huggingface.co/SG161222/Realistic_Vision_V6.0_B1_noVAE) | Strongest people coherence; trained on a narrow portrait distribution. Tends to leak NSFW on suggestive prompts — reach for AbsoluteReality / DreamShaper / epiCRealism for SFW-by-default workflows. Paired with `stabilityai/sd-vae-ft-mse` since the base ships `noVAE`. |
| `dreamshaper_hyper` | Stylized fantasy, painterly, concept art | [`Lykon/dreamshaper-8`](https://huggingface.co/Lykon/dreamshaper-8) | Fit for fantasy characters, monsters, and atmospheric scenes. Less NSFW-leaning than RV. |
| `epicrealism_hyper` | Photoreal scenes, environments, group shots | [`emilianJR/epiCRealism`](https://huggingface.co/emilianJR/epiCRealism) | Broader subject coverage than RV; strong on taverns, landscapes, group compositions. |
| `openjourney_hyper` | Midjourney v4 cinematic, dramatic lighting | [`prompthero/openjourney-v4`](https://huggingface.co/prompthero/openjourney-v4) | Set-pieces and atmospheric reveals. v4 dropped the `mdjrny-v4 style` trigger; prompts work without prefix. |
| `mo_di_hyper` | Disney / Pixar 3D-render style | [`nitrosocke/mo-di-diffusion`](https://huggingface.co/nitrosocke/mo-di-diffusion) | **Trigger phrase:** prepend `"modern disney style"` to fully exercise the look. Useful for tone shifts, comic-relief NPCs, family-friendly campaigns. |
| `absolute_reality_hyper` | SFW general workhorse, photoreal-leaning | [`Lykon/AbsoluteReality`](https://huggingface.co/Lykon/AbsoluteReality) | Versatile across portraits / scenes / characters; less stylized than DreamShaper, more general than epiCRealism. |

The API is identical across variants — `models.<name>(prompt)`. Pick by
aesthetic; swap by changing the model name in your query.

### `sdxl_turbo` — text-to-image generator (high quality)

- **What it does**: Generates 1024×1024 images from a text prompt in
  a single denoising step. Notably better composition and prompt
  adherence than SD-Turbo, at the cost of more disk + VRAM.
- **License**: ⚠️ **Stability AI Community License** — same as SD-Turbo.
  Free under $1M ARR; Enterprise license required above.
- **Source**: [huggingface.co/onnxruntime/sdxl-turbo](https://huggingface.co/onnxruntime/sdxl-turbo)
  (Microsoft's official fp16 ONNX build — CUDA EP only, madebyollin
  VAE for fp16 stability, int32 token IDs). Original PyTorch weights at
  [huggingface.co/stabilityai/sdxl-turbo](https://huggingface.co/stabilityai/sdxl-turbo).
- **Folder**: `sdxl-turbo-onnx/` — diffusers-format layout with the
  SDXL addition of a second text encoder
- **Files** (relative to the folder):
  - `text_encoder/model.onnx` — CLIP-L
  - `text_encoder_2/model.onnx` + `text_encoder_2/model.onnx_data` — OpenCLIP-G
  - `unet/model.onnx` + `unet/model.onnx_data` — UNet (~2.6B params)
  - `vae_decoder/model.onnx` — madebyollin fp16-stable VAE
  - `vae_encoder/model.onnx` — only used by img2img
  - `tokenizer/{vocab.json, merges.txt, ...}` — CLIP BPE
- **Disk footprint**: ~9.7 GB (fp16 mixed precision — UNet + text
  encoders fp16, VAE uses madebyollin's fp16-native build)
- **VRAM**: ~6-8 GB during inference; tight on 12 GB cards alongside
  Llama 8B
- **Setup**: download the pre-built ONNX directly from
  [huggingface.co/onnxruntime/sdxl-turbo](https://huggingface.co/onnxruntime/sdxl-turbo)
  into your `$env:DATUM_MODELS\sdxl-turbo-onnx` folder. No conversion
  required. **CUDA EP only** — CPU and DirectML are not supported by
  this build. For a portable fp32 build that runs on any EP, use
  [scripts/export-sdxl-turbo.ps1](../scripts/export-sdxl-turbo.ps1)
  (~15-25 minutes, ~12 GB).
- **vs `sd_turbo`**: dramatically better quality, especially for
  complex scenes / multi-subject compositions / fine detail. Slower
  per-image (~3-5s vs SD's ~1-2s). Use SDXL-Turbo for hero outputs;
  SD-Turbo for fast iteration.

### Florence-2 vision tasks (`florence2_*`)

Microsoft's prompt-driven vision-language model. One model handles
multiple caption styles, OCR with region grounding, and additional
detection / segmentation tasks via task tokens. Registered as five
separate catalog entries that share the same backbone:

| Catalog name | Task prompt | Output style |
|---|---|---|
| `florence2_caption` | `<CAPTION>` | Short COCO-style caption |
| `florence2_detailed_caption` | `<DETAILED_CAPTION>` | Full sentence with context |
| `florence2_more_detailed_caption` | `<MORE_DETAILED_CAPTION>` | Paragraph-level description |
| `florence2_caption_q8` | `<CAPTION>` (int8 quant) | Short caption, ¼ size |
| `florence2_ocr_region` | `<OCR_WITH_REGION>` | OCR text interleaved with `<loc_*>` bbox tokens |

`florence2_ocr_region` returns a single string with each detected text
run followed by four `<loc_N>` tokens that encode its bounding box in
Florence-2's quantized 0–999 coordinate space. Example output for a
screenshot:

```
File<loc_42><loc_18><loc_72><loc_36>Edit<loc_78><loc_18><loc_108><loc_36>...
```

This is a license-clean (MIT) substitute for OmniParser's AGPL-licensed
icon detector when the downstream consumer is an LLM that can parse the
location-token stream. The SQL surface returns the raw string;
consumers extract `(text, bbox)` pairs themselves with a regex over
the `<loc_*>` markers.

- **License**: MIT (Microsoft)
- **Source**: [huggingface.co/onnx-community/Florence-2-base-ft](https://huggingface.co/onnx-community/Florence-2-base-ft)
- **Folders**:
  - `florence-2-base-ft-fp16/` — fp16 build (~480 MB ONNX) used by the
    first three entries
  - `florence-2-base-ft-quantized/` — int8 build (~120 MB ONNX) used
    by the `_q8` entry
- **Files per folder** (suffix matches folder: `_fp16` or `_quantized`):
  - `vision_encoder{suffix}.onnx`
  - `embed_tokens{suffix}.onnx`
  - `encoder_model{suffix}.onnx`
  - `decoder_model{suffix}.onnx`
  - Plus shared tokenizer/config files: `tokenizer.json`, `vocab.json`,
    `merges.txt`, `config.json`, `generation_config.json`,
    `preprocessor_config.json`, `special_tokens_map.json`
- **Setup**:
  ```powershell
  # fp16 variant — used by the three caption-style entries
  huggingface-cli download onnx-community/Florence-2-base-ft `
    --include "*_fp16.onnx" "*.json" "*.txt" `
    --local-dir $env:DATUM_MODELS\florence-2-base-ft-fp16

  # int8-quantized variant — used by florence2_caption_q8
  huggingface-cli download onnx-community/Florence-2-base-ft `
    --include "*_quantized.onnx" "*.json" "*.txt" `
    --local-dir $env:DATUM_MODELS\florence-2-base-ft-quantized
  ```

### `paligemma2_224`, `paligemma2_448` — Google PaliGemma 2 captioners

Google's vision-language model: SigLIP image encoder + Gemma 2B decoder
+ learned linear projector. The "mix" variants are pre-finetuned across
captioning, VQA, and OCR — a single model handles diverse prompts via
the prefix passed to the decoder. We register two resolution variants:

| Catalog name | Input | Image tokens | Use for |
|---|---|---|---|
| `paligemma2_224` | 224×224 | 256 | Cheap iteration; broad scenes |
| `paligemma2_448` | 448×448 | 1024 | Fine-detail / OCR / scene art |

- **License**: Gemma Terms (Google) — broadly permissive, allows
  commercial use; redistribution must pass the terms along.
- **Source**: [huggingface.co/google/paligemma2-3b-mix-448](https://huggingface.co/google/paligemma2-3b-mix-448)
  (or `-224` for the smaller variant)
- **Folders**: one per variant (`paligemma2-3b-mix-224-onnx/`, etc.)
- **Files per folder**:
  - `vision_encoder.onnx` — SigLIP encoder + linear projector
  - `embed_tokens.onnx` — Gemma token-embedding lookup
  - `decoder_model.onnx` — Gemma 2B autoregressive decoder
  - `tokenizer.json`, `vocab.json`, `merges.txt`, `config.json`
- **Default prompt**: `"caption en"` — produces verbose factual
  English captions like *"Two adventurers in armor stand at the cavern
  entrance. The taller one holds a torch. Cobwebs hang from the ceiling."*
- **Output style**: PaliGemma's captions are noticeably more verbose
  and grounded than Florence-2's, with multiple short sentences
  rather than one. Good raw material to feed an LLM rewriter.
- **Other prompts** (set via the registration's `defaultPrompt`):
  - `"caption en"` / `"caption es"` / `"caption fr"` / etc.
  - `"answer en What is the dragon doing?"` — VQA mode
  - `"ocr"` — OCR mode
  - `"detect <object>"` — object detection
- **Setup**: produced by the batch ONNX conversion script:
  ```powershell
  ./scripts/export-batch-onnx.ps1 -Models paligemma2-3b-mix-448
  ```
  Conversion runs `optimum-cli export onnx --model
  google/paligemma2-3b-mix-448`. ~10-15 minutes including download.
- **Demo (vs Florence-2 for the same scene)**:
  ```sql
  SELECT
    art_id,
    models.florence2_more_detailed_caption(art) AS clinical,    -- structured single sentence
    models.paligemma2_448(art)                  AS verbose      -- multi-sentence factual
  FROM scene_art LIMIT 3;
  ```

### Vision-language models (`phi35_vision`, `moondream2`)

Free-form vision-language models with a `(image, prompt) → string` SQL
shape — the user supplies the question per call rather than getting a
fixed-task caption. Designed as a license-clean (Apache/MIT)
alternative to OmniParser's AGPL-licensed icon detector for screenshot
interpretation; same shape works for any image-grounded Q&A.

| Catalog name | Backbone | Runtime | License | Disk | Cold-start, 32-token output |
|---|---|---|---|---|---|
| `phi35_vision` | Microsoft Phi-3.5-vision (4.2B) | ORT GenAI managed runtime | MIT | ~2.5 GB (int4) | ~4s |
| `moondream2` | SigLIP encoder + Phi-1.5/2 decoder (1.9B) | Hand-rolled ORT with IO Binding | Apache-2.0 | ~3.6 GB (fp16) | ~7s |

Both are registered with `Category="vlm"` and the same input shape
`[Image, String] → String`, so a single SQL query can compare them
side-by-side:

```sql
SELECT
  path,
  models.phi35_vision(image,  'Describe what is shown')  AS phi,
  models.moondream2(image,    'Describe what is shown')  AS moondream
FROM read_directory('E:\screenshots\*.png');
```

#### `phi35_vision` — Phi-3.5-vision via ORT GenAI

- **Files**: bundle directory at `phi35-vision-onnx/gpu/gpu-int4-rtn-block-32/`
  containing `genai_config.json`, `processor_config.json`, three ONNX
  files (`phi-3.5-v-instruct-{vision,embedding,text}.onnx` plus their
  `.data` external-data sidecars), and tokenizer JSONs. The
  `gpu-int4-rtn-block-32` subfolder name is a quantization label
  (int4 round-to-nearest, block size 32) — not a runtime selector.
- **Setup**:
  ```powershell
  huggingface-cli download microsoft/Phi-3.5-vision-instruct-onnx `
    --include "gpu/gpu-int4-rtn-block-32/*" `
    --local-dir $env:DATUM_MODELS\phi35-vision-onnx
  ```
- **Runtime**: managed by `Microsoft.ML.OnnxRuntimeGenAI.Cuda` (NuGet
  0.5.2). The GenAI runtime handles vision encoder + embedding +
  decoder orchestration, KV cache, and IO binding internally — much
  faster than hand-rolled ORT for decoder-only generation. CUDA
  acceleration requires `Config.AppendProvider("cuda")` at load time
  (the genai_config.json ships with empty `provider_options`,
  defaulting to CPU regardless of folder name).
- **Output**: `string`. Greedy sampling (`do_sample=false`, `top_k=1`
  in genai_config.json) — deterministic per (image, prompt).
- **High-resolution image processing**: the bundled processor crops
  large images into up to 16 sub-images at 144 tokens each, which is
  why a single image can produce a ~2,500-token input prefix. The
  decoder uses Phi-3's 128K context; per-call cap is `max_length =
  4096 + maxTokens` to preserve the user's "N new tokens" budget.

#### `moondream2` — vikhyatk/moondream2 via hand-rolled ORT

- **Files**: `moondream2-onnx/onnx/{vision_encoder_fp16, embed_tokens_fp16,
  decoder_model_merged_fp16}.onnx` (plus `decoder_model_merged_fp16.onnx_data`
  for external weights) and tokenizer JSONs at the model-root level
  (`vocab.json`, `merges.txt`, `tokenizer.json`, `config.json`,
  `preprocessor_config.json`).
- **Setup**:
  ```powershell
  huggingface-cli download Xenova/moondream2 `
    --local-dir $env:DATUM_MODELS\moondream2-onnx
  ```
- **Runtime**: plain `Microsoft.ML.OnnxRuntime.Gpu` with explicit
  ORT IO Binding — KV cache stays GPU-resident across the
  autoregressive loop, eliminating per-token PCIe round-trips.
  Reference implementation: `MusicGenModel.cs` in the same folder
  uses the same pattern.
- **Prompt template**: Moondream's training format is
  `<image>\n\nQuestion: {prompt}\n\nAnswer:`. The `<image>` marker
  is the splice point for image embeddings; the model class
  tokenizes only the text portion and prepends the vision encoder's
  output.
- **Output**: `string`. Greedy argmax sampling — deterministic per
  (image, prompt). Tokenizer is GPT-2-style byte-level BPE; output
  is post-processed via `ByteLevelBpeDecoder` to reverse the
  byte-to-unicode mapping.
- **Resolution**: fixed 378×378 input (SigLIP encoder), 729 image
  tokens regardless of input size.

#### Choosing between them

`phi35_vision` is faster end-to-end for typical screenshots and has
stronger reasoning behavior on complex scenes. `moondream2` is the
smaller model with fewer image tokens — use it when input prompts
are simple ("describe this") and short outputs suffice. For dense
text extraction from screenshots, prefer `florence2_ocr_region`
(sub-second, no autoregressive loop).

### LLMs (`llama31_8b`, `phi3_mini`, `tinyllama_1b`, `gemma2_2b`, `qwen25_coder_*`, `granite31_1b`, `falcon3_1b`, `mistral_7b`)

Ten LLMs spanning Meta, Microsoft, TinyLlama community, Google,
Alibaba (three Qwen-Coder sizes), IBM, TII, Mistral AI. Mostly
quantized to **Q4_K_M** for clean cross-model comparison; Qwen-Coder 7B
and Mistral 7B use Q5_K_M for the small quality bump. Each is a single
GGUF file loaded via LlamaSharp.

| Catalog name | Display | License | Holder |
|---|---|---|---|
| `llama31_8b` | Llama 3.1 8B Instruct | Llama 3.1 Community | Meta |
| `phi3_mini` | Phi-3-mini-4k Instruct | MIT | Microsoft |
| `tinyllama_1b` | TinyLlama 1.1B Chat v1.0 | Apache-2.0 | TinyLlama community |
| `gemma2_2b` | Gemma 2 2B Instruct | Gemma Terms | Google |
| `qwen25_coder_1_5b` | Qwen 2.5 Coder 1.5B Instruct | Apache-2.0 | Alibaba |
| `qwen25_coder_3b` | Qwen 2.5 Coder 3B Instruct | Apache-2.0 | Alibaba |
| `qwen25_coder_7b` | Qwen 2.5 Coder 7B Instruct | Apache-2.0 | Alibaba |
| `granite31_1b` | IBM Granite 3.1 1B A400M | Apache-2.0 | IBM |
| `falcon3_1b` | Falcon3 1B Instruct | Falcon LLM License 2.0 | TII |
| `mistral_7b` | Mistral 7B Instruct v0.3 | Apache-2.0 | Mistral AI |

The Qwen2.5-Coder ladder is registered with size-appropriate defaults:
the 1.5B uses a 4K context (fast iteration), while the 3B and 7B use a
16K context with a higher max-tokens budget so single-call generation of
multi-paragraph code or HTML pages doesn't truncate. The 7B drops to
`temperature=0.5` (vs 0.7 default) for more deterministic code output.
Per-call overrides — `models.qwen25_coder_7b(prompt, 0.7, 4096)` — let
you tweak both temperature and max_tokens at the call site.

**Streaming output.** LLM responses produce tokens incrementally;
DatumIngest exposes that stream when the consumer opts in:

- `SELECT models.X(prompt) FROM t` collects the full response into a
  single string per row before rendering — no live tokens. The
  underlying streaming path is still exercised internally (LLM
  `InferBatchAsync` collects over `InferStreamingAsync`), so the same
  code is on the hot path either way.

The LLM is the only model family that produces multi-chunk streams
today; other models (classifiers, detectors, captioners, image
generators) yield a single result and the `CALL` streaming pane shows
that one value once inference completes.

**Setup**: each is a single `*.gguf` file dropped into the models
directory. Filenames must match the catalog defaults
(see [BuiltinModels.cs](../src/DatumIngest/Models/BuiltinModels.cs))
or be passed explicitly via the registration helper's `modelFilename`
parameter.

```powershell
# Llama 3.1 8B
huggingface-cli download bartowski/Meta-Llama-3.1-8B-Instruct-GGUF `
  Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Phi-3 mini
huggingface-cli download bartowski/Phi-3-mini-4k-instruct-GGUF `
  Phi-3-mini-4k-instruct-Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# TinyLlama 1.1B Chat
huggingface-cli download TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF `
  tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Gemma 2 2B
huggingface-cli download bartowski/gemma-2-2b-it-GGUF `
  gemma-2-2b-it-Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Qwen 2.5 Coder 1.5B
huggingface-cli download bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF `
  Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Qwen 2.5 Coder 3B
huggingface-cli download bartowski/Qwen2.5-Coder-3B-Instruct-GGUF `
  Qwen2.5-Coder-3B-Instruct-Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Qwen 2.5 Coder 7B (note Q5_K_M, not Q4_K_M)
huggingface-cli download bartowski/Qwen2.5-Coder-7B-Instruct-GGUF `
  Qwen2.5-Coder-7B-Instruct-Q5_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Granite 3.1 1B (MoE)
huggingface-cli download bartowski/granite-3.1-1b-a400m-instruct-GGUF `
  granite-3.1-1b-a400m-instruct-Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Falcon3 1B
huggingface-cli download tiiuae/Falcon3-1B-Instruct-GGUF `
  Falcon3-1B-Instruct-q4_k_m.gguf `
  --local-dir $env:DATUM_MODELS

# Mistral 7B Instruct v0.3 (note Q5_K_M, not Q4_K_M)
huggingface-cli download bartowski/Mistral-7B-Instruct-v0.3-GGUF `
  Mistral-7B-Instruct-v0.3-Q5_K_M.gguf `
  --local-dir $env:DATUM_MODELS
```

Total LLM disk: ~21 GB with all three Qwen-Coder sizes plus Mistral 7B.
Each call holds one model resident; the residency manager swaps when
VRAM is tight.

### Whisper STT zoo (`whisper_tiny`, `whisper_base`, `whisper_small`, `whisper_medium`)

OpenAI Whisper as native ONNX. Four size variants spanning fast/cheap
to slow/accurate. Single SQL surface: `models.whisper_X(audio_bytes)`
returns the transcription as `String`.

| Catalog name | Params | Best for |
|---|---|---|
| `whisper_tiny` | 39M | Round-trip verification, smoke tests |
| `whisper_base` | 74M | Balanced default |
| `whisper_small` | 244M | Better accuracy on accented / noisy speech |
| `whisper_medium` | 769M | Strong STT, slower |

- **License**: MIT (OpenAI)
- **Source**: [huggingface.co/openai/whisper-base](https://huggingface.co/openai/whisper-base)
  (replace `base` with the size you want)
- **Folders**: one per variant (`whisper-tiny-onnx/`, etc.)
- **Files per folder**:
  - `encoder_model.onnx` — audio features → encoder hidden states
  - `decoder_model.onnx` — autoregressive caption decoder (no KV cache;
    `decoder_with_past_model.onnx` shipped alongside but unused)
  - `vocab.json`, `merges.txt`, `tokenizer.json` — multilingual BPE
  - `preprocessor_config.json` — mel spectrogram params
  - `generation_config.json` — special token IDs
  - `special_tokens_map.json`
- **Input**: WAV bytes (any sample rate / bit depth — the C# WAV decoder
  handles 8/16/24-bit PCM and IEEE float32, mono/stereo/multi-channel,
  resampling to 16kHz via linear interpolation).
- **Output**: English transcript as `String`. Multilingual models support
  other languages but the registration uses the English language token
  prefix (`<|en|>`); to transcribe another language, register with a
  different `LanguageToken` (one-line constant in `WhisperOnnxModel`).
- **Setup**: produced by the batch ONNX conversion script:
  ```powershell
  ./scripts/export-batch-onnx.ps1 -Models whisper-base
  ```
  Convert other sizes by name (`whisper-tiny`, `whisper-small`,
  `whisper-medium`). The script reuses `.venv/` and runs
  `optimum-cli export onnx --model openai/whisper-X` per variant.

### Python-bridge models (`bark_small`, `bark`, `kokoro_82m`)

Some models are difficult or impractical to convert from PyTorch to
ONNX — multi-stage pipelines with autoregressive Python control flow
(Bark), or research-grade libraries that don't ship export tooling
(XTTS-v2, StyleTTS2). DatumIngest runs these through a long-lived
Python subprocess: the C# side hands inputs over via NDJSON on
stdio, the Python worker uses the upstream library directly, and the
results come back as bytes.

The bridge has its own status indicator in `system_models`:
**`bridge`** — backend is `python`, the venv exists, and the model is
*probably* runnable. Catalog can't fully verify pip packages without
spawning the worker, so a clean `status=bridge` doesn't guarantee
runnability — but a missing venv reliably reports `status=missing`.

#### `bark_small` — TTS with embedded sound effects

- **What it does**: Generates 24kHz mono speech with optional inline
  non-speech tokens. Write `[laughs]`, `[sighs]`, `[music]` etc. in
  the prompt and Bark renders them inline. Output is WAV bytes
  (carried as `Image` until `DataKind.Audio` lands).
- **License**: MIT (Suno)
- **Source**: [huggingface.co/suno/bark-small](https://huggingface.co/suno/bark-small)
- **Backend**: Python bridge — wraps HuggingFace `transformers`'
  `BarkModel`.
- **Files (catalog tracks)**: `.venv-bark/pyvenv.cfg` — the venv
  marker. Bark's actual weights live in `~/.cache/huggingface/`, not
  in `$DATUM_MODELS`.
- **Setup**:
  ```powershell
  ./scripts/setup-bark-venv.ps1
  ```
  Creates `$DATUM_MODELS/.venv-bark`, pip-installs `transformers`,
  `torch` (CUDA wheel), and `scipy`. The Bark weights download from
  HuggingFace on the first inference call (~1 GB, one-time).
  - Use `-Cpu` to install CPU-only torch (much slower; no NVIDIA
    needed).
  - Use `-CudaWheel cu126` (or `cu124` / `cu121`) to pin a different
    PyTorch CUDA wheel — defaults to `cu128`, which works against
    CUDA Toolkit 12.x system installs.
  - Use `-Force` to nuke and recreate.
- **Per-call overrides**:
  - `[0] voice_preset` (string) — e.g. `'v2/en_speaker_9'`. Worker
    pins `v2/en_speaker_6` by default (neutral male, well-tested).
- **Determinism**: Bark samples internally — same prompt produces
  different audio each call.
- **Tips for good output**:
  - **Use full sentences.** Bark expects multi-second context; bare
    phrases ("Cookie Dadda") produce noisy ~1s clips with weird
    prosody. "Hello there, this is Cookie Dadda speaking." sounds
    far better.
  - **Always specify a voice preset** for repeatable output. Without
    one, Bark picks a random speaker each call — quality varies wildly.
  - Inline cues like `[laughs]`, `[clears throat]`, `[sighs]` work.
  - Bark sometimes adds breath, room tone, or even bird sounds
    spontaneously — that's by design from upstream.
- **Demo**:
  ```sql
  SELECT models.bark_small(
    'Hello there from Datum Ingest. [laughs] This is rather fun, actually.',
    'v2/en_speaker_9'
  );
  ```

#### `bark` — full Bark TTS (higher quality)

Same architecture, voices, and worker as `bark_small` — bigger weights
(~700M params vs ~100M) for noticeably more natural prosody at
~3-4× the inference cost.

- **License / Source**: same as `bark_small` —
  [huggingface.co/suno/bark](https://huggingface.co/suno/bark)
- **Backend**: Python bridge — same `.venv-bark` and worker script
  (`bark_worker.py`) as `bark_small`, only the HuggingFace model ID
  differs (`suno/bark` vs `suno/bark-small`).
- **First-call download**: ~3.5 GB into `~/.cache/huggingface/`.
- **VRAM**: ~3-4 GB during inference (3-4× `bark_small`'s footprint).
- **Latency**: ~15-30s per clip on a consumer GPU vs `bark_small`'s
  ~5-10s. Use `bark` for hero outputs, `bark_small` for fast iteration.
- **Setup**: nothing extra beyond `setup-bark-venv.ps1` — both
  variants share the venv. The full model auto-downloads on first
  inference call.
- **Per-call overrides**: same as `bark_small` —
  `models.bark(text, 'v2/en_speaker_9')`.
- **Demo**:
  ```sql
  -- Compare quality side-by-side: same prompt, both variants.
  SELECT
    models.bark_small(prompt, 'v2/en_speaker_0') AS small,
    models.bark      (prompt, 'v2/en_speaker_0') AS full
  FROM (SELECT 'You enter the cavern. [pause] Distant water drips.' AS prompt);
  ```

#### `kokoro_82m` — fast multi-voice TTS

- **What it does**: 82M-parameter ONNX TTS with 11+ built-in voices.
  Fast enough to keep up with token-streaming LLM output. Apache-2.0,
  cleaner license than Bark for commercial work.
- **License**: Apache-2.0 (hexgrad)
- **Source**: [huggingface.co/hexgrad/Kokoro-82M-ONNX](https://huggingface.co/hexgrad/Kokoro-82M-ONNX)
- **Backend**: Python bridge — wraps the `kokoro-onnx` package, which
  bundles the misaki phonemizer + ONNX inference. The model itself is
  ONNX (we go through Python only for the phonemizer).
- **Files (catalog tracks)**: `kokoro-v1.0.onnx` — the ONNX model file
  in `$DATUM_MODELS`. Voices and venv tracked separately:
  - `voices-v1.0.bin` (~26 MB, bundled all voices), OR
  - `kokoro-voices/<voice>.bin` (per-voice files; the worker bundles
    them into a temp `.npz` at startup)
  - `.venv-kokoro/` for the Python deps
- **Per-call overrides**:
  - `[0] voice` (string) — e.g. `'af_bella'`, `'am_michael'`, `'bm_george'`
  - `[1] speed` (float) — `0.5` ... `2.0`
  - Example: `models.kokoro_82m('hello', 'bm_george', 1.2)`
- **Setup** — venv only:
  ```powershell
  ./scripts/setup-kokoro-venv.ps1
  ```
  Creates `$DATUM_MODELS/.venv-kokoro` and installs `kokoro-onnx`
  (which pulls in `onnxruntime`, the misaki phonemizer, and `scipy`
  as transitive deps). You provide the model + voices files yourself
  (typical for users who already downloaded the per-voice `.bin`
  files from the original hexgrad repo).
- **Setup — fully automated** (venv + model + bundled voices):
  ```powershell
  ./scripts/setup-kokoro-venv.ps1 -DownloadModel -DownloadVoices
  ```
  Downloads `kokoro-v1.0.onnx` (~326 MB) and `voices-v1.0.bin`
  (~26 MB) from the kokoro-onnx GitHub release into `$DATUM_MODELS`.
- **Per-voice .bin layout**: if you have separate per-voice files
  (e.g. `af_bella.bin`, `bm_george.bin`, ...), drop them into
  `$DATUM_MODELS/kokoro-voices/`. The default registration points at
  this path; the worker bundles the per-voice arrays into a temp
  `.npz` at startup and passes that to `kokoro-onnx`.
- **Determinism**: deterministic for a given (text, voice, speed)
  tuple. Planner CSE folds duplicate call sites.
- **Demo**:
  ```sql
  SELECT models.kokoro_82m('hello there from datum ingest', 'af_bella');
  ```

## Quantization conventions

GGUF LLMs use the K-quant family. Common suffixes:

| Suffix | Bits | Quality | Size (vs FP16) |
|---|---|---|---|
| `Q3_K_M` | 3-bit | Noticeable drop | ~25% |
| `Q4_K_M` | 4-bit | **Standard** | ~30% |
| `Q5_K_M` | 5-bit | Slightly better than Q4 | ~37% |
| `Q5_K_L` | 5-bit + fp16 critical layers | High | ~40% |
| `Q6_K` | 6-bit | Near-FP16 | ~45% |
| `Q8_0` | 8-bit | Indistinguishable | ~57% |

The default zoo uses Q4_K_M throughout for consistent
quality-comparison conditions. If you re-quantize, prefer matching
quants across all entries you plan to compare side-by-side — mixing
quants confounds tone/quality differences with quantization noise.

ONNX models use ONNX-Runtime quantization formats (fp32, fp16, int8).
The Florence-2 entries explicitly cover both fp16 and int8 to support
quality / size A/B testing.

## Querying the catalog

```sql
-- The whole zoo
SELECT * FROM system_models;

-- Just the LLM zoo, smallest first
SELECT name, parameters, file_size_bytes, license
FROM system_models
WHERE category = 'llm'
ORDER BY file_size_bytes;

-- What's missing?
SELECT name, file_names, source_url
FROM system_models
WHERE status = 'missing';

-- Which Python-bridge models are set up but unverified?
-- (status = 'bridge' means files present, but the catalog can't see
-- whether the venv's pip packages are intact — first invocation will
-- fail loudly if they aren't.)
SELECT name, file_names
FROM system_models
WHERE status = 'bridge';

-- License audit
SELECT category, license, COUNT(*) AS n
FROM system_models
GROUP BY category, license;

-- Which models are commercially clean (Apache / MIT / BSD)?
SELECT name, display_name, license
FROM system_models
WHERE license IN ('Apache-2.0', 'MIT', 'BSD-3-Clause');
```

## Adding a new model

1. **Pick a backend.** Three options:
   - **ONNX** — vision, embeddings, captioners, detectors, image gen
     pipelines. Inherits from `OnnxModel`. The fast, native path.
   - **GGUF + LlamaSharp** — LLMs. Use `LlamaModel`.
   - **Python bridge** — for libraries that don't ship ONNX export
     tooling (research-grade TTS, multi-stage pipelines with dynamic
     control flow, anything in the HuggingFace transformers ecosystem
     that fights `optimum-cli`). Inherits from `PythonBackedModel`,
     ships a worker `.py` in `src/DatumIngest/Models/Python/scripts/`,
     gets a `setup-X-venv.ps1` script under `scripts/`, and reports
     `status=bridge` in `system_models`.
2. **Add a model class** to `src/DatumIngest/Models/Onnx/` or
   `src/DatumIngest/Models/Llama/`. Inherit from `OnnxModel` for ONNX
   Runtime models; for multi-session pipelines like ViT-GPT2,
   Florence-2, or Whisper, override `InferBatchAsync` directly.
   For Python-bridge models, the model class is just a
   `PythonBackedModel` instantiation in the registration helper —
   the worker script does the per-model logic.
3. **Add a register helper** to
   [BuiltinModels.cs](../src/DatumIngest/Models/BuiltinModels.cs).
   Populate the full metadata: `DisplayName`, `Parameters`, `License`,
   `LicenseHolder`, `SourceUrl`, `Category`, `Modalities`, `Files`.
4. **Wire into `AttachStandardModels`** so it ships with the default
   catalog.
5. **Add a smoke test** under
   `tests/DatumIngest.Tests/Models/`. Self-skip when the file isn't
   available so CI machines don't fail.
6. **Add a setup script** if Python-backed: `scripts/setup-X-venv.ps1`
   following the Bark / Kokoro template.
7. **Update this doc** with the model entry.
