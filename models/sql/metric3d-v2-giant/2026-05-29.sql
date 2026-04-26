-- ============================================================================
-- Metric3D V2 Giant — metric depth + surface normals, BSD-2-Clause.
-- ============================================================================
--
-- Catalog id:  metric3d-v2-giant               (models/catalog.json)
-- ONNX files:  onnx/model.onnx + onnx/model.onnx_data
-- License:     BSD-2-Clause
-- Upstream:    https://github.com/YvanYin/Metric3D
--              (onnx-community ONNX export of Metric3D V2 ViT-Giant)
--
-- ViT-G backbone (~1B params, ~5.5 GB on disk). Best absolute accuracy
-- in the family, especially on long-range outdoor and edge-case
-- geometry. Needs ~6 GB VRAM for usable inference; CPU technically
-- possible but slow. Production-batch tier.
--
-- Body shape is identical to metric3d-v2-small.sql.
-- ============================================================================

CREATE OR REPLACE MODEL metric3d_v2_giant(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'metric3d-v2-giant/onnx/model.onnx'
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

CREATE OR REPLACE MODEL metric3d_v2_giant_meters(img Image)
  RETURNS Array<Float32>
IMPLEMENTS DepthEstimatorMetric
USING 'metric3d-v2-giant/onnx/model.onnx'
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

CREATE OR REPLACE MODEL metric3d_v2_giant_full(img Image)
  RETURNS Struct
USING 'metric3d-v2-giant/onnx/model.onnx'
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
