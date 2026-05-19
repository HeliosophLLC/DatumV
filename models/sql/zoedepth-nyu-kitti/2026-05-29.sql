-- ============================================================================
-- ZoeDepth NYU-KITTI — metric monocular depth, MIT.
-- ============================================================================
--
-- Catalog id:  zoedepth-nyu-kitti              (models/catalog.json)
-- ONNX file:   model.onnx
-- License:     MIT
-- Upstream:    https://github.com/isl-org/ZoeDepth
--              (Heliosoph.DatumV ONNX export from Intel/zoedepth-nyu-kitti)
--
-- Intel ISL's ZoeDepth combines DPT-Large's relative-depth backbone with
-- dual metric heads (NYU indoor + KITTI outdoor calibration) to emit
-- **metric** depth — real-world distances in meters, not arbitrary units.
-- Use when absolute scale matters: 3D point-cloud reconstruction with
-- consistent scale across images, AR-style distance overlays, robotics.
-- Much heavier than the relative-depth variants (~1.3 GB); see the fp16
-- sibling (`zoedepth_nyu_kitti_fp16`) for ~half the disk footprint with
-- the same metric output on fp16-native GPUs/NPUs.
--
-- Pipeline (matches dpt-large shape; ZoeDepth shares the DPT backbone):
--   1. image_to_tensor_chw   — stretch-resize to 384×384, RGB,
--                              ImageNet mean/std.
--   2. infer                 — single ONNX dispatch; output is a single-
--                              channel Float32 metric-depth map (units:
--                              meters).
--   3. depth_map_to_image    — min-max normalize + grayscale-as-RGBA pack +
--                              bilinear resize back to source dims.
--                              `invert => true` keeps the "near = bright"
--                              convention consistent with MiDaS / DPT /
--                              Depth-Anything (which emit inverse depth so
--                              bigger value = closer already maps that way).
--                              For metric depth bigger value = farther, so
--                              without the flip the visualization would
--                              read inverted vs. the rest of the catalog.
--
-- Caveat: the grayscale-pack step in `depth_map_to_image` performs a
-- per-image min-max rescale, which **discards the metric units** — the
-- returned Image is a visualization only. A follow-up that returns the
-- raw meters (paired with the multi-dim array work) would let callers do
-- real distance math; not implemented yet.
-- ============================================================================

-- ---- Visualization variant (returns grayscale Image) ----------------------

CREATE OR REPLACE MODEL zoedepth_nyu_kitti(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'zoedepth-nyu-kitti/2026-05-29/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [384, 384],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(384 AS Int32), CAST(384 AS Int32)]);
  RETURN depth_map_to_image(depth, 384, 384, image_height(img), image_width(img), true)
END;

-- ---- Metric variant (returns raw meters as a shape-aware Float32 array) ---
--
-- Same dispatch, but skips the depth_map_to_image min-max-normalize step
-- that throws away the absolute units. Useful for downstream consumers
-- (PointCloud reconstruction, AR distance overlay, robotics) that need
-- the actual meter values per pixel.
--
-- The ONNX session emits depth at its native 384×384 resolution. We
-- bilinear-resize back to the source image's pixel grid via
-- `array_resize_2d` so the returned array aligns per-pixel with the
-- input image — exactly what point_cloud_from_depth_pinhole expects, and
-- what any per-pixel sampling downstream needs. Bilinear interpolation
-- is linear in meters so the metric units are preserved across the
-- resample.

CREATE OR REPLACE MODEL zoedepth_nyu_kitti_meters(img Image)
  RETURNS Array<Float32>
IMPLEMENTS DepthEstimatorMetric
USING 'zoedepth-nyu-kitti/2026-05-29/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [384, 384],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth_native Array<Float32> = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(384 AS Int32), CAST(384 AS Int32)]);
  RETURN array_resize_2d(depth_native, image_height(img), image_width(img))
END
