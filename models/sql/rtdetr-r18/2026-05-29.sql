-- ============================================================================
-- RT-DETR-R18 — Apache-2.0 NMS-free transformer object detector, COCO-80.
-- ============================================================================
--
-- Catalog id:  rtdetr-r18        (models/catalog.json)
-- ONNX file:   onnx/model.onnx
-- License:     Apache-2.0
-- Upstream:    https://github.com/lyuwenyu/RT-DETR (Baidu)
--              https://huggingface.co/onnx-community/rtdetr_r18vd_coco_o365 (ONNX export)
--
-- Baidu's Real-Time DEtection TRansformer with ResNet-18 backbone. Same
-- COCO-80 vocabulary as YOLOX but with a transformer detection head — no
-- anchors, no NMS, ~46 mAP at ~real-time speed.
--
-- Pipeline:
--   1. image_to_tensor_chw — stretch-resize to 640×640, RGB, pixel/255
--                            rescale only — no ImageNet mean/std. Matches
--                            HuggingFace's RTDetrImageProcessor whose
--                            preprocessor_config.json declares
--                            `do_normalize: false`, `rescale_factor: 1/255`.
--   2. infer_outputs       — multi-output ONNX dispatch. Emits two
--                            tensors: `logits` [1, 300, 80] and
--                            `pred_boxes` [1, 300, 4] (normalized
--                            [cx, cy, w, h] in original-image space).
--   3. rtdetr_postprocess  — sigmoid + per-query argmax + confidence
--                            filter + box denormalization. NO NMS (RT-DETR
--                            is set-prediction). Emits
--                            Array<LabeledDetection> in original-image
--                            pixel coordinates.
--
-- Hyperparameter:
--   conf_thresh Float32 = 0.5     Per-query max-class-probability floor.
--                                  Override per call:
--                                    SELECT models.rtdetr_r18(img, 0.3) FROM photos
--
-- Labels. Hardcoded COCO-80 in author-list order (matches HuggingFace's
-- transformers `RTDetrForObjectDetection.config.id2label`). Embedding them
-- in SQL keeps the file standalone — the onnx-community RT-DETR bundle
-- doesn't ship a separate labels file like the YOLOX bundle does.
-- ============================================================================

CREATE OR REPLACE MODEL rtdetr_r18(
  img Image,
  conf_thresh Float32 = 0.5::Float32
    CHECK (conf_thresh BETWEEN 0.0 AND 1.0) STEP 0.05
    COMMENT 'Per-query max-class-probability floor for emitting a detection.'
) RETURNS Array<LabeledDetection>
IMPLEMENTS LabeledObjectDetector
USING 'rtdetr-r18/onnx/model.onnx'
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
