-- ============================================================================
-- Whisper Tiny — speech-to-text, MIT.
-- ============================================================================
--
-- Catalog id:  whisper-tiny                     (models/catalog.json)
-- License:     MIT
-- Upstream:    https://huggingface.co/onnx-community/whisper-tiny
--
-- Smallest Whisper variant — same architecture and feature pipeline as
-- whisper-base, just a thinner encoder/decoder pair. Pipeline shape is
-- identical to every other Whisper body; see
-- models/sql/whisper-base/2026-05-29.sql for the canonical annotated
-- reference. This file differs only in the model name and USING paths.
-- ============================================================================

CREATE OR REPLACE MODEL whisper_tiny(
  clip       Audio,
  max_tokens Int32 = 448
    CHECK (max_tokens BETWEEN 1 AND 448)
    COMMENT 'Hard cap on generated tokens per clip. Whispers ceiling is 448 (positional embeddings); lower values bound the decoder loop for short clips.'
) RETURNS String
IMPLEMENTS AudioToText
USING 'whisper-tiny/2026-05-29/onnx/encoder_model.onnx' AS encoder,
      'whisper-tiny/2026-05-29/onnx/decoder_model.onnx' AS decoder
AS BEGIN
  DECLARE samples Float32[] = audio_samples(16000, audio_to_mono(clip));
  DECLARE mel Float32[] = audio_to_log_mel(samples, 80::Int32);
  DECLARE encoder_features Float32[] = infer(
    'encoder', mel, [1::Int32, 80::Int32, 3000::Int32]);

  DECLARE token_ids Int64[] = decode_seq2seq(
    'decoder',
    encoder_features,
    NULL,
    [50258::Int64, 50259::Int64, 50359::Int64, 50363::Int64],
    50257::Int64,
    max_tokens,
    false,
    50257::Int64);

  DECLARE raw String = tokenizer.decode_bpe(token_ids, '../vocab.json', '../merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END
