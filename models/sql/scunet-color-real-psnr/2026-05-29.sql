-- ============================================================================
-- SCUNet (Blind Real-World Color, PSNR) — Apache-2.0.
-- ============================================================================
--
-- Catalog id:  scunet-color-real-psnr          (models/catalog.json)
-- ONNX file:   scunet_color_real_psnr.onnx (+ .onnx.data weights sidecar)
-- License:     Apache-2.0
-- Upstream:    https://github.com/cszn/SCUNet
--              (Heliosoph.DatumV ONNX export)
--
-- Blind real-world color denoiser — handles unknown noise patterns
-- (sensor + JPEG + downsampling artifacts) without a noise-level prior.
-- PSNR-optimised: stays faithful to the input, no GAN sharpening. The
-- recommended default for general-purpose photo denoising.
--
-- **Input must be /64 in both dims.** The ONNX export accepts dynamic
-- H/W but SCUNet's body has 3 levels of /2 downsample plus Swin-window
-- attention with window=8 — the bottleneck feature map (at /8 of input)
-- must itself be /8, which means the **input** dims must be divisible
-- by 8 × 8 = 64. The original SCUNet README's "/8 is enough" advice
-- only covers the convolutional half; the Swin half raises the bar to
-- /64. We funnel through `image_resize_to_stride(img, 1024, 64)` so any
-- caller's image lands at /64 dims aspect-preserving.
--
-- **Memory cap (caller-tunable via `max_side`).** SCUNet's activation
-- memory scales with input_h × input_w × channels × num_layers, and at
-- native 4K input the peak activation footprint runs 8–12 GB — easy to
-- OOM a CPU run and uncomfortable on most consumer GPUs. The default
-- 1024 cap keeps the peak around ~1 GB. The `max_side` parameter
-- exposes this as a per-call knob: raise to 2048 on a 4 GB+ GPU for
-- higher-resolution restoration, drop to 512 on tight CPU budgets, or
-- crank to 4096 if you know your hardware can swallow the original
-- problem. Trade-off at the default: high-res inputs are downsampled
-- before denoising and the result is returned at the capped resolution
-- (callers wanting source-resolution output can chain with realesrgan
-- or another upscaler). A tile-based variant (overlapping 512×512
-- windows with blended seams — the canonical fix in Real-ESRGAN /
-- SwinIR / SCUNet reference implementations) would let high-res inputs
-- pass through losslessly; it's the right follow-up and would add a
-- new `image_tile_iterate` primitive.
--
-- Output is at the resized resolution; users wanting alignment with
-- the source image can pair with `array_resize_2d` or a future
-- image_resize primitive.
--
-- Pipeline (mirrors realesrgan-x4v3 shape with the stride pre-step):
--   1. image_resize_to_stride — aspect-preserving, max side 1024, both
--                                dims rounded to /64.
--   2. image_to_tensor_chw    — pack as NCHW Float32 RGB at (rh, rw),
--                                raw pixel/255 in [0, 1].
--   3. infer                   — single ONNX dispatch on [1, 3, rh, rw];
--                                output is [1, 3, rh, rw] in [0, 1] RGB
--                                (1× denoise — same dims as input).
--   4. tensor_to_image_chw     — pack back into RGB at (rh, rw).
-- ============================================================================

CREATE OR REPLACE MODEL scunet_color_real_psnr(
  img      Image,
  max_side Int32 = 1024
    CHECK (max_side BETWEEN 64 AND 4096) STEP 64 UNIT 'pixels'
    COMMENT 'Long-side cap after aspect-preserving resize. Default 1024 keeps activation memory under ~1 GB; raise on GPUs with headroom (2048 ≈ 4 GB, 4096 ≈ 12 GB) for higher source resolution, lower for fastest CPU dispatch. Rounded to /64 (the architectural stride) regardless of the supplied value.'
) RETURNS Image
IMPLEMENTS ImageRestorer
USING 'scunet-color-real-psnr/2026-05-29/scunet_color_real_psnr.onnx'
AS BEGIN
  DECLARE resized Image = image_resize_to_stride(img, max_side, 64);
  DECLARE rh Int32 = image_height(resized);
  DECLARE rw Int32 = image_width(resized);
  DECLARE tensor Float32[] = image_to_tensor_chw(
    resized,
    [rh, rw],
    [CAST(0.0 AS Float32), CAST(0.0 AS Float32), CAST(0.0 AS Float32)],
    [CAST(1.0 AS Float32), CAST(1.0 AS Float32), CAST(1.0 AS Float32)]);
  DECLARE denoised Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), rh, rw]);
  RETURN tensor_to_image_chw(denoised, rh, rw)
END
