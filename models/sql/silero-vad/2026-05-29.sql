-- ============================================================================
-- Silero VAD — voice-activity detection, MIT.
-- ============================================================================
--
-- Catalog id:  silero-vad                       (models/catalog.json)
-- ONNX file:   onnx/model.onnx
-- License:     MIT
-- Upstream:    https://github.com/snakers4/silero-vad
--              (onnx-community ONNX export)
--
-- Tiny (~2 MB) voice-activity classifier. Recurrent (LSTM-based): each
-- 32 ms frame's prediction depends on hidden state threaded from the
-- prior frame. The ONNX export reflects that — inputs are
-- (audio_frame, lstm_state, sample_rate); outputs are (probability,
-- new_lstm_state). To process a full clip we loop over fixed-size
-- frames, threading state forward.
--
-- **ONNX signature (Silero v5, 16 kHz):**
--   input    Float32 [N, 512]    audio samples (N=batch, 512=32 ms frame)
--   state    Float32 [2, N, 128] LSTM hidden + cell, initial zeros
--   sr       Int64   []          sample rate (16000)
--   output   Float32 [N, 1]      P(speech) for this frame, range [0, 1]
--   stateN   Float32 [2, N, 128] new state to thread to the next frame
--
-- **Body shape.** Decode the audio at 16 kHz mono via `audio_samples`,
-- slice the resulting PCM stream into 512-sample frames, and loop over
-- them feeding each (frame, state) pair to the model. Append per-frame
-- probabilities to the output array; the trailing partial frame (less
-- than 512 samples) is dropped — speech rarely straddles the last
-- 32 ms boundary and clamping is simpler than zero-padding.
--
-- **Output semantics.** Returns a per-frame Float32 vector of speech
-- probabilities — one entry per 32 ms window, value = P(speech) ∈ [0, 1].
-- Higher = more likely speech, lower = more likely silence / noise.
-- Downstream pipelines threshold at ≥0.5 conventionally to gate ASR /
-- Whisper input or compute speech-time statistics.
-- ============================================================================

-- The body parameter is `clip` (not `audio`) because `Audio` is a type
-- keyword and a bare lowercase `audio` in expression position parses as
-- a type literal rather than a variable reference. Same convention as
-- metric3d-v2's `img Image` — the parameter name is local to the body
-- and doesn't affect the task signature.
CREATE OR REPLACE MODEL silero_vad(clip Audio) RETURNS Array<Float32>
IMPLEMENTS VoiceActivityDetector
USING 'silero-vad/2026-05-29/onnx/model.onnx'
AS BEGIN
  -- Pipe through audio_to_mono so stereo / multi-channel sources work
  -- transparently — silero is mono-only by design, but most real-world
  -- audio (music, podcasts, phone recordings) is stereo. The downmix
  -- is a no-op for already-mono sources.
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
