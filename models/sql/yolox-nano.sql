-- ============================================================================
-- YOLOX-Nano — smallest YOLOX variant (0.91M params, 416×416 input).
-- ============================================================================
-- Catalog id:  yolox-nano    ONNX: yolox_nano.onnx    License: Apache-2.0
-- For mobile/edge/CPU detection. Same pipeline as yolox-s with smaller
-- input + smaller anchor grid (3549 anchors vs 8400). See yolox-s.sql for
-- the detailed pipeline notes.
-- ============================================================================

CREATE OR REPLACE MODEL yolox_nano(img Image) RETURNS Array<LabeledDetection>
IMPLEMENTS LabeledObjectDetector
USING 'yolox-nano/yolox_nano.onnx'
AS BEGIN
  DECLARE tensor Float32[] = yolox_preprocess(img, 416);
  DECLARE raw    Float32[] = infer(tensor, [CAST(1 AS Int32), CAST(3 AS Int32), CAST(416 AS Int32), CAST(416 AS Int32)]);
  DECLARE labels Array<String> = read_string_list('coco-classes.json');
  RETURN yolox_postprocess(raw, labels, img, 416, CAST(0.25 AS Float32), CAST(0.45 AS Float32))
END
