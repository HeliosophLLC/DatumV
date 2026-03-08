-- ============================================================================
-- YOLOX-M — balanced size, 25.3M params, 640×640 input.
-- ============================================================================
-- Catalog id:  yolox-m    ONNX: yolox_m.onnx    License: Apache-2.0
-- CPU-viable, GPU-faster. See yolox-s.sql for pipeline notes.
-- ============================================================================

CREATE OR REPLACE MODEL yolox_m(img Image) RETURNS Array<LabeledDetection>
IMPLEMENTS LabeledObjectDetector
USING 'yolox-m/yolox_m.onnx'
AS BEGIN
  DECLARE tensor Float32[] = yolox_preprocess(img, 640);
  DECLARE raw    Float32[] = infer(tensor, [CAST(1 AS Int32), CAST(3 AS Int32), CAST(640 AS Int32), CAST(640 AS Int32)]);
  DECLARE labels Array<String> = read_string_list('coco-classes.json');
  RETURN yolox_postprocess(raw, labels, img, 640, CAST(0.25 AS Float32), CAST(0.45 AS Float32))
END
