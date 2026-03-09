-- ============================================================================
-- YOLOX-Darknet53 — Darknet53-backbone YOLOX variant, 63.7M params.
-- ============================================================================
-- Catalog id:  yolox-darknet    ONNX: yolox_darknet.onnx    License: Apache-2.0
-- Original Darknet backbone variant (vs CSPDarknet in s/m/l/x); included
-- for reproducibility. CSPDarknet variants generally supersede on accuracy/
-- cost. 640×640 input. See yolox-s.sql for pipeline notes.
-- ============================================================================

CREATE OR REPLACE MODEL yolox_darknet(
  img Image,
  conf_thresh Float32 = CAST(0.25 AS Float32),
  iou_thresh  Float32 = CAST(0.45 AS Float32)
) RETURNS Array<LabeledDetection>
IMPLEMENTS LabeledObjectDetector
USING 'yolox-darknet/yolox_darknet.onnx'
AS BEGIN
  DECLARE tensor Float32[] = yolox_preprocess(img, 640);
  DECLARE raw    Float32[] = infer(tensor, [CAST(1 AS Int32), CAST(3 AS Int32), CAST(640 AS Int32), CAST(640 AS Int32)]);
  DECLARE labels Array<String> = read_string_list('coco-classes.json');
  RETURN yolox_postprocess(raw, labels, img, 640, conf_thresh, iou_thresh)
END
