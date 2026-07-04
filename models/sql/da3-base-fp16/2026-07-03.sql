-- ============================================================================
-- Depth Anything 3 Base (fp16) — monocular depth + camera intrinsics,
-- Apache-2.0.
-- ============================================================================
--
-- Catalog id:  da3-base-fp16                   (models/catalog.json)
-- ONNX file:   model_fp16.onnx
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/depth-anything/DA3-BASE
--              (Heliosoph.DatumV ONNX export from upstream safetensors;
--              see scripts/export-da3metric.ps1)
--
-- Half-precision sibling of `da3_base` — same architecture, ~half the
-- disk (~198 MB vs ~394 MB). fp32 kept at the I/O boundary
-- (`keep_io_types`), so the bodies are identical to the fp32 variant's.
--
-- See models/sql/da3-base/2026-07-03.sql for the full rationale:
-- up-to-scale depth (NOT meters), no single-view pose, K at the 504 grid
-- rescaled to source coordinates in the `_full` body.
-- ============================================================================

-- ---- Visualization variant (returns grayscale Image) ----------------------

CREATE OR REPLACE MODEL da3_base_fp16(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'da3-base-fp16/2026-07-03/model_fp16.onnx'
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

-- ---- Full bundle (depth + confidence + intrinsics) -------------------------

CREATE OR REPLACE MODEL da3_base_fp16_full(img Image)
  RETURNS Struct<
    depth Array<Float32>,
    confidence Array<Float32>,
    intrinsics Array<Float32>(3, 3)
  >
USING 'da3-base-fp16/2026-07-03/model_fp16.onnx'
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
  DECLARE sx Float32 = w::Float32 / 504.0::Float32;
  DECLARE sy Float32 = h::Float32 / 504.0::Float32;
  DECLARE k Float32[] = array_flatten(outputs['intrinsics']);
  RETURN {
    depth:      array_resize_2d(outputs['depth'], h, w),
    confidence: array_resize_2d(outputs['depth_conf'], h, w),
    intrinsics: CAST([
      array_get(k, 1) * sx,  0.0::Float32,          array_get(k, 3) * sx,
      0.0::Float32,          array_get(k, 5) * sy,  array_get(k, 6) * sy,
      0.0::Float32,          0.0::Float32,          1.0::Float32
    ] AS Array<Float32>(3, 3))
  }
END
