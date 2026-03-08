-- ============================================================================
-- YOLOX-S — Apache-2.0 object detector, COCO-80 classes.
-- ============================================================================
--
-- Catalog id:  yolox-s        (models/catalog.json)
-- ONNX file:   yolox_s.onnx
-- License:     Apache-2.0
-- Upstream:    https://github.com/Megvii-BaseDetection/YOLOX
--
-- Letterbox-preprocessed image → ONNX → decoded bboxes + class labels.
-- 9.0M params, 640×640 input. The recommended general-purpose detector
-- when accuracy matters but VRAM is constrained.
--
-- Pipeline:
--   1. yolox_preprocess       — letterbox to 640×640, BGR, raw 0-255,
--                               114-gray padding, NCHW Float32.
--   2. infer                  — single ONNX dispatch; output is
--                               [1, 8400, 85] = (4 bbox + 1 objectness + 80
--                               class scores) per anchor over the three
--                               FPN strides 8/16/32.
--   3. read_string_list       — loads coco-classes.json from the model's
--                               USING directory, cached process-wide.
--   4. yolox_postprocess      — bbox decoder + objectness × class filter +
--                               class-aware NMS + reverse letterbox; emits
--                               Array<LabeledDetection> (nested bbox +
--                               label + score in original-image pixel
--                               coordinates).
--
-- Hyperparameter defaults:
--   conf_thresh = 0.25
--   iou_thresh  = 0.45
-- ============================================================================

CREATE OR REPLACE MODEL yolox_s(img Image) RETURNS Array<LabeledDetection>
IMPLEMENTS LabeledObjectDetector
USING 'yolox-s/yolox_s.onnx'
AS BEGIN
  DECLARE tensor Float32[] = yolox_preprocess(img, 640);
  -- ONNX input is pinned to [1, 3, 640, 640] in Megvii's default exports
  -- (no dynamic batch); the 2-arg infer() form keeps the shape unambiguous.
  DECLARE raw    Float32[] = infer(tensor, [CAST(1 AS Int32), CAST(3 AS Int32), CAST(640 AS Int32), CAST(640 AS Int32)]);
  DECLARE labels Array<String> = read_string_list('coco-classes.json');
  RETURN yolox_postprocess(
    raw, labels, img, 640,
    CAST(0.25 AS Float32),
    CAST(0.45 AS Float32))
END
