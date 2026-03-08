-- ============================================================================
-- YOLOX-Tiny — step up from Nano with better mAP at minimal extra cost.
-- ============================================================================
-- Catalog id:  yolox-tiny    ONNX: yolox_tiny.onnx    License: Apache-2.0
-- 5.06M params, 416×416 input. See yolox-s.sql for pipeline notes.
-- ============================================================================

CREATE OR REPLACE MODEL yolox_tiny(img Image) RETURNS Array<LabeledDetection>
IMPLEMENTS LabeledObjectDetector
USING 'yolox-tiny/yolox_tiny.onnx'
AS BEGIN
  DECLARE tensor Float32[] = yolox_preprocess(img, 416);
  DECLARE raw    Float32[] = infer(tensor, [CAST(1 AS Int32), CAST(3 AS Int32), CAST(416 AS Int32), CAST(416 AS Int32)]);
  DECLARE labels Array<String> = read_string_list('coco-classes.json');
  RETURN yolox_postprocess(raw, labels, img, 416, CAST(0.25 AS Float32), CAST(0.45 AS Float32))
END
