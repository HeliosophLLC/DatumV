-- ============================================================================
-- ViT-GPT2 Image Captioning — nlpconnect/vit-gpt2-image-captioning
-- ============================================================================
--
-- Catalog id:  vit-gpt2-image-captioning   (models/catalog.json)
-- Folder:      vit-gpt2-image-captioning   (Heliosoph/...-onnx repo contents)
-- License:     Apache-2.0
--
-- Vision Transformer (ViT-base, 224×224) encoder feeding a GPT-2
-- autoregressive decoder via cross-attention. Single-sentence English
-- captions, short (~10-16 tokens). The vanilla "describe this image"
-- baseline — predates Florence-2's task-tokens, so the only thing the
-- model does is plain captions.
--
-- Pipeline (one CREATE MODEL body, four scalar calls):
--   1. image_to_tensor_chw  — resize to 224×224, ViT [0.5, 0.5, 0.5]
--                              mean/std normalize, pack CHW Float32[]
--   2. infer('encoder')      — ViT → [1, 197, 768] hidden states
--                              (1 CLS token + 196 patches × 768 hidden)
--   3. decode_seq2seq        — greedy GPT-2 decoder loop, no KV cache.
--                              Start + EOS = 50256 (GPT-2 <|endoftext|>).
--                              Max 16 generated tokens.
--   4. decode_bpe + byte_level_decode — token ids → mojibake → UTF-8
--                              caption. byte_level_decode also strips
--                              the leading-space artifact every byte-
--                              level BPE decode produces.
-- ============================================================================

CREATE OR REPLACE MODEL vit_gpt2_caption(img Image) RETURNS String
IMPLEMENTS ImageCaptioner
USING 'vit-gpt2-image-captioning/2026-05-29/encoder_model.onnx' AS encoder,
      'vit-gpt2-image-captioning/2026-05-29/decoder_model.onnx' AS decoder
AS BEGIN
  -- Step 1: ViT preprocessing. nlpconnect/vit-gpt2 was fine-tuned on
  -- top of google/vit-base-patch16-224 which uses [0.5, 0.5, 0.5]
  -- mean/std normalization (NOT ImageNet stats — that's MobileNet's).
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [CAST(224 AS Int32), CAST(224 AS Int32)],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)]);

  -- Step 2: encoder produces [1, 197, 768] flat-packed Float32. The ViT
  -- ONNX export declares all four input dims as dynamic ([?, ?, ?, ?]) so
  -- we must pass the explicit [1, 3, 224, 224] shape — the 1-arg form's
  -- shape resolver only handles a single dynamic dim.
  DECLARE encoder_features Float32[] = infer(
    'encoder',
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(224 AS Int32), CAST(224 AS Int32)]);

  -- Step 3: greedy decoder loop. The decoder declares input_ids +
  -- encoder_hidden_states; no encoder_attention_mask, no KV cache
  -- (the no-cache GPT-2 export rebuilds full sequence each step).
  DECLARE token_ids Int64[] = decode_seq2seq(
    'decoder',
    encoder_features,
    NULL,                              -- decoder takes no encoder_attention_mask
    [CAST(50256 AS Int64)],            -- <|endoftext|> as decoder_start_token
    CAST(50256 AS Int64),              -- and as EOS
    CAST(16 AS Int32),                 -- max_tokens (short captions)
    false);                            -- no KV cache in v1

  -- Step 4: token ids → text. decode_bpe returns the raw byte-level-BPE
  -- mojibake (`Ġ` for space, etc.); byte_level_decode inverts that AND
  -- trims the leading-space artifact every byte-level decode produces.
  DECLARE raw String = tokenizer.decode_bpe(
    token_ids,
    'vocab.json',
    'merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END
