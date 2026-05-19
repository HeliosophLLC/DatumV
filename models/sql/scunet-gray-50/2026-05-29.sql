-- ============================================================================
-- SCUNet Grayscale (σ=50) — Apache-2.0.
-- ============================================================================
--
-- Catalog id:  scunet-gray-50                  (models/catalog.json)
-- ONNX file:   scunet_gray_50.onnx (+ .onnx.data weights sidecar)
-- License:     Apache-2.0
-- Upstream:    https://github.com/cszn/SCUNet
--              (Heliosoph.DatumV ONNX export)
--
-- Single-channel Gaussian denoising at σ=50 (heavy noise). Use for
-- heavily-noisy grayscale workflows (low-light astrophotography,
-- degraded scans) at matched σ≈50 conditions. Body shape identical to
-- scunet-gray-15; see scunet-gray-15.sql for the grayscale-pipeline
-- rationale and scunet-color-real-psnr.sql for the /64-stride
-- preprocessing notes.

CREATE OR REPLACE MODEL scunet_gray_50(
  img      Image,
  max_side Int32 = 1024
    CHECK (max_side BETWEEN 64 AND 4096) STEP 64 UNIT 'pixels'
    COMMENT 'Long-side cap after aspect-preserving resize. See scunet-color-real-psnr.sql for memory-budget rationale (default ~1 GB activations, 2048 ≈ 4 GB, 4096 ≈ 12 GB).'
) RETURNS Image
IMPLEMENTS ImageRestorer
USING 'scunet-gray-50/2026-05-29/scunet_gray_50.onnx'
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
