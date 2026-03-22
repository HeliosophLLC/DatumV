-- ============================================================================
-- GLPN-NYU — lightweight monocular depth, Apache-2.0.
-- ============================================================================
--
-- Catalog id:  glpn-nyu                        (models/catalog.json)
-- ONNX file:   onnx/model.onnx
-- License:     Apache-2.0
-- Upstream:    https://github.com/vinvino02/GLPDepth
--              (Xenova ONNX export of vinvino02/glpn-nyu)
--
-- Global-Local Path Network for monocular depth from KAIST (2022).
-- EfficientFormer encoder + custom hierarchical decoder, ~52M params.
-- Older than Depth Anything V2 but useful as a lightweight baseline AND
-- as an architectural-diversity row in a comparison demo — the backbone
-- family is distinct from the DPT / DINOv2 lineage every other depth
-- entry in the catalog uses. NYU indoor training.
--
-- Pipeline (mirrors midas-small / dpt-large shape):
--   1. image_to_tensor_chw   — stretch-resize to 480×480 (NYU training
--                              size), RGB, ImageNet mean/std.
--   2. infer                 — single ONNX dispatch; output is a single-
--                              channel inverse-depth Float32 map.
--   3. depth_map_to_image    — min-max normalize + grayscale-as-RGBA pack +
--                              bilinear resize back to source dims.
-- ============================================================================

CREATE OR REPLACE MODEL glpn_nyu(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'glpn-nyu/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [480, 480],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(480 AS Int32), CAST(480 AS Int32)]);
  RETURN depth_map_to_image(depth, 480, 480, image_height(img), image_width(img))
END
