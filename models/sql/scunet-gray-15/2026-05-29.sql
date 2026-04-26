-- ============================================================================
-- SCUNet Grayscale (σ=15) — Apache-2.0.
-- ============================================================================
--
-- Catalog id:  scunet-gray-15                  (models/catalog.json)
-- ONNX file:   scunet_gray_15.onnx (+ .onnx.data weights sidecar)
-- License:     Apache-2.0
-- Upstream:    https://github.com/cszn/SCUNet
--              (Heliosoph ONNX export)
--
-- Single-channel Gaussian denoising at σ=15 (light noise). ~3× faster
-- than running the color variant on a channel-replicated tensor since
-- the network's first conv has 3× less FLOPs at the input. Use for
-- medical / document / scientific grayscale denoising at matched σ≈15
-- conditions.
--
-- Body shape is the color sibling's (scunet-color-real-psnr.sql) plus
-- one substitution: the input tensor comes from `image_to_tensor_chw_gray`
-- (BT.601 luma, single channel) instead of `image_to_tensor_chw` (RGB,
-- three channels), and the infer shape's channel dim is 1 instead of 3.
-- See scunet-color-real-psnr.sql for the /64-stride preprocessing and
-- `max_side` memory-cap rationale (both shared by the grayscale variants).

CREATE OR REPLACE MODEL scunet_gray_15(
  img      Image,
  max_side Int32 = 1024
    CHECK (max_side BETWEEN 64 AND 4096) STEP 64 UNIT 'pixels'
    COMMENT 'Long-side cap after aspect-preserving resize. See scunet-color-real-psnr.sql for memory-budget rationale (default ~1 GB activations, 2048 ≈ 4 GB, 4096 ≈ 12 GB).'
) RETURNS Image
IMPLEMENTS ImageRestorer
USING 'scunet-gray-15/scunet_gray_15.onnx'
AS BEGIN
  DECLARE resized Image = image_resize_to_stride(img, max_side, 64);
  DECLARE rh Int32 = image_height(resized);
  DECLARE rw Int32 = image_width(resized);
  DECLARE tensor Float32[] = image_to_tensor_chw_gray(
    resized,
    [rh, rw],
    0.0::Float32,
    1.0::Float32);
  DECLARE denoised Float32[] = infer(
    tensor,
    [1::Int32, 1::Int32, rh, rw]);
  RETURN tensor_to_image_chw_gray(denoised, rh, rw)
END
