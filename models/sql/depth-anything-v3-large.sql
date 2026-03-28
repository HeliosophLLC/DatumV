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
USING 'depth-anything-v3-large/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(1 AS Int32), CAST(3 AS Int32), CAST(518 AS Int32), CAST(518 AS Int32)]);
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
USING 'depth-anything-v3-large/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth_native Array<Float32> = infer(
    tensor,
    [CAST(1 AS Int32), CAST(1 AS Int32), CAST(3 AS Int32), CAST(518 AS Int32), CAST(518 AS Int32)]);
  RETURN array_resize_2d(depth_native, image_height(img), image_width(img))
END;

-- ---- Full bundle (every output: depth + confidence + pose matrices) -------
--
-- Single forward pass surfacing every head DAv3 emits. Useful when
-- downstream wants more than just depth:
--   • confidence — filter low-reliability points before PointCloud
--   • intrinsics — predicted camera matrix K = [[fx, 0, cx],
--                                                [0, fy, cy],
--                                                [0,  0,  1]]
--                  Lets `point_cloud_from_depth_pinhole` use the model's
--                  actual focal length instead of a hardcoded fov_deg;
--                  more accurate cloud geometry.
--   • extrinsics — camera pose [R | t]. Identity / decorative for single-
--                  view input; meaningful once a multi-view body lands.
--
-- The depth field is returned at NATIVE 518×518 resolution to avoid the
-- bilinear-resize cost when callers don't actually need a full-res
-- depth (e.g. just want the predicted intrinsics for a separate
-- visualization pipeline). Caller resizes via `array_resize_2d` if
-- per-pixel alignment with the source image is required.
--
-- No `IMPLEMENTS` clause — there's no task contract for
-- "depth + pose + confidence bundle" yet. If a second bundled-output
-- model lands, register a `DepthEstimatorWithPose` contract returning
-- `Struct` and add the IMPLEMENTS here.
--
-- The declared return type is the bare `Struct` kind — the parser
-- doesn't accept inline `Struct<a: T, b: T>` field-list types in
-- RETURNS today (only named structs from NamedTypeRegistry). The body's
-- `RETURN { depth, confidence, extrinsics, intrinsics }` literal
-- defines the struct shape at runtime; the engine interns a TypeId for
-- it and downstream `r.depth` / `r['depth']` field access resolves
-- against that runtime descriptor. A future named-struct registration
-- (`DepthPoseBundle`) would make the contract visible in `system.tasks`
-- + the type registry — small one-time C# addition.

CREATE OR REPLACE MODEL depth_anything_v3_large_full(img Image)
  RETURNS Struct
USING 'depth-anything-v3-large/onnx/model.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [518, 518],
    imagenet_mean(),
    imagenet_std());
  DECLARE outputs Struct = infer_outputs(
    tensor,
    [CAST(1 AS Int32), CAST(1 AS Int32), CAST(3 AS Int32), CAST(518 AS Int32), CAST(518 AS Int32)]);
  RETURN {
    depth:      outputs['predicted_depth'],
    confidence: outputs['confidence'],
    extrinsics: outputs['extrinsics'],
    intrinsics: outputs['intrinsics']
  }
END
