-- ============================================================================
-- Metric3D V2 Large — metric depth + surface normals, BSD-2-Clause.
-- ============================================================================
--
-- Catalog id:  metric3d-v2-large               (models/catalog.json)
-- ONNX file:   onnx/model.onnx
-- License:     BSD-2-Clause
-- Upstream:    https://github.com/YvanYin/Metric3D
--              (onnx-community ONNX export of Metric3D V2 ViT-Large)
--
-- ViT-L backbone (~300M params, ~1.6 GB on disk). Recommended quality
-- tier — best accuracy in the family without the Giant's VRAM cost.
-- Strong on outdoor scenes where DAv3 / ZoeDepth's training distribution
-- falls off (parking lots, landscapes, mixed distances).
--
-- Body shape is identical to the Small sibling (metric3d-v2-small.sql)
-- — only the USING path and SQL-visible names differ. See that file for
-- the full Metric3D V2 pipeline rationale (canonical-camera scaling
-- caveat included).
-- ============================================================================

CREATE OR REPLACE MODEL metric3d_v2_large(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'metric3d-v2-large/2026-05-29/onnx/model.onnx'
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

CREATE OR REPLACE MODEL metric3d_v2_large_meters(img Image)
  RETURNS Array<Float32>
IMPLEMENTS DepthEstimatorMetric
USING 'metric3d-v2-large/2026-05-29/onnx/model.onnx'
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

CREATE OR REPLACE MODEL metric3d_v2_large_full(img Image)
  RETURNS Struct
USING 'metric3d-v2-large/2026-05-29/onnx/model.onnx'
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
