-- ============================================================================
-- SwinIR Color Denoising (σ=25) — Apache-2.0.
-- ============================================================================
--
-- Catalog id:  swinir-denoising-color-25       (models/catalog.json)
-- ONNX file:   swinir_denoising_color_25.onnx
-- License:     Apache-2.0
-- Upstream:    https://github.com/JingyunLiang/SwinIR
--              (Heliosoph ONNX export — SwinIR-M config, 180-dim embeds)
--
-- SwinIR color denoiser specialised on Gaussian noise σ=25 — the
-- standard denoising-benchmark reference level. Use for σ≈25 matched
-- conditions or for benchmark reproduction. For blind real-world noise
-- pick scunet-color-real-{psnr,gan} instead.
--
-- **Pinned input resolution.** This export hard-codes 128×128; output
-- is 128×128 (1× denoise). Caller's image is stretch-resized to 128×128
-- on the way in, which heavily aliases inputs of any other shape. A
-- tiled variant (overlapping 128×128 windows with blending) is the
-- right follow-up for full-frame inputs; not implemented yet.
--
-- Pipeline:
--   1. image_to_tensor_chw  — stretch-resize to 128×128, RGB, raw
--                              pixel/255 in [0, 1].
--   2. infer                 — single ONNX dispatch; output is
--                              [1, 3, 128, 128] in [0, 1] RGB.
--   3. tensor_to_image_chw   — pack into a 128×128 RGB image.
-- ============================================================================

CREATE OR REPLACE MODEL swinir_denoising_color_25(img Image) RETURNS Image
IMPLEMENTS ImageRestorer
USING 'swinir-denoising-color-25/2026-05-29/swinir_denoising_color_25.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [CAST(128 AS Int32), CAST(128 AS Int32)],
    [CAST(0.0 AS Float32), CAST(0.0 AS Float32), CAST(0.0 AS Float32)],
    [CAST(1.0 AS Float32), CAST(1.0 AS Float32), CAST(1.0 AS Float32)]);
  DECLARE denoised Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(128 AS Int32), CAST(128 AS Int32)]);
  RETURN tensor_to_image_chw(denoised, 128, 128)
END
