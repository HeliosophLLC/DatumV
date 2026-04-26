-- ============================================================================
-- TrOCR Base Printed — Microsoft TrOCR fp32 for printed-text OCR
-- ============================================================================
--
-- Catalog id:  trocr-base-printed   (models/catalog.json)
-- License:     MIT
-- Upstream:    https://huggingface.co/microsoft/trocr-base-printed
--              (Xenova ONNX export: https://huggingface.co/Xenova/trocr-base-printed)
--
-- Vision Transformer encoder (ViT-base, 384×384) feeding a RoBERTa-style
-- 12-layer autoregressive decoder with cross-attention. Pair with a
-- text-region detector (PaddleOCR-det / Florence-2) for end-to-end OCR;
-- the model expects a single tightly-cropped line of printed text.
--
-- Pipeline:
--   1. image_to_tensor_chw   — resize to 384×384, ViT [0.5, 0.5, 0.5]
--                              mean/std normalize (NOT ImageNet).
--   2. infer('encoder')       — ViT → [1, 577, 768] hidden states.
--   3. decode_seq2seq         — KV-cache greedy loop against the merged
--                              decoder. Step 0 prefills with [2] (RoBERTa
--                              BOS = decoder_start_token), use_cache_branch
--                              flips true thereafter. Generation stops on
--                              EOS = 2 or max_tokens.
--   4. decode_bpe + byte_level_decode — RoBERTa byte-level BPE → UTF-8.
--
-- File layout assumes the HuggingFace include patterns are preserved
-- (`onnx/encoder_model.onnx` ends up at <folder>/onnx/encoder_model.onnx;
-- tokenizer/configs flat at <folder>/vocab.json etc). The vocab/merges
-- references use `../vocab.json` because the model directory (resolved
-- from encoder_model.onnx) is the `onnx/` subdir.
-- ============================================================================

CREATE OR REPLACE MODEL trocr_printed(img Image) RETURNS String
IMPLEMENTS TextRecognizer
USING 'trocr-base-printed/onnx/encoder_model.onnx' AS encoder,
      'trocr-base-printed/onnx/decoder_model_merged.onnx' AS decoder
AS BEGIN
  -- Step 1: TrOCR uses ViT mean/std (NOT ImageNet).
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [CAST(384 AS Int32), CAST(384 AS Int32)],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)]);

  -- Step 2: ViT encoder → [1, 577, 768]. The ONNX export declares all
  -- four input dims as dynamic, so pass the explicit shape.
  DECLARE encoder_features Float32[] = infer(
    'encoder',
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(384 AS Int32), CAST(384 AS Int32)]);

  -- Step 3: KV-cache greedy decode. The merged decoder uses use_cache_branch
  -- to switch between prefill (step 0, full prefix) and incremental
  -- (step 1+, single new token + past KV from previous step). RoBERTa
  -- token ids: BOS = decoder_start_token = 2, EOS = 2 (same value;
  -- generation stops when 2 is reproduced after generation starts).
  DECLARE token_ids Int64[] = decode_seq2seq(
    'decoder',
    encoder_features,
    NULL,                                  -- decoder takes no encoder_attention_mask
    [CAST(2 AS Int64)],                    -- decoder_start_token
    CAST(2 AS Int64),                      -- eos_token_id
    CAST(20 AS Int32),                     -- max_tokens (generation_config default)
    true);                                 -- use_kv_cache

  -- Step 4: token ids → UTF-8. Vocab/merges live one level up from the
  -- encoder ONNX (sibling of the `onnx/` subdir). byte_level_decode
  -- inverts RoBERTa's byte-level BPE mojibake and trims leading-space.
  DECLARE raw String = tokenizer.decode_bpe(
    token_ids,
    '../vocab.json',
    '../merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END
