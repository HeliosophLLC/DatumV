-- ============================================================================
-- Depth Anything 3 Metric Large — metric monocular depth + sky mask,
-- Apache-2.0.
-- ============================================================================
--
-- Catalog id:  da3metric-large                 (models/catalog.json)
-- ONNX file:   model.onnx
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/depth-anything/DA3METRIC-LARGE
--              (Heliosoph.DatumV ONNX export from upstream safetensors;
--              see scripts/export-da3metric.ps1)
--
-- The metric-depth monocular variant of ByteDance's Depth Anything 3
-- family — DINOv2 ViT-L encoder + single-channel DPT head, plus a
-- sky-segmentation head. The largest Apache-2.0 model in the DA3 zoo
-- (the any-view Large/Giant checkpoints are CC-BY-NC and not shipped
-- here).
--
-- **Canonical depth.** The network emits depth as seen through a
-- reference camera with a 300-pixel focal length. Real meters are one
-- multiply away:
--
--     metric_depth_m = net_output * focal_px / 300
--
-- where focal_px is the focal length in pixels of the image *as fed to
-- the network* (the 504×504 input grid): focal_px = 252 / tan(fov/2)
-- for a horizontal field of view `fov`. The `_meters` and `_full`
-- bodies below take a `fov_deg` argument (default 60°, a typical
-- phone/webcam horizontal FOV) and apply the conversion internally.
-- Depth is proportional to tan(fov/2)⁻¹, so a wrong FOV scales all
-- distances by a constant factor — pass the real FOV when absolute
-- accuracy matters.
--
-- **Fixed 504×504 input.** The ONNX trace pins the spatial dims (ViT
-- position-embedding interpolation bakes the token count into the
-- graph); the bodies stretch-resize to 504×504 and resize outputs back
-- to source dimensions.
--
-- Output bag (2 outputs — first declared is the depth map):
--   depth   Float32  [batch, 1, 504, 504]   canonical depth, bigger=farther
--   sky     Float32  [batch, 1, 504, 504]   sky score; >= 0.5 means sky
--
-- Depth is unreliable on sky pixels — mask them (via the `_full` body's
-- sky field) before point-cloud reconstruction.
-- ============================================================================

-- ---- Visualization variant (returns grayscale Image) ----------------------
--
-- Canonical depth min-max-normalizes identically to metric depth (the
-- conversion is a constant multiply), so the visualization needs no FOV.
-- `invert => true` keeps "near = bright" consistent with the rest of the
-- depth zoo (bigger value = farther here, same as ZoeDepth / GLPN).

CREATE OR REPLACE MODEL da3metric_large(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'da3metric-large/2026-07-03/model.onnx'
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
--
-- Same body shape as zoedepth_nyu_kitti_meters — see that file for the
-- broader rationale. Canonical→meters conversion happens here:
-- focal_px = 252 / tan(fov/2) on the 504-wide network grid, then
-- × focal_px / 300. The scaled buffer is re-shaped to the native
-- (504, 504) grid and bilinear-resized to source dimensions, ready for
-- `point_cloud_from_depth_pinhole(img, depth, fov_deg)` — pass the same
-- fov_deg to both so the unprojection agrees with the metric scale.

CREATE OR REPLACE MODEL da3metric_large_meters(
  img     Image,
  fov_deg Float32 = 60.0::Float32
    CHECK (fov_deg BETWEEN 20.0 AND 120.0) STEP 1.0 UNIT 'degrees'
    COMMENT 'Horizontal field of view of the source camera. Sets the absolute scale: depth scales by tan(fov/2)⁻¹, so a wrong FOV multiplies all distances by a constant. 60° suits typical phone/webcam photos; check EXIF or calibration for precision work.'
) RETURNS Array<Float32>
IMPLEMENTS DepthEstimatorMetric
USING 'da3metric-large/2026-07-03/model.onnx'
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
--
-- Single forward pass surfacing both heads, aligned to the source image:
--   • depth — meters (same conversion as `_meters` above), bilinear-
--             resized to (image_height, image_width).
--   • sky   — sky score resized to the same grid; `>= 0.5` is the
--             upstream sky threshold. Mask sky before reconstruction:
--             monocular depth on sky pixels is extrapolation, and a
--             "sky at 40 m" wall ruins a point cloud.
--
-- No `IMPLEMENTS` clause — there's no task contract for a depth+sky
-- bundle. The inline `Struct<…>` return annotation is design-time
-- metadata for LanguageServer hover/completion; the body's RETURN
-- literal defines the actual per-row struct shape.

CREATE OR REPLACE MODEL da3metric_large_full(
  img     Image,
  fov_deg Float32 = 60.0::Float32
    CHECK (fov_deg BETWEEN 20.0 AND 120.0) STEP 1.0 UNIT 'degrees'
    COMMENT 'Horizontal field of view of the source camera. Sets the absolute scale: depth scales by tan(fov/2)⁻¹, so a wrong FOV multiplies all distances by a constant. 60° suits typical phone/webcam photos; check EXIF or calibration for precision work.'
) RETURNS Struct<
    depth Array<Float32>,
    sky Array<Float32>
  >
USING 'da3metric-large/2026-07-03/model.onnx'
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
