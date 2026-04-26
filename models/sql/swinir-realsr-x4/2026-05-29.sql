-- ============================================================================
-- SwinIR Real-World SR (4×) — Apache-2.0.
-- ============================================================================
--
-- Catalog id:  swinir-realsr-x4               (models/catalog.json)
-- ONNX file:   swinir_realsr_x4.onnx
-- License:     Apache-2.0
-- Upstream:    https://github.com/JingyunLiang/SwinIR
--              (Heliosoph ONNX export — SwinIR-L config, 240-dim embeds)
--
-- Swin Transformer for Image Restoration, real-world SR variant. Upscales
-- by 4× while cleaning compression artifacts, sensor noise, and mild
-- blur. Heavier than Real-ESRGAN — pick when transformer-quality
-- restoration matters more than CPU throughput.
--
-- **Pinned input resolution.** This ONNX export hard-codes the input
-- shape to 64×64; output is 256×256 (4× upscale). We stretch-resize the
-- caller's image to 64×64 before dispatch, which yields a strongly
-- aliased result for any input that wasn't already 64×64. A tile-based
-- variant (slide a 64×64 window with overlap, blend overlaps) would be
-- the right follow-up for high-resolution inputs; not implemented as a
-- SQL primitive yet.
--
-- Pipeline:
--   1. image_to_tensor_chw  — stretch-resize to 64×64, RGB, raw pixel/255
--                              in [0, 1] (no per-channel normalisation).
--   2. infer                 — single ONNX dispatch; output is
--                              [1, 3, 256, 256] in [0, 1] RGB.
--   3. tensor_to_image_chw   — pack into a 256×256 RGB image.
-- ============================================================================

CREATE OR REPLACE MODEL swinir_realsr_x4(img Image) RETURNS Image
IMPLEMENTS ImageUpscaler
USING 'swinir-realsr-x4/swinir_realsr_x4.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [CAST(64 AS Int32), CAST(64 AS Int32)],
    [CAST(0.0 AS Float32), CAST(0.0 AS Float32), CAST(0.0 AS Float32)],
    [CAST(1.0 AS Float32), CAST(1.0 AS Float32), CAST(1.0 AS Float32)]);
  DECLARE upscaled Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(64 AS Int32), CAST(64 AS Int32)]);
  RETURN tensor_to_image_chw(upscaled, 256, 256)
END
