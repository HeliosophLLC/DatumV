-- ============================================================================
-- Silero VAD fp16 — voice-activity detection, MIT.
-- ============================================================================
--
-- Catalog id:  silero-vad-fp16                  (models/catalog.json)
-- ONNX file:   onnx/model_fp16.onnx
-- License:     MIT
-- Upstream:    https://github.com/snakers4/silero-vad
--              (onnx-community fp16 ONNX export)
--
-- Half-precision sibling of `silero-vad`. ~1.15 MB on disk vs ~2.24 MB
-- for fp32. Same outputs; pick when bundle size or VRAM footprint
-- matters more than the negligible numerical precision difference.
--
-- Body shape is identical to silero-vad/2026-05-29.sql — only the
-- USING path differs. See that file for the full Silero VAD pipeline
-- rationale and the LSTM state-threading convention.
-- ============================================================================

CREATE OR REPLACE MODEL silero_vad_fp16(clip Audio) RETURNS Array<Float32>
IMPLEMENTS VoiceActivityDetector
USING 'silero-vad-fp16/onnx/model_fp16.onnx'
AS BEGIN
  DECLARE samples Float32[] = audio_samples(16000, audio_to_mono(clip));
  DECLARE frame_size Int32 = 512;
  DECLARE n_frames Int32 = cardinality(samples) / frame_size;
  DECLARE state Float32[] = array_repeat(CAST(0.0 AS Float32), 2 * 1 * 128);
  DECLARE probs Float32[] = array_repeat(CAST(0.0 AS Float32), 0);
  DECLARE i Int32 = 1;
  WHILE i <= n_frames
  BEGIN
    DECLARE frame Float32[] = array_slice(samples, (i - 1) * frame_size + 1, frame_size);
    DECLARE outputs Struct = infer_outputs(
      { input: frame, state: state, sr: CAST(16000 AS Int64) },
      { input: [CAST(1 AS Int32), CAST(512 AS Int32)],
        state: [CAST(2 AS Int32), CAST(1 AS Int32), CAST(128 AS Int32)] });
    SET probs = array_concat(probs, [outputs['output']]);
    SET state = outputs['stateN'];
    SET i = i + 1
  END
  RETURN probs
END
