-- ============================================================================
-- PaddleOCR PP-OCRv4 Detection — text-region detector for OCR pipelines.
-- ============================================================================
--
-- Catalog id:  paddleocr-v4-det        (models/catalog.json)
-- ONNX file:   ch_PP-OCRv4_det.onnx
-- License:     Apache-2.0
-- Upstream:    https://github.com/PaddlePaddle/PaddleOCR
--
-- DBNet-style binary-segmentation detector. Outputs one bounding box per
-- text region as Struct{label, score, x, y, w, h} in original-image pixel
-- coordinates. Pair with a recognizer (TrOCR / Florence-2 / PP-OCRv4-rec)
-- on each crop for end-to-end OCR.
--
-- Pipeline:
--   1. image_resize_to_stride — aspect-preserving resize, longest side ≤ 960,
--      both dims rounded to multiples of 32 (PaddleOCR's DetResizeForTest).
--   2. image_to_tensor_chw    — pack as Float32 NCHW with ImageNet normalize.
--   3. infer                  — single ONNX dispatch; output is a [1,1,H,W]
--                               probability map at the resized resolution.
--   4. dbnet_postprocess      — threshold + connected-components BFS +
--                               DBNet polygon unclip + scale back to
--                               original-image pixel space.
--
-- Hyperparameter defaults (PaddleOCR canonical):
--   pixel_threshold      = 0.3
--   box_score_threshold  = 0.6
--   min_size             = 3   (pixels in resized space)
--   unclip_ratio         = 1.5
-- ============================================================================

CREATE OR REPLACE MODEL paddleocr_v4_det(img Image) RETURNS Array<Struct>
USING 'paddleocr-v4-det/ch_PP-OCRv4_det.onnx'
AS BEGIN
  DECLARE resized Image    = image_resize_to_stride(img, 960, 32);
  DECLARE rh      Int32    = image_height(resized);
  DECLARE rw      Int32    = image_width(resized);
  DECLARE tensor  Float32[] = image_to_tensor_chw(resized, [rh, rw], imagenet_mean(), imagenet_std());
  -- The ONNX input shape is [-1, 3, -1, -1] (batch, channels, H, W) —
  -- three dynamic dims means infer() can't pick a unique shape from the
  -- flat tensor length alone. The 2-arg form passes the explicit
  -- [batch=1, channels=3, H, W] shape so each per-image resize routes
  -- through cleanly.
  DECLARE prob    Float32[] = infer(tensor, [CAST(1 AS Int32), CAST(3 AS Int32), rh, rw]);
  RETURN dbnet_postprocess(
    prob, rh, rw,
    CAST(image_width(img)  AS Float32) / CAST(rw AS Float32),
    CAST(image_height(img) AS Float32) / CAST(rh AS Float32),
    CAST(0.3 AS Float32),
    CAST(0.6 AS Float32),
    3,
    CAST(1.5 AS Float32))
END
