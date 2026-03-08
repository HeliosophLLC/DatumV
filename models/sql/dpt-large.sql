-- ============================================================================
-- DPT-Large v3.1 — monocular depth estimation, Apache-2.0.
-- ============================================================================
--
-- Catalog id:  dpt-large        (models/catalog.json)
-- ONNX file:   dpt_large_384.onnx
-- License:     Apache-2.0
-- Upstream:    https://github.com/isl-org/MiDaS
--
-- Intel ISL's Dense Prediction Transformer for monocular depth estimation.
-- ~344M params, RGB input, 384×384, normalised to [-1, 1] via
-- mean=[0.5,0.5,0.5] std=[0.5,0.5,0.5]. Outputs a single-channel inverse
-- depth map (bigger value = closer) in arbitrary units; depth_map_to_image
-- handles the per-image min-max rescale, grayscale-pack, and resize back
-- to the source image's dimensions.
--
-- Pipeline:
--   1. image_to_tensor_chw   — stretch-resize to 384×384, RGB, ±1 normalize.
--   2. infer                 — single ONNX dispatch; output is [1, 384, 384]
--                              (or [1, 1, 384, 384]) inverse-depth Float32.
--   3. depth_map_to_image    — min-max normalize + grayscale-as-RGBA pack +
--                              bilinear resize to original image's H × W.
-- ============================================================================

CREATE OR REPLACE MODEL dpt_large(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'dpt-large/dpt_large_384.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [384, 384],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)]);
  DECLARE depth Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(384 AS Int32), CAST(384 AS Int32)]);
  RETURN depth_map_to_image(depth, 384, 384, image_height(img), image_width(img))
END
