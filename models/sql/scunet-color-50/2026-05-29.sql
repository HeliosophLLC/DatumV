-- ============================================================================
-- SCUNet Color (σ=50) — Apache-2.0.
-- ============================================================================
--
-- Catalog id:  scunet-color-50                 (models/catalog.json)
-- ONNX file:   scunet_color_50.onnx (+ .onnx.data weights sidecar)
-- License:     Apache-2.0
-- Upstream:    https://github.com/cszn/SCUNet
--              (Heliosoph.DatumV ONNX export)
--
-- Color Gaussian-noise specialist trained at σ=50 (heavy noise — extreme
-- low-light, old digital cameras). Aggressive enough to flatten heavy
-- noise; over-smooths on cleaner inputs. Use for σ≈50 matched conditions
-- or for visualizing wrong-specialist artifacts in a comparison demo.
--
-- Body shape is identical to scunet-color-real-psnr.sql — see that file
-- for the /64-stride preprocessing rationale (Swin window=8 at /8
-- bottleneck means input dims must be /64).
-- ============================================================================

CREATE OR REPLACE MODEL scunet_color_50(
  img      Image,
  max_side Int32 = 1024
    CHECK (max_side BETWEEN 64 AND 4096) STEP 64 UNIT 'pixels'
    COMMENT 'Long-side cap after aspect-preserving resize. See scunet-color-real-psnr.sql for memory-budget rationale (default ~1 GB activations, 2048 ≈ 4 GB, 4096 ≈ 12 GB).'
) RETURNS Image
IMPLEMENTS ImageRestorer
USING 'scunet-color-50/2026-05-29/scunet_color_50.onnx'
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
