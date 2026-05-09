-- ============================================================================
-- Microsoft Florence-2 base (fp16) — vision-language model
-- ============================================================================
--
-- Catalog id:  florence-2-base-ft-fp16   (models/catalog.json)
-- License:     MIT
-- Upstream:    https://huggingface.co/microsoft/Florence-2-base-ft
--              (Heliosoph fp16 ONNX export — onnx-community Florence-2-base-ft)
--
-- One model file (the fp16 build) drives several different tasks selected
-- by a prompt token at runtime: short caption, detailed caption,
-- paragraph caption, OCR with region. We register one SQL-visible
-- catalog name per task so the front-end can show them as distinct
-- entries with their own `IMPLEMENTS` contract.
--
-- Pipeline (one CREATE MODEL body per task — all share this shape):
--   1. image_to_tensor_chw     — resize to 768×768, ImageNet mean/std,
--                                CHW Float32[].
--   2. infer('vision_encoder') — DaViT → [1, visual_seq, 768] hidden states.
--   3. tokenizer.encode_bpe    — instruction text → BPE token ids.
--   4. array_concat            — wrap [BOS=0, ...instruction_ids, EOS=2].
--   5. infer('embed_tokens')   — token ids → [1, prompt_seq, 768] embeddings.
--   6. array_concat            — combined = visual_features || prompt_embeds.
--   7. array_repeat            — attention_mask = [1, total_seq] all 1s.
--   8. infer('encoder')        — BART encoder → encoder_hidden_states.
--                                Multi-input: inputs_embeds + attention_mask.
--   9. decode_seq2seq          — greedy decoder loop in inputs_embeds form
--                                (8-arg variant; embed_tokens runs each step
--                                to lift token ids into the decoder's
--                                embedding space).
--  10. decode_bpe + byte_level_decode — token ids → UTF-8 string.
--
-- File-name conventions:
--   - All four ONNX files share the `_fp16` suffix from the export
--     (vision_encoder_fp16.onnx, embed_tokens_fp16.onnx, etc.)
--   - tokenizer files (vocab.json, merges.txt, …) are flat at the folder
--     root and shared across the fp16 / quantized variants.
-- ============================================================================

-- ---- Short caption (single sentence, COCO-style) ---------------------------

CREATE OR REPLACE MODEL florence2_caption(
  img    Image,
  prompt String = 'What does the image describe?'
    COMMENT 'Florence-2 task instruction. Default targets a single-sentence COCO-style caption; pass alternate task tokens like ''<DETAILED_CAPTION>'', ''<OD>'', ''<CAPTION_TO_PHRASE_GROUNDING>'', or any natural-language instruction Florence-2 was fine-tuned on to switch behavior without picking a different model variant.'
) RETURNS String
IMPLEMENTS ImageCaptioner
USING 'florence-2-base-ft-fp16/2026-05-29/vision_encoder_fp16.onnx' AS vision_encoder,
      'florence-2-base-ft-fp16/2026-05-29/embed_tokens_fp16.onnx'   AS embed_tokens,
      'florence-2-base-ft-fp16/2026-05-29/encoder_model_fp16.onnx'  AS encoder,
      'florence-2-base-ft-fp16/2026-05-29/decoder_model_fp16.onnx'  AS decoder
AS BEGIN
  -- DaViT uses ImageNet normalization.
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [768::Int32, 768::Int32],
    imagenet_mean(),
    imagenet_std());

  DECLARE visual_features Float32[] = infer(
    'vision_encoder',
    tensor,
    [1::Int32, 3::Int32, 768::Int32, 768::Int32]);

  -- Instruction text → BPE → [BOS, ...ids, EOS]. Default is the literal
  -- Florence-2 used during fine-tuning for this task (Microsoft's
  -- Florence-2 model card lists the mapping); callers may override via
  -- the `prompt` parameter to access the wider task surface (OD,
  -- dense-region-caption, caption-to-phrase-grounding, …).
  DECLARE instruction_ids Int64[] = tokenizer.encode_bpe(
    prompt, 'vocab.json', 'merges.txt');
  DECLARE prompt_ids Int64[] = array_concat(
    array_concat([CAST(0 AS Int64)], instruction_ids),
    [CAST(2 AS Int64)]);
  DECLARE prompt_seq Int32 = cardinality(prompt_ids);
  DECLARE prompt_embeds Float32[] = infer(
    'embed_tokens',
    prompt_ids,
    [1::Int32, prompt_seq]);

  -- Florence-2 base hidden_dim is 768; derive visual_seq from the
  -- visual_features length. The combined sequence (visual || prompt)
  -- is what the BART encoder consumes as inputs_embeds.
  DECLARE visual_seq Int32 = cardinality(visual_features) / 768::Int32;
  DECLARE total_seq Int32 = visual_seq + prompt_seq;
  DECLARE combined_embeds Float32[] = array_concat(visual_features, prompt_embeds);
  DECLARE attention_mask Int64[] = array_repeat(1::Int64, total_seq);

  DECLARE encoder_features Float32[] = infer(
    'encoder',
    {inputs_embeds: combined_embeds, attention_mask: attention_mask},
    {inputs_embeds: [1::Int32, total_seq, 768::Int32],
     attention_mask: [1::Int32, total_seq]});

  DECLARE token_ids Int64[] = decode_seq2seq(
    'decoder',
    encoder_features,
    attention_mask,                              -- decoder reuses encoder's mask
    [CAST(0 AS Int64)],                          -- BOS = decoder_start_token
    CAST(2 AS Int64),                            -- EOS
    CAST(50 AS Int32),                           -- short caption — small budget
    false,                                       -- Florence-2 export is no-cache
    'embed_tokens');                             -- decoder takes inputs_embeds

  DECLARE raw String = tokenizer.decode_bpe(token_ids, 'vocab.json', 'merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END;

-- ---- Detailed caption ------------------------------------------------------

CREATE OR REPLACE MODEL florence2_detailed_caption(
  img    Image,
  prompt String = 'Describe in detail what is shown in the image.'
    COMMENT 'Florence-2 task instruction. Default targets a multi-sentence detailed caption (max 150 tokens); override to repurpose this variant''s token budget for other task tokens.'
) RETURNS String
IMPLEMENTS ImageCaptioner
USING 'florence-2-base-ft-fp16/2026-05-29/vision_encoder_fp16.onnx' AS vision_encoder,
      'florence-2-base-ft-fp16/2026-05-29/embed_tokens_fp16.onnx'   AS embed_tokens,
      'florence-2-base-ft-fp16/2026-05-29/encoder_model_fp16.onnx'  AS encoder,
      'florence-2-base-ft-fp16/2026-05-29/decoder_model_fp16.onnx'  AS decoder
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [768::Int32, 768::Int32],
    imagenet_mean(),
    imagenet_std());

  DECLARE visual_features Float32[] = infer(
    'vision_encoder', tensor,
    [1::Int32, 3::Int32, 768::Int32, 768::Int32]);

  DECLARE instruction_ids Int64[] = tokenizer.encode_bpe(
    prompt,
    'vocab.json', 'merges.txt');
  DECLARE prompt_ids Int64[] = array_concat(
    array_concat([CAST(0 AS Int64)], instruction_ids),
    [CAST(2 AS Int64)]);
  DECLARE prompt_seq Int32 = cardinality(prompt_ids);
  DECLARE prompt_embeds Float32[] = infer(
    'embed_tokens', prompt_ids,
    [CAST(1 AS Int32), prompt_seq]);

  DECLARE visual_seq Int32 = cardinality(visual_features) / 768::Int32;
  DECLARE total_seq Int32 = visual_seq + prompt_seq;
  DECLARE combined_embeds Float32[] = array_concat(visual_features, prompt_embeds);
  DECLARE attention_mask Int64[] = array_repeat(1::Int64, total_seq);

  DECLARE encoder_features Float32[] = infer(
    'encoder',
    {inputs_embeds: combined_embeds, attention_mask: attention_mask},
    {inputs_embeds: [1::Int32, total_seq, 768::Int32],
     attention_mask: [1::Int32, total_seq]});

  DECLARE token_ids Int64[] = decode_seq2seq(
    'decoder', encoder_features, attention_mask,
    [0::Int64], 2::Int64,
    150::Int32,                          -- longer than short caption
    false, 'embed_tokens');

  DECLARE raw String = tokenizer.decode_bpe(token_ids, 'vocab.json', 'merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END;

-- ---- More-detailed (paragraph) caption -------------------------------------

CREATE OR REPLACE MODEL florence2_more_detailed_caption(
  img    Image,
  prompt String = 'Describe with a paragraph what is shown in the image.'
    COMMENT 'Florence-2 task instruction. Default targets a multi-sentence paragraph caption (max 300 tokens); override to access alternate task tokens with the same large token budget.'
) RETURNS String
IMPLEMENTS ImageCaptioner
USING 'florence-2-base-ft-fp16/2026-05-29/vision_encoder_fp16.onnx' AS vision_encoder,
      'florence-2-base-ft-fp16/2026-05-29/embed_tokens_fp16.onnx'   AS embed_tokens,
      'florence-2-base-ft-fp16/2026-05-29/encoder_model_fp16.onnx'  AS encoder,
      'florence-2-base-ft-fp16/2026-05-29/decoder_model_fp16.onnx'  AS decoder
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img, [768::Int32, 768::Int32],
    imagenet_mean(), imagenet_std());

  DECLARE visual_features Float32[] = infer(
    'vision_encoder', tensor,
    [1::Int32, 3::Int32, 768::Int32, 768::Int32]);

  DECLARE instruction_ids Int64[] = tokenizer.encode_bpe(
    prompt,
    'vocab.json', 'merges.txt');
  DECLARE prompt_ids Int64[] = array_concat(
    array_concat([0::Int64], instruction_ids), [2::Int64]);
  DECLARE prompt_seq Int32 = cardinality(prompt_ids);
  DECLARE prompt_embeds Float32[] = infer(
    'embed_tokens', prompt_ids, [1::Int32, prompt_seq]);

  DECLARE visual_seq Int32 = cardinality(visual_features) / 768::Int32;
  DECLARE total_seq Int32 = visual_seq + prompt_seq;
  DECLARE combined_embeds Float32[] = array_concat(visual_features, prompt_embeds);
  DECLARE attention_mask Int64[] = array_repeat(1::Int64, total_seq);

  DECLARE encoder_features Float32[] = infer(
    'encoder',
    {inputs_embeds: combined_embeds, attention_mask: attention_mask},
    {inputs_embeds: [1::Int32, total_seq, 768::Int32],
     attention_mask: [1::Int32, total_seq]});

  DECLARE token_ids Int64[] = decode_seq2seq(
    'decoder', encoder_features, attention_mask,
    [0::Int64], 2::Int64,
    300::Int32,                          -- multi-sentence paragraph
    false, 'embed_tokens');

  DECLARE raw String = tokenizer.decode_bpe(token_ids, 'vocab.json', 'merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END;

-- ---- OCR with region tokens ------------------------------------------------
--
-- Returns a single string interleaving each text run with four <loc_*>
-- bounding-box tokens (Florence-2's 0–999 quantized-coordinate convention).
-- License-clean substitute for OmniParser's AGPL detector when the
-- consumer is an LLM that can parse the location-token stream itself;
-- structured Array<Struct{text, bbox}> parsing is a follow-up.

CREATE OR REPLACE MODEL florence2_ocr_with_region(
  img    Image,
  prompt String = 'What is the text in the image, with regions?'
    COMMENT 'Florence-2 task instruction. Default targets OCR with <loc_*> region tokens (max 500 tokens); override to access alternate long-output task tokens like ''<OD>'' or ''<DENSE_REGION_CAPTION>'' with the same generous token budget.'
) RETURNS String
IMPLEMENTS TextRecognizer
USING 'florence-2-base-ft-fp16/2026-05-29/vision_encoder_fp16.onnx' AS vision_encoder,
      'florence-2-base-ft-fp16/2026-05-29/embed_tokens_fp16.onnx'   AS embed_tokens,
      'florence-2-base-ft-fp16/2026-05-29/encoder_model_fp16.onnx'  AS encoder,
      'florence-2-base-ft-fp16/2026-05-29/decoder_model_fp16.onnx'  AS decoder
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img, [768::Int32, 768::Int32],
    imagenet_mean(), imagenet_std());

  DECLARE visual_features Float32[] = infer(
    'vision_encoder', tensor,
    [1::Int32, 3::Int32, 768::Int32, 768::Int32]);

  DECLARE instruction_ids Int64[] = tokenizer.encode_bpe(
    prompt,
    'vocab.json', 'merges.txt');
  DECLARE prompt_ids Int64[] = array_concat(
    array_concat([0::Int64], instruction_ids), [2::Int64]);
  DECLARE prompt_seq Int32 = cardinality(prompt_ids);
  DECLARE prompt_embeds Float32[] = infer(
    'embed_tokens', prompt_ids, [1::Int32, prompt_seq]);

  DECLARE visual_seq Int32 = cardinality(visual_features) / 768::Int32;
  DECLARE total_seq Int32 = visual_seq + prompt_seq;
  DECLARE combined_embeds Float32[] = array_concat(visual_features, prompt_embeds);
  DECLARE attention_mask Int64[] = array_repeat(1::Int64, total_seq);

  DECLARE encoder_features Float32[] = infer(
    'encoder',
    {inputs_embeds: combined_embeds, attention_mask: attention_mask},
    {inputs_embeds: [1::Int32, total_seq, 768::Int32],
     attention_mask: [1::Int32, total_seq]});

  DECLARE token_ids Int64[] = decode_seq2seq(
    'decoder', encoder_features, attention_mask,
    [0::Int64], 2::Int64,
    500::Int32,                          -- OCR streams can be long
    false, 'embed_tokens');

  DECLARE raw String = tokenizer.decode_bpe(token_ids, 'vocab.json', 'merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END
