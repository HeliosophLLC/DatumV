---
title: Inference Helpers
category: inference
---

# Inference Helpers

This page documents the model-specific preprocess, postprocess, and
dispatch helpers that turn raw ONNX tensors into typed, usable
outputs. The functions here are the bridge between the generic
[`infer()`](../sql/create-model.md#infer) dispatch surface and the
domain shapes (`Image`, `Mesh`, `Array<LabeledDetection>`,
`Array<RegionScore>`) that `CREATE MODEL` bodies return.

Most are *not* body-scoped (they can be called from any `SELECT`),
but they appear almost exclusively inside model bodies. Image
*tensor* conversion (`image_to_tensor_chw`, `imagenet_mean`,
`tensor_to_image_chw`, ...) lives one page over in
[Image Functions](image.md); tokenizers live in
[Tokenization Functions](tokenization.md); audio mel-spectrogram in
[Audio Functions](audio.md).

## Generative Decode Loops

The decode-loop scalars are *body-scoped*: they're only callable from
inside a `CREATE MODEL` body. They wrap the standard greedy
autoregressive loop (KV cache management, EOS detection, token
suppression, embedding lookup) around aliased ONNX sessions so a
multi-step generative model collapses into one SQL call.

For the higher-level `infer` / `infer_outputs` / `llama_chat` /
`llama_generate` dispatch scalars, see
[CREATE MODEL](../sql/create-model.md#body-scoped-dispatch-surface).

### decode_seq2seq

`decode_seq2seq(decoder_alias, encoder_features, encoder_attention_mask, prefix_token_ids, eos_token_id, max_tokens, use_kv_cache [, embed_tokens_alias] [, suppress_above])` → `Int64[]` ![Body-scoped](https://img.shields.io/badge/-body--scoped-blue)

**Body-scoped.** Encoder-decoder autoregressive loop. Used by
Whisper, MarianMT, T5, BART, TrOCR, and any other encoder-decoder
transformer with an ONNX decoder export.

- `decoder_alias` — `USING ... AS` name of the decoder session.
- `encoder_features` — flat last-hidden-state output of the encoder
  pass (`[1, encoder_seq, hidden]`).
- `encoder_attention_mask` — `Int64[]` mask over `encoder_seq`, or
  `NULL` when the decoder ignores it (Whisper).
- `prefix_token_ids` — task-prefix token sequence the decoder starts
  with. Whisper-English transcription is
  `[SOT, EN, TRANSCRIBE, NO_TIMESTAMPS]`; T5 / BART start with just
  the decoder-start-of-sequence token.
- `eos_token_id` — generation stops as soon as the decoder emits this
  id.
- `max_tokens` — hard upper bound on generated tokens (Whisper's
  positional-embedding ceiling is 448).
- `use_kv_cache` — `true` for KV-cached decoder exports (the modern
  optimum-style; ~5× faster); `false` for the no-cache export used by
  the legacy reference graphs.
- `embed_tokens_alias` (optional) — separate `USING ... AS` name when
  the embedding lookup lives in its own ONNX file.
- `suppress_above` (optional) — caps argmax at token ids ≤ this value.
  Whisper uses `50257` to suppress timestamp / language / task tokens
  mid-transcript.

Returns the flat `Int64[]` of generated token ids (prefix excluded,
EOS excluded).

```sql
DECLARE token_ids Int64[] = decode_seq2seq(
    'decoder', encoder_features, NULL,
    [50258::Int64, 50259::Int64, 50359::Int64, 50363::Int64],
    50257::Int64, max_tokens, false, 50257::Int64);
```

Canonical use: `models.whisper_base`.

### decode_decoder_only

`decode_decoder_only(decoder_alias, embed_tokens_alias, prefix_embeddings, eos_token_id, max_tokens)` → `Int64[]` ![Body-scoped](https://img.shields.io/badge/-body--scoped-blue)

**Body-scoped.** KV-cached greedy autoregressive loop for
decoder-only LLMs (Phi, Llama, GPT) consumed from a vision-language
or other multi-modal body. The first step prefills with the full
`prefix_embeddings` (typically `visual_features || text_embeds`);
subsequent steps embed the previously-generated token via
`embed_tokens_alias` and grow the KV cache.

- `decoder_alias` — decoder ONNX session (must expose KV-cache
  past/present inputs and outputs).
- `embed_tokens_alias` — token-embedding ONNX session used to embed
  each newly-generated token id back to hidden-state space.
- `prefix_embeddings` — concatenated prefix `[1, prefix_len, hidden]`
  in hidden-state space. `prefix_len` is derived from the embedding
  size in the decoder's `past_key_values.0.key` shape.
- `eos_token_id` — generation stops on this id. Phi-2 uses `50256`
  (`<|endoftext|>`).
- `max_tokens` — hard upper bound on generated tokens.

Returns the flat `Int64[]` of generated token ids (prefix excluded,
EOS excluded). Pair with [`tokenizer.decode_bpe`](tokenization.md#tokenizerdecode_bpe)
+ [`tokenizer.byte_level_decode`](tokenization.md#tokenizerbyte_level_decode)
to surface as UTF-8.

```sql
DECLARE prefix_embeds Float32[] = array_concat(visual_features, text_embeds);
DECLARE token_ids Int64[] = decode_decoder_only(
    'decoder', 'embed_tokens', prefix_embeds, 50256::Int64, 256::Int32);
```

Canonical use: `models.moondream2`.

## Pooling & Normalization

### mean_pool_masked

`mean_pool_masked(embeddings, attention_mask, embedding_dim)` → `Float32[]`

Mean-pool a flat `[seq_len, embedding_dim]` token-embedding tensor
along the sequence axis, weighting by `attention_mask`. The standard
sentence-transformers pooler — produces one fixed-length vector per
input regardless of token count. `embeddings` is the flat
last-hidden-state output of a BERT encoder (length =
`seq_len × embedding_dim`); `attention_mask` carries one Int64 per
token (1 = real token, 0 = padding); `embedding_dim` is the model's
hidden size (384 for MiniLM-L6, 768 for `base`, 1024 for `large`).

```sql
DECLARE pooled Float32[] = mean_pool_masked(
    last_hidden_state, encoded['attention_mask'], 384::Int32);
RETURN l2_normalize(pooled)
```

Canonical use: `models.all_minilm_l6_v2`.
For L2 normalization to the unit sphere — the second half of the
sentence-transformers recipe — see `l2_normalize` in
[Vector Functions](vector.md).

## Detector Preprocess

### yolox_preprocess

`yolox_preprocess(img, target_size)` → `Float32[]`

YOLOX-canonical preprocessor: letterbox to a `target_size × target_size`
square with 114-gray padding (aspect preserved), swap to BGR channel
order, pack as CHW Float32 in the 0–255 range — *no* per-channel mean
/ std normalisation, because the YOLOX backbone applies its own
normalisation inside the ONNX graph.

Output length = `3 × target_size × target_size`. The 1-arg
[`infer()`](../sql/create-model.md#infer) form unwraps it into the
model's pinned `[1, 3, target_size, target_size]` input.

```sql
DECLARE tensor Float32[] = yolox_preprocess(img, 640);
DECLARE raw Float32[] = infer(
    tensor, [1::Int32, 3::Int32, 640::Int32, 640::Int32]);
```

Canonical use: `models.yolox_s`, `models.yolox_m`, `models.yolox_l`, `models.yolox_nano`, `models.yolox_tiny`, `models.yolox_x`

### sam_preprocess

`sam_preprocess(img)` → `Struct<tensor: Float32[], scale: Float32, height: Int32, width: Int32>`

SAM-canonical preprocessor: aspect-preserving resize so the longest
side becomes 1024 px, pack as HWC Float32 (the encoder graph applies
its own normalisation and pads to a 1024×1024 square internally). The
returned struct carries the resized height/width (needed to set the
encoder's dynamic input shape) plus the `scale` factor that maps
original-image prompt coordinates into the encoder's 1024-space.

```sql
DECLARE prep Struct = sam_preprocess(img);
DECLARE embeddings Float32[] = infer(
    'encoder', prep['tensor'],
    [prep['height'], prep['width'], 3::Int32]);
-- Original-pixel click at (x, y) maps to encoder-space (x*scale, y*scale):
DECLARE point_coords Float32[] = [
    CAST(x AS Float32) * prep['scale'],
    CAST(y AS Float32) * prep['scale'],
    0.0::Float32, 0.0::Float32];
```

Canonical use: `models.mobilesam`.

## Detector Postprocess

### yolox_postprocess

`yolox_postprocess(raw, labels, img, target_size, conf_thresh, iou_thresh)` → `Array<LabeledDetection>`

YOLOX bbox decoder + class-aware NMS + reverse letterbox.

- `raw` — the flat `[1, 8400, 85]` head output from
  `infer()` (4 bbox + 1 objectness + 80 class scores per anchor over
  the three FPN strides 8/16/32 at `target_size = 640`).
- `labels` — class-name strings, one per class index. Typically
  loaded with [`read_string_list`](#read_string_list).
- `img` — original image; used for the reverse letterbox step.
- `target_size` — the input side length passed to `yolox_preprocess`.
- `conf_thresh` — pre-NMS confidence floor (objectness × max class
  score). Default `0.25`.
- `iou_thresh` — NMS IoU overlap threshold. Default `0.45`.

Returns one `LabeledDetection` per surviving box (nested `bbox` +
`label` + `score`, all in original-image pixel coordinates).

```sql
DECLARE labels Array<String> = read_string_list('coco-classes.json');
RETURN yolox_postprocess(raw, labels, img, 640, conf_thresh, iou_thresh)
```

Canonical use: `models.yolox_s`, `models.yolox_m`, `models.yolox_l`, `models.yolox_nano`, `models.yolox_tiny`, `models.yolox_x`

### rtdetr_postprocess

`rtdetr_postprocess(logits, boxes, labels, img, conf_thresh)` → `Array<LabeledDetection>`

RT-DETR DETR-style two-output postprocess. `logits` is the
`[1, num_queries, num_classes]` class head; `boxes` is the
`[1, num_queries, 4]` cxcywh-normalised box head. Resolves each
query's best class, applies softmax to derive `score`, filters by
`conf_thresh`, and converts cxcywh → xywh in original-image pixel
coordinates. No NMS (DETR-family detectors are trained to emit
deduplicated predictions).

Canonical use: `models.rtdetr_r18`.

### dbnet_postprocess

`dbnet_postprocess(prob, h, w, scale_x, scale_y, pixel_threshold, box_score_threshold, min_size, unclip_ratio)` → `Array<RegionScore>`

PaddleOCR / DBNet text-detection postprocess.

- `prob` — flat `[1, 1, h, w]` probability map at the *resized*
  resolution (the output of the detector ONNX).
- `h`, `w` — resized-image dimensions matching `prob`.
- `scale_x`, `scale_y` — multipliers to map resized-space
  coordinates back to original-image pixel space
  (`image_width(orig) / w`, `image_height(orig) / h`).
- `pixel_threshold` — per-pixel cutoff for the binary mask. Default
  `0.3`.
- `box_score_threshold` — mean-probability floor for keeping a region.
  Default `0.6`.
- `min_size` — smallest accepted side length, in resized-space pixels.
  Default `3`.
- `unclip_ratio` — DBNet polygon-expansion factor (the canonical Vatti
  clipping inverse). Default `1.5`.

Returns `Array<RegionScore>` (label = ordinal string, score = mean
probability, bbox in original-image pixel coordinates).

Canonical use: `models.paddleocr_v4_det`.

## Mask Postprocess

### binary_mask_from_logits

`binary_mask_from_logits(plane, h, w, threshold)` → `Image`

Threshold a flat `[h × w]` Float32 logit plane at `> threshold` and
pack the result as a binary grayscale RGBA `Image` — the same shape
`u2net` and `mobilesam_point` emit. Pixels above the threshold are
opaque white; pixels at or below are transparent.

```sql
RETURN binary_mask_from_logits(best_plane, orig_h, orig_w, 0.0::Float32)
```

Canonical use: `models.mobilesam_point`.

### mask_nms_planes

`mask_nms_planes(planes, scores, h, w, iou_threshold)` → `Array<Image>`

Mask-IoU non-maximum suppression for SAM-style multi-candidate
segmentation. `planes` is the concatenation of `N` flat
`[h × w]` Float32 logit planes (total length =
`N × h × w`); `scores` is the parallel `Float32[N]` of per-candidate
scores. Sort by score desc, threshold each plane at `logit > 0`,
suppress any candidate whose mask-IoU vs. a higher-scoring kept
candidate exceeds `iou_threshold`, and emit the survivors as
`Array<Image>` (binary RGBA, original resolution).

Canonical use: `models.mobilesam`.

### sam_stability_score

`sam_stability_score(plane, h, w, delta)` → `Float32`

SAM's mask-quality metric: the IoU of the mask thresholded at
`logit > +delta` vs. the mask thresholded at `logit > -delta`. A
score near 1.0 means the mask boundary is sharp (small threshold
perturbations don't change it); near 0 means the boundary is fuzzy.
The canonical SAM pipeline keeps candidates whose stability ≥ 0.95.

```sql
DECLARE stability Float32 = sam_stability_score(plane, orig_h, orig_w, 1.0::Float32);
IF stability >= 0.97::Float32 BEGIN ... END
```

## Depth → Image

### depth_map_to_image

`depth_map_to_image(values, source_h, source_w, target_h, target_w[, invert])` → `Image`

Resize a flat `[source_h × source_w]` depth tensor to
`target_h × target_w` (bilinear), normalise to 0–255, and pack as a
grayscale RGBA `Image`. The common final step of every depth model
in the zoo — the raw ONNX output is at the network's internal
resolution and needs to come back to the original image's size for
display.

The optional `invert` flag (default `false`) flips the colour ramp:
useful for models like DPT/MiDaS that emit *inverse* depth (nearer =
larger value) so the output renders with the dark-near / light-far
convention most viewers expect.

Canonical uses: every model in `models.depth_anything_v2_small`, `models.midas_small`.

## Diffusion

### sd_turbo_schedule

`sd_turbo_schedule(steps)` → `Struct<sigmas: Float32[steps + 1], timesteps: Float32[steps]>`

Stable Diffusion Turbo (ADD-distilled SD 2.1) sampling schedule for
the Euler ancestral sampler. Returns the per-step sigma sequence
(`steps + 1` values — one per state plus the terminal zero) and the
per-step timestep index the UNet expects. `steps` is bounded
1–8 in practice; the model was distilled for 1–4 and produces
diminishing returns beyond that.

```sql
DECLARE schedule Struct = sd_turbo_schedule(steps);
DECLARE sigmas Float32[] = schedule['sigmas'];
DECLARE timesteps Float32[] = schedule['timesteps'];

DECLARE latents Float32[] = array_scale(
    sample_normal(4 * latent_dim * latent_dim), sigmas[1]);
```

Canonical use: `models.sd_turbo`.

### sample_normal

`sample_normal(count)` → `Float32[count]`

`count` standard-normal samples via Box–Muller. The initial-noise
source for every diffusion body — feeds the first sigma scaling step
to seed the latent. Non-pure: each call draws fresh randomness, so
two `sample_normal(n)` calls in the same query produce independent
draws.

For reproducible noise within a session, set the seed via
[`set_random_seed`](random.md#set_random_seed).

## Mesh & 3D

### mesh_from_triplane

`mesh_from_triplane(session_alias, triplane, triplane_shape, resolution, isolevel, radius, chunk_size)` → `Mesh` ![Body-scoped](https://img.shields.io/badge/-body--scoped-blue)

**Body-scoped.** Chunked NeRF query loop + Marching Cubes for
TripoSR-style triplane → mesh extraction.

- `session_alias` — the [`USING ... AS <alias>`](../sql/create-model.md#the-using-clause)
  name of the per-query NeRF MLP ONNX session.
- `triplane` — flat `[1, 3, channels, side, side]` feature volume
  emitted by the one-shot triplane generator.
- `triplane_shape` — the 5-element shape array (e.g.
  `[1, 3, 40, 64, 64]`).
- `resolution` — voxel grid resolution per axis. `resolution³` query
  points get sampled across the unit cube `[-radius, +radius]³`. Typical
  values: 128 (preview), 256 (reference), 384+ (high quality).
- `isolevel` — Marching Cubes density threshold. Higher = tighter
  surface, lower = puffier. TripoSR reference is `25.0`.
- `radius` — half-side of the query cube. Use the model's trained
  radius, not 1.0 — TripoSR's reference is `0.87` (slightly larger
  than 0.5·√3, the sphere that contains the unit cube). Sampling
  outside the trained range produces phantom cube-face slices.
- `chunk_size` — query points per NeRF dispatch. Trades dispatch
  count vs. VRAM per call.

Returns a `Mesh` with position + per-vertex RGBA8 colour (a second
chunked NeRF pass evaluates colour at each Marching Cubes vertex).

Like every dispatching scalar (`infer`, `llama_chat`, ...), this
function is only callable from inside a `CREATE MODEL` body — the
planner refuses every other call site.

```sql
DECLARE raw Mesh = mesh_from_triplane(
    'nerf', triplane_features,
    [1::Int32, 3::Int32, 40::Int32, 64::Int32, 64::Int32],
    resolution, isolevel, 0.87::Float32, chunk_size);
```

Canonical use: `models.triposr`.

### mesh_swap_axes

`mesh_swap_axes(mesh, source_axes)` → `Mesh`

Permute mesh vertex axes. `source_axes` is a 3-element `Int32[]`
naming which input axis becomes each output axis (1-based). The
cyclic permutation `[2, 3, 1]` reads "out.X = in.Y, out.Y = in.Z,
out.Z = in.X" — the rotation from TripoSR's training frame (+X back,
+Y right, +Z up) to the glTF / Three.js convention (+X right, +Y up,
+Z toward viewer).

Determinant +1 permutations preserve winding (no triangle index
reversal needed); determinant −1 permutations flip the orientation,
which the function corrects automatically.

```sql
RETURN mesh_swap_axes(raw, [2::Int32, 3::Int32, 1::Int32])
```

Canonical use: `models.triposr`.

### point_cloud_from_depth_pinhole

`point_cloud_from_depth_pinhole(color, depth, fov_deg)` → `PointCloud`

Unproject a grayscale depth image into a coloured 3D `PointCloud`
under a pinhole-camera model with the given vertical field-of-view
(degrees). `color` and `depth` must have matching dimensions; depth
values are read off the grayscale luminance of the depth image
(typically the output of [`depth_map_to_image`](#depth_map_to_image)
upstream). The resulting `PointCloud` has one point per pixel,
positioned in metric units inferred from FoV + image dimensions, and
each point inherits its RGBA from the corresponding pixel in `color`.

Pair with the monocular-depth models in the zoo for "photo →
point cloud" pipelines without an actual depth camera.

## File-Backed Constants

### read_string_list

`read_string_list(path)` → `Array<String>`

Read a JSON string-array file and return it as a typed
`Array<String>`. Used inside model bodies to side-load class-label
lists (`coco-classes.json`, `imagenet-classes.json`, ...) without
hard-coding them in the SQL. The file is parsed once per path and
cached process-wide, so the per-row cost is a hash-map hit.

`path` follows the same resolution rules as `USING` paths — `file://`
short-circuits; bare relative paths resolve against the
[models directory](../sql/create-model.md#models-directory) (or, more
specifically, against the model's per-version subfolder for catalog
installs).

```sql
DECLARE labels Array<String> = read_string_list('coco-classes.json');
RETURN yolox_postprocess(raw, labels, img, 640, conf_thresh, iou_thresh)
```

Canonical use: `models.yolox_s`.

## See Also

- [CREATE MODEL](../sql/create-model.md) — the DDL surface these functions wire into, including the full body-scoped dispatch surface (`infer`, `infer_outputs`, `llama_chat`, `llama_generate`, `decode_seq2seq`, `decode_decoder_only`).
- [Image Functions](image.md) — tensor conversion (`image_to_tensor_chw`, `tensor_to_image_chw`), normalization presets (`imagenet_mean`, `clip_mean`), and geometric helpers (`image_resize_to_stride`, `image_resize_foreground`, `image_composite_over`).
- [Tokenization Functions](tokenization.md) — `tokenizer.*` family.
- [Audio Functions](audio.md) — `audio_samples`, `audio_to_log_mel`, `audio_to_mono`.
- [Vector Functions](vector.md) — `l2_normalize`, `softmax`, `cosine_similarity`.
- [Models](../models.md) — how `models.X(...)` dispatches and what's in the built-in catalog.
