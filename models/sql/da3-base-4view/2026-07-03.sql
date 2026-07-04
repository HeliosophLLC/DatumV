-- ============================================================================
-- Depth Anything 3 Base, 4-view — camera-pose recovery, Apache-2.0.
-- ============================================================================
--
-- Catalog id:  da3-base-4view                  (models/catalog.json)
-- ONNX file:   model.onnx
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/depth-anything/DA3-BASE
--              (Heliosoph.DatumV ONNX export from upstream safetensors,
--              traced with -Views 4; see scripts/export-da3metric.ps1)
--
-- The same DA3-BASE checkpoint as `da3_base`, traced with a 4-frame
-- window so the cross-view attention — and therefore the pose head —
-- actually functions. Four views of one scene in a single forward pass;
-- per-view depth, confidence, camera intrinsics, and **relative camera
-- poses** out (view 1 is the reference).
--
-- **Why exactly 4 views.** ONNX tracing bakes the view count into the
-- cross-view attention reshapes; a graph traced at one count run at
-- another is silently wrong (measured ~5e-1 relative error), so the
-- views axis is pinned and the runtime rejects any other count. Stitch
-- longer sequences with overlapping 4-frame windows. For a different
-- window size, re-export with `-Views N` and clone this body.
--
-- **Scale.** Depth and pose translations share one unknown global scale
-- (the standard any-view ambiguity): shapes and relative geometry are
-- right, absolute size isn't. To land in meters, anchor against the
-- metric estimator on the same frames:
--
--   scale s   = median(da3metric_large_meters(img) / this.depth)
--               over confidence-gated pixels of one (or all) views;
--   pose in m = [R | s·t]   (rotation unchanged; K unchanged — K is in
--               pixels and only ever needs the resolution rescale).
--
-- Outputs return at the model's native 504×504 grid, un-resized: the
-- four views may come from different source resolutions, so per-view
-- alignment is the caller's call. K is at the 504 grid — rescale to a
-- view's source dims via K' = diag(w/504, h/504, 1)·K (see
-- da3_base_full's body for the pattern).
--
-- Output bag:
--   depth       Float32  [1, 4, 504, 504]   up-to-scale, bigger=farther
--   depth_conf  Float32  [1, 4, 504, 504]   per-pixel confidence
--   extrinsics  Float32  [1, 4, 3, 4]       per-view [R | t] world→camera,
--                                           relative within the window
--   intrinsics  Float32  [1, 4, 3, 3]       per-view K at the 504 grid
-- ============================================================================

-- No IMPLEMENTS — there's no task contract for a multi-view pose bundle.
-- The rank-5 input [1, 4, 3, 504, 504] is assembled by concatenating the
-- four per-view CHW tensors in view order (array_concat is row-major
-- append, which is exactly the views-axis layout infer expects).

CREATE OR REPLACE MODEL da3_base_4view(
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
USING 'da3-base-4view/2026-07-03/model.onnx'
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
