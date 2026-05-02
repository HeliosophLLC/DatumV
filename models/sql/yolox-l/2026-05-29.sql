-- ============================================================================
-- YOLOX-L — higher quality than M, 54.2M params, 640×640 input.
-- ============================================================================
-- Catalog id:  yolox-l    ONNX: yolox_l.onnx    License: Apache-2.0
-- GPU recommended. See yolox-s.sql for pipeline notes.
-- ============================================================================

CREATE OR REPLACE MODEL yolox_l(
  img Image,
  conf_thresh Float32 = CAST(0.25 AS Float32)
    CHECK (conf_thresh BETWEEN 0.0 AND 1.0) STEP 0.05
    COMMENT 'Objectness × class-score floor applied pre-NMS.',
  iou_thresh  Float32 = CAST(0.45 AS Float32)
    CHECK (iou_thresh BETWEEN 0.0 AND 1.0) STEP 0.05
    COMMENT 'NMS IoU overlap threshold.'
) RETURNS Array<LabeledDetection>
IMPLEMENTS LabeledObjectDetector
USING 'yolox-l/2026-05-29/yolox_l.onnx'
AS BEGIN
  DECLARE tensor Float32[] = yolox_preprocess(img, 640);
  DECLARE raw    Float32[] = infer(tensor, [CAST(1 AS Int32), CAST(3 AS Int32), CAST(640 AS Int32), CAST(640 AS Int32)]);
  DECLARE labels Array<String> = read_string_list('coco-classes.json');
  RETURN yolox_postprocess(raw, labels, img, 640, conf_thresh, iou_thresh)
END
