-- ============================================================================
-- Whisper Large v3 Turbo — speech-to-text, MIT.
-- ============================================================================
--
-- Catalog id:  whisper-large-v3-turbo           (models/catalog.json)
-- License:     MIT
-- Upstream:    https://huggingface.co/onnx-community/whisper-large-v3-turbo
--
-- OpenAI Whisper Large v3 with a 4-layer decoder (the "turbo" variant) —
-- near-large-v3 transcription quality at ~7× the decoder throughput.
-- Same 30-second audio context as the smaller variants, but two
-- load-bearing differences from base / small / tiny:
--
--   1. **n_mels = 128** (vs 80 for the other variants). large-v3 was the
--      first Whisper retrained on the wider mel filterbank — feeding it
--      80-channel features produces silence / hallucination. The encoder
--      input shape is therefore [1, 128, 3000].
--
--   2. **Special-token IDs are shifted by 1.** large-v3 added Cantonese as
--      a new language token, which pushed every later special token down
--      one slot:
--
--        | token                 | v1/v2 | large-v3 |
--        |-----------------------|-------|----------|
--        | <|endoftext|>         | 50257 | 50256    |
--        | <|startoftranscript|> | 50258 | 50257    |
--        | <|en|>                | 50259 | 50258    |
--        | <|transcribe|>        | 50359 | 50358    |
--        | <|notimestamps|>      | 50363 | 50362    |
--
--      Reusing the v1/v2 IDs against a large-v3 model produces
--      garbage transcripts (the prefix maps to entirely different
--      task / language semantics inside the model). suppress_above
--      moves to 50256 to match the new EOS position.
--
-- See models/sql/whisper-base/2026-05-29.sql for the canonical annotated
-- reference of the shared pipeline shape.
-- ============================================================================

CREATE OR REPLACE MODEL whisper_large_v3_turbo(
  clip       Audio,
  max_tokens Int32 = 448
    CHECK (max_tokens BETWEEN 1 AND 448)
    COMMENT 'Hard cap on generated tokens per clip. Whispers ceiling is 448 (positional embeddings); lower values bound the decoder loop for short clips.'
) RETURNS String
IMPLEMENTS AudioToText
USING 'whisper-large-v3-turbo/onnx/encoder_model.onnx' AS encoder,
      'whisper-large-v3-turbo/onnx/decoder_model.onnx' AS decoder
AS BEGIN
  -- 1. WAV bytes → mono 16 kHz Float32 samples.
  DECLARE samples Float32[] = audio_samples(16000, audio_to_mono(clip));

  -- 2. Log-mel features: 128 mel channels × 3000 frames, flat mel-major.
  -- Note the 128 (not 80) — large-v3 is the wide-filterbank variant.
  DECLARE mel Float32[] = audio_to_log_mel(samples, 128::Int32);

  -- 3. Encoder pass.
  DECLARE encoder_features Float32[] = infer(
    'encoder', mel, [1::Int32, 128::Int32, 3000::Int32]);

  -- 4. Greedy decode. Prefix is [SOT, EN, TRANSCRIBE, NO_TIMESTAMPS] using
  -- the large-v3 special-token IDs (Cantonese addition shifted every
  -- multilingual special token down by 1 vs v1/v2). suppress_above=50256
  -- keeps argmax at the content vocab (allowing EOS at 50256 but dropping
  -- the 50257+ task / language / timestamp markers the decoder
  -- occasionally fires mid-transcript).
  DECLARE token_ids Int64[] = decode_seq2seq(
    'decoder',
    encoder_features,
    NULL,                                                       -- no encoder_attention_mask
    [50257::Int64, 50258::Int64, 50358::Int64, 50362::Int64],   -- [SOT, EN, TRANSCRIBE, NO_TIMESTAMPS]
    50256::Int64,                                               -- EOS
    max_tokens,
    false,                                                      -- use_kv_cache (no-cache export)
    50256::Int64);                                              -- suppress_above

  -- 5. Tokens → text, then strip GPT-2 byte-level mojibake (Ġ → space, etc.).
  DECLARE raw String = tokenizer.decode_bpe(token_ids, '../vocab.json', '../merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END
