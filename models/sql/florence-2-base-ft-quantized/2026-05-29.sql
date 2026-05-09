-- ============================================================================
-- Microsoft Florence-2 base (INT8 quantized) — vision-language model
-- ============================================================================
--
-- Catalog id:  florence-2-base-ft-quantized   (models/catalog.json)
-- License:     MIT
-- Upstream:    Heliosoph/florence-2-base-ft-quantized-onnx
--              (onnx-community Florence-2-base-ft, INT8-quantized export)
--
-- Same body shape as the fp16 sibling — see florence-2-base-ft-fp16.sql for
-- pipeline detail. Only the file paths differ (the `_quantized` suffix on
-- each ONNX file name + the variant folder). Each task gets its own
-- catalog-visible name suffixed with `_q8` so the four Q8 entries sit
-- alongside the four fp16 variants without name collisions.
-- ============================================================================

-- ---- Short caption (single sentence, COCO-style) ---------------------------

CREATE OR REPLACE MODEL florence2_caption_q8(
  img    Image,
  prompt String = 'What does the image describe?'
    COMMENT 'Florence-2 task instruction. Default targets a single-sentence COCO-style caption; pass alternate task tokens like ''<DETAILED_CAPTION>'', ''<OD>'', ''<CAPTION_TO_PHRASE_GROUNDING>'', or any natural-language instruction Florence-2 was fine-tuned on to switch behavior without picking a different model variant.'
) RETURNS String
IMPLEMENTS ImageCaptioner
USING 'florence-2-base-ft-quantized/2026-05-29/vision_encoder_quantized.onnx' AS vision_encoder,
      'florence-2-base-ft-quantized/2026-05-29/embed_tokens_quantized.onnx'   AS embed_tokens,
      'florence-2-base-ft-quantized/2026-05-29/encoder_model_quantized.onnx'  AS encoder,
      'florence-2-base-ft-quantized/2026-05-29/decoder_model_quantized.onnx'  AS decoder
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img, [768::Int32, 768::Int32],
    imagenet_mean(), imagenet_std());

  DECLARE visual_features Float32[] = infer(
    'vision_encoder', tensor,
    [1::Int32, 3::Int32, 768::Int32, 768::Int32]);

  DECLARE instruction_ids Int64[] = tokenizer.encode_bpe(
    prompt, 'vocab.json', 'merges.txt');
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
    50::Int32,                            -- short caption — small budget
    false, 'embed_tokens');

  DECLARE raw String = tokenizer.decode_bpe(token_ids, 'vocab.json', 'merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END;

-- ---- Detailed caption ------------------------------------------------------

CREATE OR REPLACE MODEL florence2_detailed_caption_q8(
  img    Image,
  prompt String = 'Describe in detail what is shown in the image.'
    COMMENT 'Florence-2 task instruction. Default targets a multi-sentence detailed caption (max 150 tokens); override to repurpose this variant''s token budget for other task tokens.'
) RETURNS String
IMPLEMENTS ImageCaptioner
USING 'florence-2-base-ft-quantized/2026-05-29/vision_encoder_quantized.onnx' AS vision_encoder,
      'florence-2-base-ft-quantized/2026-05-29/embed_tokens_quantized.onnx'   AS embed_tokens,
      'florence-2-base-ft-quantized/2026-05-29/encoder_model_quantized.onnx'  AS encoder,
      'florence-2-base-ft-quantized/2026-05-29/decoder_model_quantized.onnx'  AS decoder
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
    150::Int32,                          -- longer than short caption
    false, 'embed_tokens');

  DECLARE raw String = tokenizer.decode_bpe(token_ids, 'vocab.json', 'merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END;

-- ---- More-detailed (paragraph) caption -------------------------------------

CREATE OR REPLACE MODEL florence2_more_detailed_caption_q8(
  img    Image,
  prompt String = 'Describe with a paragraph what is shown in the image.'
    COMMENT 'Florence-2 task instruction. Default targets a multi-sentence paragraph caption (max 300 tokens); override to access alternate task tokens with the same large token budget.'
) RETURNS String
IMPLEMENTS ImageCaptioner
USING 'florence-2-base-ft-quantized/2026-05-29/vision_encoder_quantized.onnx' AS vision_encoder,
      'florence-2-base-ft-quantized/2026-05-29/embed_tokens_quantized.onnx'   AS embed_tokens,
      'florence-2-base-ft-quantized/2026-05-29/encoder_model_quantized.onnx'  AS encoder,
      'florence-2-base-ft-quantized/2026-05-29/decoder_model_quantized.onnx'  AS decoder
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

CREATE OR REPLACE MODEL florence2_ocr_with_region_q8(
  img    Image,
  prompt String = 'What is the text in the image, with regions?'
    COMMENT 'Florence-2 task instruction. Default targets OCR with <loc_*> region tokens (max 500 tokens); override to access alternate long-output task tokens like ''<OD>'' or ''<DENSE_REGION_CAPTION>'' with the same generous token budget.'
) RETURNS String
IMPLEMENTS TextRecognizer
USING 'florence-2-base-ft-quantized/2026-05-29/vision_encoder_quantized.onnx' AS vision_encoder,
      'florence-2-base-ft-quantized/2026-05-29/embed_tokens_quantized.onnx'   AS embed_tokens,
      'florence-2-base-ft-quantized/2026-05-29/encoder_model_quantized.onnx'  AS encoder,
      'florence-2-base-ft-quantized/2026-05-29/decoder_model_quantized.onnx'  AS decoder
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
