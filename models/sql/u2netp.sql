-- ============================================================================
-- U²-Net Lite (u2netp) — salient object segmentation, Apache-2.0.
-- ============================================================================
--
-- Catalog id:  u2netp        (models/catalog.json)
-- ONNX file:   u2netp.onnx
-- License:     Apache-2.0
-- Upstream:    https://github.com/xuebinqin/U-2-Net
--
-- Distilled U²-Net (4.7M params, ~4.7 MB). Same task as full u2net but
-- ~35× smaller and ~10× faster, at the cost of some boundary precision on
-- fine structures (hair, fur). The recommended default — use u2net when
-- those fine boundaries matter. See u2net.sql for the detailed pipeline
-- notes; u2netp is identical bar the ONNX file.
-- ============================================================================

CREATE OR REPLACE MODEL u2netp(img Image) RETURNS Image
IMPLEMENTS BackgroundRemover
USING 'u2netp/u2netp.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img, [320, 320], imagenet_mean(), imagenet_std());
  DECLARE mask Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(320 AS Int32), CAST(320 AS Int32)]);
  RETURN depth_map_to_image(mask, 320, 320, image_height(img), image_width(img))
END
