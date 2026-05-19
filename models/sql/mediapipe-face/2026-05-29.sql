-- ============================================================================
-- MediaPipe Face (Detector) — Apache-2.0.
-- ============================================================================
--
-- Catalog id:  mediapipe-face                  (models/catalog.json)
-- ONNX files:  float/face_detector.onnx, float/face_landmark_detector.onnx
-- License:     Apache-2.0
-- Upstream:    https://github.com/google-ai-edge/mediapipe
--              (Qualcomm AI Hub ONNX export via Heliosoph.DatumV mirror)
--
-- BlazeFace short-range face detector (256×256, two-layer SSD anchor
-- pyramid). Emits a bounding box per detected face plus the 6 BlazeFace
-- keypoints (right eye, left eye, nose tip, mouth, right ear tragion,
-- left ear tragion) plus a confidence score.
--
-- **Detection-only pipeline.** The bundled `face_landmark_detector.onnx`
-- (which emits the 468-point MediaPipe FaceMesh, NOT a 6-point landmark
-- model — the catalog description was inaccurate) is downloaded
-- alongside but not wired to a SQL surface here. Two-stage detect →
-- crop → landmark workflows need a per-face fan-out which the SQL
-- surface doesn't express cleanly today (LATERAL TVF deferred); landmark
-- integration is a separate follow-up model.
--
-- Pipeline:
--   1. image_to_tensor_chw — stretch-resize to 256×256, RGB, raw
--                            pixel/255 in [0, 1] (no per-channel
--                            normalization — BlazeFace convention).
--   2. infer_outputs       — single ONNX dispatch; emits 4 tensors —
--                            box_coords_{1,2} (per-anchor 4 box + 12
--                            keypoint offsets) and box_scores_{1,2}
--                            (raw pre-sigmoid logits).
--   3. blazeface_decode    — concat the two scales, sigmoid scores,
--                            decode boxes + 6 keypoints against the
--                            896-anchor SSD pyramid, class-less NMS,
--                            scale to source-image pixel coords.
-- ============================================================================

CREATE OR REPLACE MODEL mediapipe_face(
  img         Image,
  conf_thresh Float32 = 0.5::Float32
    CHECK (conf_thresh BETWEEN 0.0 AND 1.0) STEP 0.05
    COMMENT 'Sigmoid-confidence floor. Drops anchors below this score before NMS. Lower (0.3) for hard-to-see faces; raise (0.7+) for strict precision.',
  iou_thresh  Float32 = 0.3::Float32
    CHECK (iou_thresh BETWEEN 0.0 AND 1.0) STEP 0.05
    COMMENT 'IoU overlap threshold for class-less NMS. Lower = stricter dedup (drops overlapping faces); higher = retains overlapping detections.'
) RETURNS Array<FaceDetection>
IMPLEMENTS FaceDetector
USING 'mediapipe-face/2026-05-29/float/face_detector.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [256::Int32, 256::Int32],
    [0.0::Float32, 0.0::Float32, 0.0::Float32],
    [1.0::Float32, 1.0::Float32, 1.0::Float32]);

  DECLARE outputs Struct = infer_outputs(
    tensor,
    [1::Int32, 3::Int32, 256::Int32, 256::Int32]);

  RETURN blazeface_decode(
    outputs['box_coords_1'],
    outputs['box_coords_2'],
    outputs['box_scores_1'],
    outputs['box_scores_2'],
    img,
    256::Int32,
    conf_thresh,
    iou_thresh)
END
