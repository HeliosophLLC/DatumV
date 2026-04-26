-- ============================================================================
-- U²-Net Full — salient object segmentation, Apache-2.0.
-- ============================================================================
--
-- Catalog id:  u2net        (models/catalog.json)
-- ONNX file:   u2net.onnx
-- License:     Apache-2.0
-- Upstream:    https://github.com/xuebinqin/U-2-Net
--
-- Xuebin Qin et al.'s full U²-Net (176M params, ~170 MB). Returns a single-
-- channel saliency mask sized to match the input — pair with image_cutout()
-- for background removal. Pick over u2netp only when fine-edge boundaries
-- (hair, fur, lace) matter and disk + latency aren't constrained.
--
-- Pipeline:
--   1. image_to_tensor_chw   — stretch-resize to 320×320, RGB, ImageNet stats.
--   2. infer                 — single ONNX dispatch; the graph emits seven
--                              deep-supervision tensors d0..d6 (in declaration
--                              order); infer() returns the first (d0), the
--                              final fused saliency map [1, 1, 320, 320].
--   3. depth_map_to_image    — per-image min-max normalize (the upstream
--                              normPRED step) + grayscale-as-RGBA pack +
--                              bilinear resize back to source dimensions.
-- ============================================================================

CREATE OR REPLACE MODEL u2net(img Image) RETURNS Image
IMPLEMENTS BackgroundRemover
USING 'u2net/u2net.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img, [320, 320], imagenet_mean(), imagenet_std());
  DECLARE mask Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(320 AS Int32), CAST(320 AS Int32)]);
  RETURN depth_map_to_image(mask, 320, 320, image_height(img), image_width(img))
END
