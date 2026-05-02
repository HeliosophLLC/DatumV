-- ============================================================================
-- MediaPipe Face Detector (INT8 quantized) — Apache-2.0.
-- ============================================================================
--
-- Catalog id:  mediapipe-face-int8             (models/catalog.json)
-- ONNX files:  int8/face_detector.onnx, int8/face_landmark_detector.onnx
-- License:     Apache-2.0
-- Upstream:    https://github.com/google-ai-edge/mediapipe
--              (Qualcomm AI Hub W8A8-quantized ONNX export via Heliosoph)
--
-- INT8-quantized sibling of `mediapipe_face`. Same input/output interface
-- as the float variant, ~half the disk footprint. Pick for CPU / NPU /
-- mobile deployments where size matters; on hosts without INT8-accelerated
-- runtimes the quantized graph may run slower than the float version
-- (ORT upcasts INT8 → INT32 internally). Modest accuracy drop on small /
-- distant faces.
--
-- Body shape is identical to `mediapipe-face.sql` — see that file for the
-- BlazeFace decode-pipeline rationale.
-- ============================================================================

CREATE OR REPLACE MODEL mediapipe_face_int8(
  img         Image,
  conf_thresh Float32 = 0.5::Float32
    CHECK (conf_thresh BETWEEN 0.0 AND 1.0) STEP 0.05
    COMMENT 'Sigmoid-confidence floor. Drops anchors below this score before NMS. Lower (0.3) for hard-to-see faces; raise (0.7+) for strict precision.',
  iou_thresh  Float32 = 0.3::Float32
    CHECK (iou_thresh BETWEEN 0.0 AND 1.0) STEP 0.05
    COMMENT 'IoU overlap threshold for class-less NMS. Lower = stricter dedup (drops overlapping faces); higher = retains overlapping detections.'
) RETURNS Array<FaceDetection>
IMPLEMENTS FaceDetector
USING 'mediapipe-face-int8/2026-05-29/int8/face_detector.onnx'
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
