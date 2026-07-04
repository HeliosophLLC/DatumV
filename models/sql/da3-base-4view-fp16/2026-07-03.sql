-- ============================================================================
-- Depth Anything 3 Base, 4-view (fp16) — camera-pose recovery, Apache-2.0.
-- ============================================================================
--
-- Catalog id:  da3-base-4view-fp16             (models/catalog.json)
-- ONNX file:   model_fp16.onnx
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/depth-anything/DA3-BASE
--              (Heliosoph.DatumV ONNX export from upstream safetensors,
--              traced with -Views 4; see scripts/export-da3metric.ps1)
--
-- Half-precision sibling of `da3_base_4view` — same architecture, ~half
-- the disk (~198 MB vs ~394 MB). fp32 kept at the I/O boundary
-- (`keep_io_types`), so the body is identical to the fp32 variant's.
--
-- See models/sql/da3-base-4view/2026-07-03.sql for the full rationale:
-- pinned 4-view window, relative poses, the unknown-global-scale caveat
-- and the median-ratio recipe for anchoring to `da3metric_large_meters`.
-- ============================================================================

CREATE OR REPLACE MODEL da3_base_4view_fp16(
  img1 Image,
  img2 Image,
  img3 Image,
  img4 Image
) RETURNS Struct<
    depth Array<Float32>(1, 4, 504, 504),
    confidence Array<Float32>(1, 4, 504, 504),
    extrinsics Array<Float32>(1, 4, 3, 4),
    intrinsics Array<Float32>(1, 4, 3, 3)
  >
USING 'da3-base-4view-fp16/2026-07-03/model_fp16.onnx'
AS BEGIN
  DECLARE t1 Float32[] = image_to_tensor_chw(
    img1, [504, 504], imagenet_mean(), imagenet_std());
  DECLARE t2 Float32[] = image_to_tensor_chw(
    img2, [504, 504], imagenet_mean(), imagenet_std());
  DECLARE t3 Float32[] = image_to_tensor_chw(
    img3, [504, 504], imagenet_mean(), imagenet_std());
  DECLARE t4 Float32[] = image_to_tensor_chw(
    img4, [504, 504], imagenet_mean(), imagenet_std());
  DECLARE stacked Float32[] = array_concat(
    array_concat(t1, t2),
    array_concat(t3, t4));
  DECLARE outputs Struct = infer_outputs(
    stacked,
    [1::Int32, 4::Int32, 3::Int32, 504::Int32, 504::Int32]);
  RETURN {
    depth:      outputs['depth'],
    confidence: outputs['depth_conf'],
    extrinsics: outputs['extrinsics'],
    intrinsics: outputs['intrinsics']
  }
END
