-- ============================================================================
-- Depth Anything V2 Base — monocular depth estimation, Apache-2.0.
-- ============================================================================
--
-- Catalog id:  depth-anything-v2-base         (models/catalog.json)
-- ONNX file:   onnx/model.onnx
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/depth-anything/Depth-Anything-V2-Base
--              (onnx-community ONNX export)
--
-- DINOv2 ViT-B encoder + DPT-style depth decoder. ~97M params. Mid-size
-- step up from the Small variant — closer to V2-Large quality without the
-- CC-BY-NC license problem (V2-Large is non-commercial; V2-Base is
-- Apache-2.0). Pick this over the Small variant when accuracy on fine
-- structural detail matters more than CPU throughput. **Relative** depth
-- (not metric) in arbitrary units.
--
-- Body is identical to depth-anything-v2-small.sql — only the catalog
-- folder differs. Both variants share the V2 preprocessing convention
-- (518×518, ImageNet normalize).
-- ============================================================================

CREATE OR REPLACE MODEL depth_anything_v2_base(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'depth-anything-v2-base/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth Float32[] = infer(
    tensor,
    [1::Int32, 3::Int32, 518::Int32, 518::Int32]);
  RETURN depth_map_to_image(depth, 518, 518, image_height(img), image_width(img))
END
