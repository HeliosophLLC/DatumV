-- ============================================================================
-- Depth Anything V2 Small — monocular depth estimation, Apache-2.0.
-- ============================================================================
--
-- Catalog id:  depth-anything-v2-small        (models/catalog.json)
-- ONNX file:   onnx/model.onnx
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/depth-anything/Depth-Anything-V2-Small
--              (onnx-community ONNX export)
--
-- DINOv2 ViT-S encoder + DPT-style depth decoder. ~24M params. Current
-- SOTA permissive-license general-purpose monocular depth — faster than
-- DPT-Large with often sharper boundaries. **Relative** depth (not
-- metric) in arbitrary units; bigger value = closer to camera.
--
-- Pipeline (matches dpt-large / midas-small shape):
--   1. image_to_tensor_chw   — stretch-resize to 518×518 (DINOv2 canonical),
--                              RGB, ImageNet mean/std (DINOv2 convention).
--   2. infer                 — single ONNX dispatch; output is a single-
--                              channel inverse-depth Float32 map at 518×518.
--   3. depth_map_to_image    — min-max normalize + grayscale-as-RGBA pack +
--                              bilinear resize back to the source image's
--                              original H × W.
-- ============================================================================

CREATE OR REPLACE MODEL depth_anything_v2_small(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'depth-anything-v2-small/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(518 AS Int32), CAST(518 AS Int32)]);
  RETURN depth_map_to_image(depth, 518, 518, image_height(img), image_width(img))
END
