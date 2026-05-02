-- ============================================================================
-- Metric3D V2 Small — metric depth + surface normals, BSD-2-Clause.
-- ============================================================================
--
-- Catalog id:  metric3d-v2-small               (models/catalog.json)
-- ONNX file:   onnx/model.onnx
-- License:     BSD-2-Clause
-- Upstream:    https://github.com/YvanYin/Metric3D
--              (onnx-community ONNX export of Metric3D V2 ViT-Small)
--
-- ViT-S backbone (~22M params, ~151 MB on disk). Single forward pass
-- emits both metric depth AND surface normals — unusual in the depth
-- zoo and the headline feature of the Metric3D V2 family. Trained on a
-- much larger and more diverse outdoor corpus than ZoeDepth's NYU+KITTI,
-- with a canonical-camera scheme that adapts to arbitrary intrinsics.
--
-- **Three SQL-visible bodies:**
--   1. `metric3d_v2_small`        — visualization Image (near=bright via invert)
--   2. `metric3d_v2_small_meters` — Float32[] metric depth, source-aligned
--   3. `metric3d_v2_small_full`   — Struct with depth + normals + confidence
--
-- **Outputs from the ONNX session** (rank-3 / rank-4 with leading batch=1):
--   predicted_depth      Float32 [1, H, W]      metric meters
--   predicted_normal     Float32 [1, 3, H, W]   per-pixel (nx, ny, nz) unit vector
--   normal_confidence    Float32 [1, H, W]      per-pixel normals reliability
--
-- **Caveat on "metric" depth.** Metric3D V2 calibrates its metric scale
-- against a canonical camera focal length determined by the input
-- resolution. Real-world metric depth requires a focal_length_px
-- correction: `metric_depth = predicted_depth * canonical_focal /
-- actual_focal`. Without the actual focal we emit pseudo-metric depth
-- (correct up to a per-image scalar) — fine for visualization and
-- relative-distance queries. A future companion body taking
-- focal_length_px as a parameter would expose the true metric output.
--
-- Pipeline (matches midas / dpt / depth-anything shape):
--   1. image_to_tensor_chw — stretch-resize to 518×518, ImageNet mean/std.
--   2. infer / infer_outputs — single ONNX dispatch.
--   3a. depth_map_to_image — visualization with invert=true (Metric3D
--                            emits real meters where bigger=farther, so
--                            invert keeps "near=bright" consistent with
--                            the inverse-depth catalog entries).
--   3b. array_resize_2d   — metric variant resizes native depth back to
--                            source dimensions for per-pixel alignment.
--   3c. RETURN literal    — full variant packs the three outputs into a
--                            Struct via `infer_outputs`.
-- ============================================================================

-- ---- Visualization variant (returns grayscale Image) ----------------------

CREATE OR REPLACE MODEL metric3d_v2_small(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'metric3d-v2-small/2026-05-29/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth Float32[] = infer(
    tensor,
    [1::Int32, 3::Int32, 518::Int32, 518::Int32]);
  -- Metric3D V2's DPT-style decoder emits 516×516 (not 518×518) due to
  -- boundary trimming. Read the actual output dims off the trailing axes
  -- so this body works for any variant that drifts from input size.
  DECLARE ndim Int32 = array_ndims(depth);
  DECLARE depth_h Int32 = array_length(depth, ndim - 1);
  DECLARE depth_w Int32 = array_length(depth, ndim);
  RETURN depth_map_to_image(depth, depth_h, depth_w, image_height(img), image_width(img), true)
END;

-- ---- Metric variant (returns raw meters as a shape-aware Float32 array) ---

CREATE OR REPLACE MODEL metric3d_v2_small_meters(img Image)
  RETURNS Array<Float32>
IMPLEMENTS DepthEstimatorMetric
USING 'metric3d-v2-small/2026-05-29/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth_native Array<Float32> = infer(
    tensor,
    [1::Int32, 3::Int32, 518::Int32, 518::Int32]);
  RETURN array_resize_2d(depth_native, image_height(img), image_width(img))
END;

-- ---- Full bundle: depth + normals + confidence in one forward pass --------
--
-- Single dispatch surfacing every head Metric3D V2 emits. Useful for
-- photogrammetry / 3D-reconstruction workflows that want surface normals
-- alongside depth, and for downstream consumers that filter by normal
-- confidence. The depth field is returned at native 518×518 to avoid
-- bilinear-resize cost when only intrinsics or normals matter; caller
-- composes with `array_resize_2d` if per-pixel alignment with the source
-- image is required.

CREATE OR REPLACE MODEL metric3d_v2_small_full(img Image)
  RETURNS Struct<
    depth Array<Float32>,
    normals Array<Float32>,
    normal_confidence Array<Float32>
  >
USING 'metric3d-v2-small/2026-05-29/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE outputs Struct = infer_outputs(
    tensor,
    [1::Int32, 3::Int32, 518::Int32, 518::Int32]);
  RETURN {
    depth:             outputs['predicted_depth'],
    normals:           outputs['predicted_normal'],
    normal_confidence: outputs['normal_confidence']
  }
END
