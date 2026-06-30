-- ============================================================================
-- Depth Anything V3 Large — monocular depth + camera-pose estimation,
-- Apache-2.0.
-- ============================================================================
--
-- Catalog id:  depth-anything-v3-large         (models/catalog.json)
-- ONNX files:  onnx/model.onnx + onnx/model.onnx_data
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/depth-anything/Depth-Anything-V3-Large
--
-- Successor to Depth-Anything-V2-Large. DINOv2 ViT-L encoder + DPT-style
-- decoder retrained on a substantially larger metric-depth corpus, plus
-- additional heads that emit per-image camera intrinsics + extrinsics +
-- a per-pixel confidence map. Same 518×518 spatial input convention as
-- V2, same ImageNet mean/std normalisation, but the ONNX export uses a
-- **rank-5 input shape** `[batch, views, channels, H, W]` to support
-- multi-view inference; single-image dispatch passes `views=1`.
--
-- Output bag (5 outputs — first declared is the depth map, the rest
-- expose the pose / confidence heads):
--   predicted_depth   Float32  [batch, views, H, W]   metric meters, bigger=farther
--   confidence        Float32  [batch, views, H, W]   per-pixel reliability
--   extrinsics        Float32  [batch, views, 3, 4]   camera pose (R | t)
--   intrinsics        Float32  [batch, views, 3, 3]   camera matrix (fx, fy, cx, cy)
--
-- The visualization + metric bodies below consume `predicted_depth`
-- (first output via `infer()`); a future companion that surfaces
-- intrinsics for FOV-aware point-cloud reconstruction would route
-- through `infer_outputs(...)`.
--
-- **Local coherence vs ZoeDepth / GLPN-NYU.** DAv3 was trained on orders
-- of magnitude more data than NYU+KITTI, addressing the patchy
-- face-region / mixed-lighting failure modes ZoeDepth's dual-head router
-- exhibits on out-of-distribution photographs. Recommended default when
-- absolute scale matters AND the scene isn't strictly indoor or
-- driving-style outdoor (where the specialist ZoeDepth heads still win).
--
-- Pipeline (matches midas / dpt / depth-anything-v2 shape):
--   1. image_to_tensor_chw   — stretch-resize to 518×518, RGB,
--                              ImageNet mean/std.
--   2. infer                 — single ONNX dispatch; primary output is
--                              a single-channel Float32 metric-depth map
--                              (units: meters; bigger value = farther).
--                              The pose-estimation head's intrinsics
--                              output is reachable via `infer_outputs`
--                              for callers that need it (see the
--                              follow-up note below).
--   3. depth_map_to_image    — min-max normalize + grayscale-as-RGBA
--                              pack + bilinear resize to source dims.
--                              `invert => true` keeps "near = bright"
--                              consistent with the rest of the depth
--                              zoo (DAv3 emits real meters where bigger
--                              = farther, same as ZoeDepth / GLPN).
--
-- Pose / intrinsics follow-up: the visualization + metric bodies below
-- consume only the depth head. A future `depth_anything_v3_large_pose`
-- model could surface the intrinsics output (camera focal length / FOV
-- estimate) for downstream point-cloud reconstruction without a
-- hard-coded `fov_deg`. Wire via `infer_outputs(tensor, ...)` once the
-- exact ONNX output name is confirmed.
-- ============================================================================

-- ---- Visualization variant (returns grayscale Image) ----------------------

CREATE OR REPLACE MODEL depth_anything_v3_large(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'depth-anything-v3-large/2026-05-29/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth Float32[] = infer(
    tensor,
    [1::Int32, 1::Int32, 3::Int32, 518::Int32, 518::Int32]);
  RETURN depth_map_to_image(depth, 518, 518, image_height(img), image_width(img), true)
END;

-- ---- Metric variant (returns raw meters as a shape-aware Float32 array) ---
--
-- Same body shape as zoedepth_nyu_kitti_meters / glpn_nyu_meters — see
-- those for the broader rationale. Native 518×518 depth is bilinear-
-- resized back to source dimensions via `array_resize_2d` so the
-- returned array aligns per-pixel with the input image, ready for
-- `point_cloud_from_depth_pinhole(img, depth, fov_deg)` directly.

CREATE OR REPLACE MODEL depth_anything_v3_large_meters(img Image)
  RETURNS Array<Float32>
IMPLEMENTS DepthEstimatorMetric
USING 'depth-anything-v3-large/2026-05-29/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth_native Array<Float32> = infer(
    tensor,
    [1::Int32, 1::Int32, 3::Int32, 518::Int32, 518::Int32]);
  RETURN array_resize_2d(depth_native, image_height(img), image_width(img))
END;

-- ---- Full bundle (every output: depth + confidence + pose matrices) -------
--
-- Single forward pass surfacing every head DAv3 emits, aligned to the
-- source image so downstream code can consume the bundle without
-- per-field rescaling:
--   • depth      — bilinear-resized from native 518×518 back to
--                  (image_height, image_width), units = metric meters.
--   • confidence — bilinear-resized to (image_height, image_width) so
--                  thresholding and masking compose pixel-for-pixel with
--                  `depth` and the source image.
--   • intrinsics — predicted camera matrix K = [[fx, 0, cx],
--                                                [0, fy, cy],
--                                                [0,  0,  1]]
--                  rescaled from the 518×518 input grid to image
--                  coordinates (sx = W/518, sy = H/518; K' =
--                  diag(sx, sy, 1) · K). Plugs straight into
--                  `point_cloud_from_depth_pinhole_intrinsics` /
--                  `point_cloud_from_depth_orthographic_intrinsics`
--                  without further math.
--   • extrinsics — camera pose [R | t]. Pass-through; world coordinates
--                  don't depend on image resolution. Identity /
--                  decorative for single-view input; meaningful once a
--                  multi-view body lands.
--
-- For raw 518×518 outputs (e.g. chaining at native resolution, or
-- carrying the unscaled K through to a separate pipeline) see
-- `depth_anything_v3_large_full_native` below.
--
-- No `IMPLEMENTS` clause — there's no task contract for
-- "depth + pose + confidence bundle" yet. If a second bundled-output
-- model lands, register a `DepthEstimatorWithPose` contract returning
-- `Struct` and add the IMPLEMENTS here.
--
-- The declared return type uses the inline `Struct<name Type, …>` form
-- (see docs/sql/create-model.md#struct-return-types). The annotation is
-- design-time metadata only — the LanguageServer reads it to surface
-- hover / completion on field access (`r.intrinsics` resolves to its
-- declared `Array<Float32>(3, 3)` shape), while the runtime treats
-- the value as the opaque `DataKind.Struct`. The body's
-- `RETURN { depth: …, intrinsics: … }` literal still defines the actual
-- per-row struct shape; the engine interns a per-query TypeId from that
-- literal. A future named-struct registration (`DepthPoseBundle`) would
-- also make the contract visible in `system.tasks` and the cross-query
-- type registry — small one-time C# addition.

CREATE OR REPLACE MODEL depth_anything_v3_large_full(img Image)
  RETURNS Struct<
    depth Array<Float32>,
    confidence Array<Float32>,
    extrinsics Array<Float32>(1, 1, 3, 4),
    intrinsics Array<Float32>(3, 3)
  >
USING 'depth-anything-v3-large/2026-05-29/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE outputs Struct = infer_outputs(
    tensor,
    [1::Int32, 1::Int32, 3::Int32, 518::Int32, 518::Int32]);
  DECLARE h Int32 = image_height(img);
  DECLARE w Int32 = image_width(img);
  DECLARE sx Float32 = w::Float32 / 518.0::Float32;
  DECLARE sy Float32 = h::Float32 / 518.0::Float32;
  -- intrinsics extracts as a shape-aware (1,1,3,3); array_flatten gives the
  -- row-major buffer [fx, 0, cx, 0, fy, cy, 0, 0, 1] for 1-based array_get,
  -- so flat indices 1=fx, 3=cx, 5=fy, 6=cy.
  DECLARE k Float32[] = array_flatten(outputs['intrinsics']);
  RETURN {
    depth:      array_resize_2d(outputs['predicted_depth'], h, w),
    confidence: array_resize_2d(outputs['confidence'], h, w),
    extrinsics: outputs['extrinsics'],
    intrinsics: CAST([
      array_get(k, 1) * sx,  0.0::Float32,          array_get(k, 3) * sx,
      0.0::Float32,          array_get(k, 5) * sy,  array_get(k, 6) * sy,
      0.0::Float32,          0.0::Float32,          1.0::Float32
    ] AS Array<Float32>(3, 3))
  }
END;

-- ---- Full bundle, native 518x518 resolution -------------------------------
--
-- Escape-hatch sibling of `depth_anything_v3_large_full` that returns
-- every head at the model's native input grid, without bilinear-resizing
-- depth/confidence or rescaling the predicted intrinsics matrix. Use when
-- chaining at 518×518 (avoids the resize cost), debugging the raw model
-- output, or feeding the unscaled K through to a separate pipeline that
-- handles its own resolution mapping.

CREATE OR REPLACE MODEL depth_anything_v3_large_full_native(img Image)
  RETURNS Struct<
    depth Array<Float32>(518, 518),
    confidence Array<Float32>(518, 518),
    extrinsics Array<Float32>(1, 1, 3, 4),
    intrinsics Array<Float32>(1, 1, 3, 3)
  >
USING 'depth-anything-v3-large/2026-05-29/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE outputs Struct = infer_outputs(
    tensor,
    [1::Int32, 1::Int32, 3::Int32, 518::Int32, 518::Int32]);
  RETURN {
    depth:      outputs['predicted_depth'],
    confidence: outputs['confidence'],
    extrinsics: outputs['extrinsics'],
    intrinsics: outputs['intrinsics']
  }
END
