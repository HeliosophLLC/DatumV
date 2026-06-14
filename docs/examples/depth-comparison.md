---
title: Same input, four depth estimators
---

![Depth Anything v2, Depth Anything v3, MiDaS, and DPT depth estimates of the same input](../figures/depth_comparison.jpg)

The same input run through four depth estimators in a single query — the only thing that changes between the projections is the model name. Swapping models is a column-level concern, not a pipeline-level one.

**Depth Anything v2** and **Depth Anything v3** are the current state of the art for relative depth: both produce high-quality, edge-aware depth maps. DAv3 is the better default in most cases. **MiDaS** and **DPT** are an earlier generation and visibly softer — included here so the gap between generations is easy to see.

The query also runs `DAv3_full` — the same DAv3 model with its full structured output exposed rather than just the depth Image. It's there to demonstrate the struct shape that some catalog models return; see [What's in the struct](#whats-in-the-struct) below.

```sql
SELECT
    LET depth_anything_v2 = models.depth_anything_v2_base(file) AS DAv2,
    LET depth_anything_v3 = models.depth_anything_v3_large(file) AS DAv3,
    LET depth_anything_v3_full = models.depth_anything_v3_large_full(file) AS DAv3_full,
    LET midas = models.midas_small(file) AS midas,
    LET dpt = models.dpt_large(file) AS dpt,
    file AS baseline,
    file_name
FROM datasets.coco_val2017
LIMIT 32
```

The `LET ... AS` pattern evaluates each model call once per row and exposes the result as a named output column — see [LET Bindings](../sql/let-bindings.md) for the full surface.

## What's in the struct

`models.depth_anything_v3_large_full` returns a struct with four fields rather than a single depth Image:

```json
{
  "depth":      "<f32[H, W]>",
  "confidence": "<f32[H, W]>",
  "extrinsics": "<f32[1, 1, 3, 4]>",
  "intrinsics": "<f32[1]>"
}
```

| Field | Meaning |
|---|---|
| `depth` | The depth map — one value per input pixel, aligned to the source image dimensions. |
| `confidence` | Per-pixel confidence in the depth estimate. Useful for masking unreliable regions (sky, mirrors, specular surfaces) before downstream geometry. |
| `extrinsics` | The camera's pose — a 3×4 rotation + translation matrix. The leading `1`s are batch and view dimensions for multi-image setups. |
| `intrinsics` | A compact encoding of the camera's projection parameters — typically a normalized focal length. |

For visualization, only `depth` is needed — access it as `depth_anything_v3_full.depth` after the LET, or use `depth_map_to_image` to convert it into a depth map image, or use `models.depth_anything_v3_large` for a pre-converted output. The other fields enable downstream geometry — unprojection, multi-view fusion, pose-consistent point clouds — to work in real camera coordinates rather than relying on a guessed FOV.

See [Structs](../sql/struct.md) for the full surface of dot access, destructuring, and the catalog of named shapes.

## Relative vs metric depth

The four models above produce **relative** depth — values that order pixels from near to far but don't carry real-world units. Comparing two pixels' values within one frame is meaningful; the number `0.4` doesn't mean 0.4 metres. Relative-depth models are trained on diverse imagery without ground-truth scale, so the output is unitless and self-normalised per image.

**Metric** depth estimators output values in real units (typically metres). They're trained on data captured with ground-truth depth sensors — LiDAR, stereo rigs, structured light — so the output is interpretable physically. The catalog ships metric variants such as `models.zoedepth_nyu_kitti` (indoor + outdoor scenes) and `models.depth_anything_v3_large_*`.

Pick relative depth for visualisation and within-frame analysis. Pick metric depth when the numbers need to mean something outside the image — 3D reconstruction, robotics, AR, anything that combines depth across frames or cameras consistently.

Open the **Model Catalog** tab for the depth-estimator variants installed in your catalog and their licenses.
