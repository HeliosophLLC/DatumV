-- ============================================================================
-- Whisper Base — speech-to-text, MIT.
-- ============================================================================
--
-- Catalog id:  whisper-base                     (models/catalog.json)
-- License:     MIT
-- Upstream:    https://github.com/openai/whisper
--              ONNX export: https://huggingface.co/onnx-community/whisper-base
--
-- OpenAI Whisper base — 30-second audio clip → English transcript. Single
-- text encoder (CLIP-H-class) + autoregressive decoder, greedy decode,
-- no KV cache (the no-cache export trades ~5× decode speed for code
-- simplicity; matches what the deleted C# WhisperOnnxModel did).
--
-- **Pipeline:**
--   1. audio_to_mono + audio_samples — Decode the WAV bytes, downmix to
--                                    mono if needed, resample to 16 kHz.
--                                    Returns Float32[] of raw PCM samples
--                                    in [-1, 1].
--   2. audio_to_log_mel             — Whisper feature extractor:
--                                    center-padded STFT (n_fft=400,
--                                    hop=160, Hann window), Slaney mel
--                                    filterbank, log10, max-8 dB clip +
--                                    shift to ~[-1, 1]. 80 mel channels
--                                    × 3000 frames = 240,000 Float32.
--   3. infer('encoder')             — Mel features → encoder hidden states.
--                                    Shape [1, 80, 3000] in, [1, 1500, 512]
--                                    out (base size; small/medium have
--                                    768 / 1024 hidden).
--   4. decode_seq2seq               — Greedy autoregressive loop with the
--                                    Whisper task prefix [SOT, EN,
--                                    TRANSCRIBE, NO_TIMESTAMPS] and
--                                    special-token suppression
--                                    (suppress_above=50257 caps argmax
--                                    at content tokens + EOS).
--   5. tokenizer.decode_bpe         — Token ids → raw text via BPE.
--   6. tokenizer.byte_level_decode  — GPT-2 byte-level inverse (Ġ → space,
--                                    etc.); strips the encoded representation
--                                    that BPE leaves behind.
--
-- **Audio length.** Hardcoded to 30 s clips (Whisper's encoder context).
-- audio_to_log_mel truncates anything longer to the encoder's 480_000
-- sample limit and zero-pads shorter clips. Sliding-window chunking for
-- multi-minute audio is out of scope here — pre-split upstream.
--
-- **Language / task.** This body bakes in English transcription
-- (lang=50259, task=50359). For other languages, change the prefix's
-- second token (50260=Chinese, 50261=German, etc.); for translation
-- to English, swap 50359 → 50358.
-- ============================================================================

CREATE OR REPLACE MODEL whisper_base(
  clip       Audio,
  max_tokens Int32 = 448
    CHECK (max_tokens BETWEEN 1 AND 448)
    COMMENT 'Hard cap on generated tokens per clip. Whispers ceiling is 448 (positional embeddings); lower values bound the decoder loop for short clips.'
) RETURNS String
IMPLEMENTS AudioToText
USING 'whisper-base/onnx/encoder_model.onnx' AS encoder,
      'whisper-base/onnx/decoder_model.onnx' AS decoder
AS BEGIN
  -- 1. WAV bytes → mono 16 kHz Float32 samples.
  DECLARE samples Float32[] = audio_samples(16000, audio_to_mono(clip));

  -- 2. Log-mel features: 80 mel channels × 3000 frames, flat mel-major.
  DECLARE mel Float32[] = audio_to_log_mel(samples, 80::Int32);

  -- 3. Encoder pass.
  DECLARE encoder_features Float32[] = infer(
    'encoder', mel, [1::Int32, 80::Int32, 3000::Int32]);

  -- 4. Greedy decode. Prefix is [SOT, EN, TRANSCRIBE, NO_TIMESTAMPS];
  -- suppress_above=50257 keeps argmax at the content vocab (allowing
  -- EOS at 50257 but dropping the 50258+ task / language / timestamp
  -- markers the decoder occasionally fires mid-transcript).
  DECLARE token_ids Int64[] = decode_seq2seq(
    'decoder',
    encoder_features,
    NULL,                                                       -- no encoder_attention_mask
    [50258::Int64, 50259::Int64, 50359::Int64, 50363::Int64],   -- [SOT, EN, TRANSCRIBE, NO_TIMESTAMPS]
    50257::Int64,                                               -- EOS
    max_tokens,
    false,                                                      -- use_kv_cache (no-cache export)
    50257::Int64);                                              -- suppress_above

  -- 5. Tokens → text, then strip GPT-2 byte-level mojibake (Ġ → space, etc.).
  DECLARE raw String = tokenizer.decode_bpe(token_ids, '../vocab.json', '../merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END
