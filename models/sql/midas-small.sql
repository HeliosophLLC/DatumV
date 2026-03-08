-- ============================================================================
-- MiDaS v2.1 small — monocular depth estimation, MIT.
-- ============================================================================
--
-- Catalog id:  midas-small        (models/catalog.json)
-- ONNX file:   midas_v21_small_256.onnx
-- License:     MIT
-- Upstream:    https://github.com/isl-org/MiDaS
--
-- Intel ISL's lightweight EfficientNet-Lite3 + depth-decoder monocular depth
-- estimator. ~21M params, BGR input, 256×256, ImageNet normalisation. Pick
-- this over DPT-Large when latency / disk size matter more than accuracy.
-- Outputs a single-channel inverse depth map (bigger value = closer) in
-- arbitrary units; depth_map_to_image handles per-image rescale + resize.
--
-- Pipeline:
--   1. image_to_tensor_chw_bgr — stretch-resize to 256×256, BGR channel
--                                order, ImageNet mean/std normalisation.
--                                BGR is the canonical MiDaS v2 convention
--                                (cv2 imread() loads BGR by default and the
--                                original training pipeline never swapped).
--   2. infer                   — single ONNX dispatch; output is [1, 256, 256]
--                                inverse-depth Float32.
--   3. depth_map_to_image      — min-max normalize + grayscale-pack + resize.
-- ============================================================================

CREATE OR REPLACE MODEL midas_small(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'midas-small/midas_v21_small_256.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw_bgr(
    img,
    [256, 256],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(256 AS Int32), CAST(256 AS Int32)]);
  RETURN depth_map_to_image(depth, 256, 256, image_height(img), image_width(img))
END
