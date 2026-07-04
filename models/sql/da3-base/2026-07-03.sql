-- ============================================================================
-- Depth Anything 3 Base — monocular depth + camera intrinsics, Apache-2.0.
-- ============================================================================
--
-- Catalog id:  da3-base                        (models/catalog.json)
-- ONNX file:   model.onnx
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/depth-anything/DA3-BASE
--              (Heliosoph.DatumV ONNX export from upstream safetensors;
--              see scripts/export-da3metric.ps1)
--
-- The Apache-licensed any-view model of the Depth Anything 3 family
-- (DINOv2 ViT-B, dual-DPT head, camera heads), exported here in its
-- single-view cut: one image in, per-pixel depth + confidence + a
-- per-image camera-intrinsics estimate out.
--
-- **Depth is up-to-scale, not meters.** The any-view models share one
-- unknown global scale between depth and pose; only the metric variants
-- (`da3metric_large*`) produce meters. Use this model for visualization,
-- confidence-gated masking, and its K estimate; use `da3metric_large_meters`
-- when absolute scale matters — or divide the two depths to recover the
-- scale factor for pose work (see da3-base-4view's install SQL).
--
-- **No pose from a single view.** DA3 predicts camera pose relative to
-- the other views in the same forward pass; with one view the extrinsics
-- output is near-identity by construction, so the bodies below don't
-- surface it. Pose recovery lives in the 4-view sibling
-- (`da3_base_4view`).
--
-- Output bag (4 outputs — first declared is the depth map):
--   depth       Float32  [batch, 1, 504, 504]   up-to-scale, bigger=farther
--   depth_conf  Float32  [batch, 1, 504, 504]   per-pixel confidence
--   extrinsics  Float32  [batch, 1, 3, 4]       ~identity (single view)
--   intrinsics  Float32  [batch, 1, 3, 3]       K at the 504×504 input grid
--
-- Fixed 504×504 input (ViT pos-embed constraint, same as the other DA3
-- exports); bodies stretch-resize in and resize outputs back out.
-- ============================================================================

-- ---- Visualization variant (returns grayscale Image) ----------------------

CREATE OR REPLACE MODEL da3_base(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'da3-base/2026-07-03/model.onnx'
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
--
-- Single forward pass, aligned to the source image:
--   • depth      — up-to-scale (NOT meters — see the header), bilinear-
--                  resized to (image_height, image_width).
--   • confidence — resized to the same grid so thresholding composes
--                  pixel-for-pixel with depth and the source image.
--   • intrinsics — predicted camera matrix K rescaled from the 504×504
--                  input grid to source coordinates (K' = diag(sx, sy, 1)·K),
--                  ready for point_cloud_from_depth_pinhole_intrinsics.
--                  A per-image focal estimate: array_get(k, 1, 1) after
--                  this rescale is fx in source pixels.
--
-- extrinsics is deliberately not surfaced — near-identity for a single
-- view (see the header). No IMPLEMENTS: there's no task contract for a
-- depth+confidence+K bundle.

CREATE OR REPLACE MODEL da3_base_full(img Image)
  RETURNS Struct<
    depth Array<Float32>,
    confidence Array<Float32>,
    intrinsics Array<Float32>(3, 3)
  >
USING 'da3-base/2026-07-03/model.onnx'
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
  -- intrinsics extracts as a shape-aware (1,1,3,3); array_flatten gives the
  -- row-major buffer [fx, 0, cx, 0, fy, cy, 0, 0, 1] for 1-based array_get,
  -- so flat indices 1=fx, 3=cx, 5=fy, 6=cy.
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
