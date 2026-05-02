-- ============================================================================
-- ZoeDepth NYU-KITTI (fp16) — metric monocular depth, MIT.
-- ============================================================================
--
-- Catalog id:  zoedepth-nyu-kitti-fp16          (models/catalog.json)
-- ONNX file:   model_fp16.onnx
-- License:     MIT
-- Upstream:    https://github.com/isl-org/ZoeDepth
--              (Heliosoph onnxconverter-common fp16 conversion from
--              Intel/zoedepth-nyu-kitti PyTorch weights)
--
-- Half-precision sibling of `zoedepth_nyu_kitti`. ~660 MB on disk vs
-- ~1.3 GB for fp32, same DPT-Large + dual-metric-head architecture, same
-- metric depth output (real-world meters). Pick when deployment footprint
-- matters — container images, edge bundles, dual-load with a relative-
-- depth model. GPU/NPU runtimes with native fp16 see a modest speedup; on
-- CPU runtimes that upcast fp16→fp32 internally, expect identical latency
-- to fp32 but half the model-load memory.
--
-- Body shape is identical to the fp32 sibling (zoedepth-nyu-kitti.sql) —
-- only the USING path differs. ONNX Runtime handles the fp16 weight load
-- + auto-cast transparently; the SQL body still produces / consumes
-- Float32[] tensors via the standard image_to_tensor_chw + infer path.
-- ============================================================================

-- ---- Visualization variant (returns grayscale Image) ----------------------

CREATE OR REPLACE MODEL zoedepth_nyu_kitti_fp16(img Image) RETURNS Image
IMPLEMENTS DepthEstimator
USING 'zoedepth-nyu-kitti-fp16/2026-05-29/model_fp16.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [384, 384],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth Float32[] = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(384 AS Int32), CAST(384 AS Int32)]);
  -- `invert => true` flips the post-normalise brightness so near = bright,
  -- matching the inverse-depth catalog convention. See the fp32 sibling
  -- (zoedepth-nyu-kitti.sql) for the rationale.
  RETURN depth_map_to_image(depth, 384, 384, image_height(img), image_width(img), true)
END;

-- ---- Metric variant (returns raw meters as a shape-aware Float32 array) ---
--
-- Same body shape as the fp32 metric sibling (zoedepth_nyu_kitti_meters);
-- see zoedepth-nyu-kitti.sql for the design notes. Half the ONNX disk
-- footprint, same metric output (ORT upcasts fp16 outputs to Float32 at
-- the boundary so the array element kind is identical). Native 384×384
-- depth is bilinear-resized back to the source image dimensions so the
-- returned array aligns per-pixel with the input.

CREATE OR REPLACE MODEL zoedepth_nyu_kitti_fp16_meters(img Image)
  RETURNS Array<Float32>
IMPLEMENTS DepthEstimatorMetric
USING 'zoedepth-nyu-kitti-fp16/2026-05-29/model_fp16.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [384, 384],
    imagenet_mean(),
    imagenet_std());
  DECLARE depth_native Array<Float32> = infer(
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(384 AS Int32), CAST(384 AS Int32)]);
  RETURN array_resize_2d(depth_native, image_height(img), image_width(img))
END
