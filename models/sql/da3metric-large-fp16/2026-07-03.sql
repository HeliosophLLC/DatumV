-- ============================================================================
-- Depth Anything 3 Metric Large (fp16) — metric monocular depth + sky mask,
-- Apache-2.0.
-- ============================================================================
--
-- Catalog id:  da3metric-large-fp16            (models/catalog.json)
-- ONNX file:   model_fp16.onnx
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/depth-anything/DA3METRIC-LARGE
--              (Heliosoph.DatumV ONNX export from upstream safetensors;
--              see scripts/export-da3metric.ps1)
--
-- Half-precision sibling of `da3metric_large` — same architecture, ~half
-- the disk footprint (~670 MB vs ~1.34 GB). The conversion keeps fp32 at
-- the I/O boundary (`keep_io_types`), so the bodies below are identical
-- to the fp32 variant's: same Float32 tensors in, same Float32 depth out.
-- Depth differs from the fp32 build by at most ~0.15% — below the model's
-- own per-pixel error.
--
-- See models/sql/da3metric-large/2026-07-03.sql for the full pipeline
-- rationale: canonical-depth → meters conversion (× focal_px / 300, with
-- focal_px = 252 / tan(fov/2) on the 504×504 network grid), the fixed
-- 504×504 trace resolution, and the sky-mask contract (>= 0.5 means sky).
-- ============================================================================

-- ---- Visualization variant (returns grayscale Image) ----------------------

CREATE OR REPLACE MODEL da3metric_large_fp16(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'da3metric-large-fp16/2026-07-03/model_fp16.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [504, 504],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth Float32[] = infer(
    tensor,
    [1::Int32, 3::Int32, 504::Int32, 504::Int32]);
  RETURN depth_map_to_image(depth, 504, 504, image_height(img), image_width(img), true)
END;

-- ---- Metric variant (returns raw meters as a shape-aware Float32 array) ---

CREATE OR REPLACE MODEL da3metric_large_fp16_meters(
  img     Image,
  fov_deg Float32 = 60.0::Float32
    CHECK (fov_deg BETWEEN 20.0 AND 120.0) STEP 1.0 UNIT 'degrees'
    COMMENT 'Horizontal field of view of the source camera. Sets the absolute scale: depth scales by tan(fov/2)⁻¹, so a wrong FOV multiplies all distances by a constant. 60° suits typical phone/webcam photos; check EXIF or calibration for precision work.'
) RETURNS Array<Float32>
IMPLEMENTS DepthEstimatorMetric
USING 'da3metric-large-fp16/2026-07-03/model_fp16.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [504, 504],
    imagenet_mean(),
    imagenet_std());
  DECLARE canonical Array<Float32> = infer(
    tensor,
    [1::Int32, 3::Int32, 504::Int32, 504::Int32]);
  DECLARE scale Float32 = (252.0 / tan(radians(fov_deg / 2.0)) / 300.0)::Float32;
  DECLARE metric Float32[] = array_scale(array_flatten(canonical), scale);
  RETURN array_resize_2d(
    CAST(metric AS Array<Float32>(504, 504)),
    image_height(img),
    image_width(img))
END;

-- ---- Full bundle (depth meters + sky mask) ---------------------------------

CREATE OR REPLACE MODEL da3metric_large_fp16_full(
  img     Image,
  fov_deg Float32 = 60.0::Float32
    CHECK (fov_deg BETWEEN 20.0 AND 120.0) STEP 1.0 UNIT 'degrees'
    COMMENT 'Horizontal field of view of the source camera. Sets the absolute scale: depth scales by tan(fov/2)⁻¹, so a wrong FOV multiplies all distances by a constant. 60° suits typical phone/webcam photos; check EXIF or calibration for precision work.'
) RETURNS Struct<
    depth Array<Float32>,
    sky Array<Float32>
  >
USING 'da3metric-large-fp16/2026-07-03/model_fp16.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [504, 504],
    imagenet_mean(),
    imagenet_std());
  DECLARE outputs Struct = infer_outputs(
    tensor,
    [1::Int32, 3::Int32, 504::Int32, 504::Int32]);
  DECLARE h Int32 = image_height(img);
  DECLARE w Int32 = image_width(img);
  DECLARE scale Float32 = (252.0 / tan(radians(fov_deg / 2.0)) / 300.0)::Float32;
  DECLARE metric Float32[] = array_scale(array_flatten(outputs['depth']), scale);
  RETURN {
    depth: array_resize_2d(CAST(metric AS Array<Float32>(504, 504)), h, w),
    sky:   array_resize_2d(outputs['sky'], h, w)
  }
END
