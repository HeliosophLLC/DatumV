-- ============================================================================
-- Metric3D V2 Small (fp16) — metric depth + surface normals, BSD-2-Clause.
-- ============================================================================
--
-- Catalog id:  metric3d-v2-small-fp16          (models/catalog.json)
-- ONNX file:   onnx/model_fp16.onnx
-- License:     BSD-2-Clause
-- Upstream:    https://github.com/YvanYin/Metric3D
--              (onnx-community fp16 ONNX export of Metric3D V2 ViT-Small)
--
-- Half-precision sibling of `metric3d_v2_small`. ~half the disk and
-- model-load memory, same outputs (metric depth + surface normals +
-- normal confidence). On GPUs / NPUs with native fp16 expect a modest
-- throughput win; on CPU runtimes that upcast fp16→fp32 internally,
-- latency matches fp32 with the model-load memory cost roughly halved.
--
-- Body shape is identical to the fp32 sibling (metric3d-v2-small.sql) —
-- only the USING path differs. See that file for the full Metric3D V2
-- pipeline rationale (canonical-camera scaling caveat included).
-- ============================================================================

CREATE OR REPLACE MODEL metric3d_v2_small_fp16(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'metric3d-v2-small-fp16/2026-05-29/onnx/model_fp16.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth Float32[] = infer(
    tensor,
    [1::Int32, 3::Int32, 518::Int32, 518::Int32]);
  DECLARE ndim Int32 = array_ndims(depth);
  DECLARE depth_h Int32 = array_length(depth, ndim - 1);
  DECLARE depth_w Int32 = array_length(depth, ndim);
  RETURN depth_map_to_image(depth, depth_h, depth_w, image_height(img), image_width(img), true)
END;

CREATE OR REPLACE MODEL metric3d_v2_small_fp16_meters(img Image)
  RETURNS Array<Float32>
IMPLEMENTS DepthEstimatorMetric
USING 'metric3d-v2-small-fp16/2026-05-29/onnx/model_fp16.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth_native Array<Float32> = infer(
    tensor,
    [1::Int32, 3::Int32, 518::Int32, 518::Int32]);
  RETURN array_resize_2d(depth_native, image_height(img), image_width(img))
END;

CREATE OR REPLACE MODEL metric3d_v2_small_fp16_full(img Image)
  RETURNS Struct
USING 'metric3d-v2-small-fp16/2026-05-29/onnx/model_fp16.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE outputs Struct = infer_outputs(
    tensor,
    [1::Int32, 3::Int32, 518::Int32, 518::Int32]);
  RETURN {
    depth:             outputs['predicted_depth'],
    normals:           outputs['predicted_normal'],
    normal_confidence: outputs['normal_confidence']
  }
END
