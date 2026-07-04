---
title: Same input, four depth estimators
---

![Depth Anything v2, Depth Anything v3, MiDaS, and DPT depth estimates of the same input](../figures/depth_comparison.jpg)

The same input run through four depth estimators in a single query — the only thing that changes between the projections is the model name. Swapping models is a column-level concern, not a pipeline-level one.

**Depth Anything v2** and **Depth Anything 3 Metric** are the current state of the art: both produce high-quality, edge-aware depth maps, and DA3 Metric is the better default in most cases — its underlying depth is metric, not just relative (see [Relative vs metric depth](#relative-vs-metric-depth) below). **MiDaS** and **DPT** are an earlier generation and visibly softer — included here so the gap between generations is easy to see.

Some catalog models return a struct rather than a single depth Image — `models.da3_base_full` is the depth-family example; see [What's in the struct](#whats-in-the-struct) below.

```sql
SELECT
    LET depth_anything_v2 = models.depth_anything_v2_base(file) AS DAv2,
    LET da3_metric = models.da3metric_large(file) AS DA3m,
    LET midas = models.midas_small(file) AS midas,
    LET dpt = models.dpt_large(file) AS dpt,
    file AS baseline,
    file_name
FROM datasets.coco_val2017
LIMIT 32
```

The `LET ... AS` pattern evaluates each model call once per row and exposes the result as a named output column — see [LET Bindings](../sql/let-bindings.md) for the full surface.

## What's in the struct

`models.da3_base_full` returns a struct with three fields rather than a single depth Image:

```json
{
  "depth":      "<f32[H, W]>",
  "confidence": "<f32[H, W]>",
  "intrinsics": "<f32[3, 3]>"
}
```

| Field | Meaning |
|---|---|
| `depth` | The depth map — one value per input pixel, aligned to the source image dimensions. |
| `confidence` | Per-pixel confidence in the depth estimate. Useful for masking unreliable regions (sky, mirrors, specular surfaces) before downstream geometry. |
| `intrinsics` | The camera matrix K the model estimates for the photo — focal lengths and principal point, already rescaled to source-pixel coordinates. A per-image lens estimate, no EXIF needed. |

For visualization, only `depth` is needed — access it as `r.depth` after the LET, or use `depth_map_to_image` to convert it into a depth map image, or use `models.da3_base` for a pre-converted output. The other fields enable downstream geometry — unprojection, confidence gating, camera-aware point clouds — to work in real camera coordinates rather than relying on a guessed FOV. (`models.da3metric_large_full` is the same idea on the metric model: depth in metres plus a sky mask.)

See [Structs](../sql/struct.md) for the full surface of dot access, destructuring, and the catalog of named shapes.

## Relative vs metric depth

Three of the models above (DAv2, MiDaS, DPT) produce **relative** depth — values that order pixels from near to far but don't carry real-world units. Comparing two pixels' values within one frame is meaningful; the number `0.4` doesn't mean 0.4 metres. Relative-depth models are trained on diverse imagery without ground-truth scale, so the output is unitless and self-normalised per image.

**Metric** depth estimators output values in real units (typically metres). They're trained on data captured with ground-truth depth sensors — LiDAR, stereo rigs, structured light — so the output is interpretable physically. The `DA3m` column above is the visualization body of one: the catalog ships `models.da3metric_large_meters` (general scenes; takes the camera fov, default 60°) alongside `models.zoedepth_nyu_kitti_meters` (indoor + driving scenes, no fov needed).

Pick relative depth for visualisation and within-frame analysis. Pick metric depth when the numbers need to mean something outside the image — 3D reconstruction, robotics, AR, anything that combines depth across frames or cameras consistently.

Open the **Model Catalog** tab for the depth-estimator variants installed in your catalog and their licenses.
