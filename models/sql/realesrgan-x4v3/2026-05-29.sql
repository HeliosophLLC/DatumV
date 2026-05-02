-- ============================================================================
-- Real-ESRGAN x4v3 — 4× image super-resolution, BSD-3-Clause.
-- ============================================================================
--
-- Catalog id:  realesrgan-x4v3        (models/catalog.json)
-- ONNX file:   realesr-general-x4v3.onnx
-- License:     BSD-3-Clause
-- Upstream:    https://github.com/xinntao/Real-ESRGAN
--
-- Xintao Wang's Real-ESRGAN-Compact (SRVGGNet) general-content variant.
-- ~1.2M params, tiny (~5 MB). General-purpose 4× upscaler trained on real
-- photographic degradations (not anime). The lightweight Compact backbone
-- keeps it fast enough to run per-row in a SQL pipeline for typical photos.
--
-- Memory note: whole-image inference scales with output resolution. A
-- 1024×1024 input at 4× costs ~210 MB of intermediate floats. Tile-based
-- inference (with overlap) would be the right follow-up for high-res
-- inputs; not implemented as a SQL primitive yet.
--
-- Pipeline:
--   1. image_to_tensor_chw  — pack as NCHW Float32 RGB at native resolution
--                              (no resize: target_size = original H×W).
--                              Real-ESRGAN-Compact has no per-channel
--                              normalisation; raw pixel/255 in [0, 1] is
--                              correct, so mean=[0,0,0] / std=[1,1,1].
--   2. infer                 — single ONNX dispatch. Dynamic input shape
--                              [1, 3, H, W]; output is [1, 3, H*4, W*4]
--                              in [0, 1] RGB.
--   3. tensor_to_image_chw   — pack the upscaled tensor back into a PNG-
--                              encoded RGB image at the upscaled dims.
--                              3-arg form skips inverse normalize (the
--                              network's output is already in [0, 1]).
--
-- Note: the C# version exposed a per-call `outscale` Float64 in [1.0, 4.0]
-- that downsampled the 4× output via SkiaSharp; that feature was dropped
-- in the SQL migration (the engine doesn't have a generic image_resize
-- primitive yet). Users wanting <4× output can compose with their own
-- postprocess once such a primitive exists.
-- ============================================================================

CREATE OR REPLACE MODEL realesrgan_x4v3(img Image) RETURNS Image
IMPLEMENTS ImageUpscaler
USING 'realesrgan-x4v3/2026-05-29/realesr-general-x4v3.onnx'
AS BEGIN
  DECLARE iw Int32 = image_width(img);
  DECLARE ih Int32 = image_height(img);
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [ih, iw],
    [0.0::Float32, 0.0::Float32, 0.0::Float32],
    [1.0::Float32, 1.0::Float32, 1.0::Float32]);
  DECLARE upscaled Float32[] = infer(
    tensor,
    [1::Int32, 3::Int32, ih, iw]);
  RETURN tensor_to_image_chw(upscaled, ih * 4, iw * 4)
END
