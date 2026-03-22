-- ============================================================================
-- ZoeDepth NYU-KITTI — metric monocular depth, MIT.
-- ============================================================================
--
-- Catalog id:  zoedepth-nyu-kitti              (models/catalog.json)
-- ONNX file:   model.onnx
-- License:     MIT
-- Upstream:    https://github.com/isl-org/ZoeDepth
--              (Heliosoph ONNX export from Intel/zoedepth-nyu-kitti)
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
--
-- Caveat: the grayscale-pack step in `depth_map_to_image` performs a
-- per-image min-max rescale, which **discards the metric units** — the
-- returned Image is a visualization only. A follow-up that returns the
-- raw meters (paired with the multi-dim array work) would let callers do
-- real distance math; not implemented yet.
-- ============================================================================

CREATE OR REPLACE MODEL zoedepth_nyu_kitti(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'zoedepth-nyu-kitti/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [384, 384],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(384 AS Int32), CAST(384 AS Int32)]);
  RETURN depth_map_to_image(depth, 384, 384, image_height(img), image_width(img))
END
