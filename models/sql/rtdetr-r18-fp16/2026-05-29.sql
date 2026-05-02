-- ============================================================================
-- RT-DETR-R18 (fp16) — Apache-2.0 half-precision NMS-free detector.
-- ============================================================================
--
-- Catalog id:  rtdetr-r18-fp16   (models/catalog.json)
-- ONNX file:   onnx/model_fp16.onnx
-- License:     Apache-2.0
--
-- Half-precision RT-DETR-R18. Same architecture as the fp32 variant, same
-- 80-class COCO vocabulary, near-identical accuracy. ~half the disk and
-- markedly faster on GPUs with native fp16; on pure-CPU runtimes that
-- upcast, expect no quality change but no speed win either. See
-- rtdetr-r18.sql for the full pipeline notes — body shape is identical
-- bar the USING path.
-- ============================================================================

CREATE OR REPLACE MODEL rtdetr_r18_fp16(
  img Image,
  conf_thresh Float32 = 0.5::Float32
    CHECK (conf_thresh BETWEEN 0.0 AND 1.0) STEP 0.05
    COMMENT 'Per-query max-class-probability floor for emitting a detection.'
) RETURNS Array<LabeledDetection>
IMPLEMENTS LabeledObjectDetector
USING 'rtdetr-r18-fp16/2026-05-29/onnx/model_fp16.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(img, [640, 640]);
  DECLARE outputs Struct = infer_outputs(
    tensor,
    [1::Int32, 3::Int32, 640::Int32, 640::Int32]);
  DECLARE logits Float32[] = outputs['logits'];
  DECLARE boxes  Float32[] = outputs['pred_boxes'];
  DECLARE labels Array<String> = [
    'person', 'bicycle', 'car', 'motorcycle', 'airplane', 'bus', 'train', 'truck',
    'boat', 'traffic light', 'fire hydrant', 'stop sign', 'parking meter', 'bench',
    'bird', 'cat', 'dog', 'horse', 'sheep', 'cow', 'elephant', 'bear', 'zebra',
    'giraffe', 'backpack', 'umbrella', 'handbag', 'tie', 'suitcase', 'frisbee',
    'skis', 'snowboard', 'sports ball', 'kite', 'baseball bat', 'baseball glove',
    'skateboard', 'surfboard', 'tennis racket', 'bottle', 'wine glass', 'cup',
    'fork', 'knife', 'spoon', 'bowl', 'banana', 'apple', 'sandwich', 'orange',
    'broccoli', 'carrot', 'hot dog', 'pizza', 'donut', 'cake', 'chair', 'couch',
    'potted plant', 'bed', 'dining table', 'toilet', 'tv', 'laptop', 'mouse',
    'remote', 'keyboard', 'cell phone', 'microwave', 'oven', 'toaster', 'sink',
    'refrigerator', 'book', 'clock', 'vase', 'scissors', 'teddy bear', 'hair drier',
    'toothbrush'
  ];
  RETURN rtdetr_postprocess(logits, boxes, labels, img, conf_thresh)
END
