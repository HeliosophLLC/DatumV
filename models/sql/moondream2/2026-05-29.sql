-- ============================================================================
-- Moondream2 — small (1.9B-param) vision-language model
-- ============================================================================
--
-- Catalog id:  moondream2   (models/catalog.json)
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/vikhyatk/moondream2
--              (Xenova ONNX export — three sessions: vision_encoder,
--              embed_tokens, decoder_model_merged)
--
-- SigLIP-style 378×378 vision encoder feeding a Phi-1.5/2 decoder via
-- spliced image embeddings. The vision encoder bakes the SigLIP→Phi
-- projection into its graph and emits 729 image tokens (27×27 patches at
-- 378/14 stride) in Phi's 2048-d hidden space, ready to splice ahead of
-- the prompt embeddings.
--
-- Pipeline (one CREATE MODEL body, six scalar calls):
--   1. image_to_tensor_chw     — resize to 378×378, SigLIP [0.5, 0.5, 0.5]
--                                mean/std normalize, pack CHW Float32[].
--   2. infer('vision_encoder') — SigLIP → [1, 729, 2048] image features
--                                already projected into Phi's hidden space.
--   3. tokenizer.encode_bpe    — text portion of Moondream's prompt
--                                template → Phi-2 byte-level BPE token ids.
--   4. infer('embed_tokens')   — token ids → [1, T, 2048] text embeddings.
--   5. array_concat            — prefix_embeds = visual_features || text_embeds.
--   6. decode_decoder_only     — KV-cached greedy decoder loop. Step 0
--                                prefills with the full prefix (image +
--                                prompt); subsequent steps embed a single
--                                generated token and grow the cache. Stops
--                                on Phi-2 EOS (50256 = <|endoftext|>) or
--                                max_tokens.
--   7. decode_bpe + byte_level_decode — token ids → UTF-8 answer.
--
-- File layout (Xenova-style export, files placed under the catalog folder):
--   moondream2/onnx/vision_encoder_fp16.onnx
--   moondream2/onnx/embed_tokens_fp16.onnx
--   moondream2/onnx/decoder_model_merged_fp16.onnx
--   moondream2/vocab.json
--   moondream2/merges.txt
--
-- Prompt template — Moondream's training format is the literal string
--   <image>\n\nQuestion: {prompt}\n\nAnswer:
-- where <image> is the splice point we already filled with visual_features;
-- only the surrounding text gets tokenized. We embed the newlines as
-- actual line breaks inside the SQL string literal (this dialect's only
-- string escape is '' for a single quote — no C-style \n).
-- ============================================================================

CREATE OR REPLACE MODEL moondream2(img Image, prompt String) RETURNS String
IMPLEMENTS VisualQA
USING 'moondream2/2026-05-29/vision_encoder_fp16.onnx'        AS vision_encoder,
      'moondream2/embed_tokens_fp16.onnx'          AS embed_tokens,
      'moondream2/decoder_model_merged_fp16.onnx'  AS decoder
AS BEGIN
  -- Step 1: SigLIP normalize — (raw/255 - 0.5)/0.5. Same mean/std as the
  -- ViT family (NOT ImageNet stats).
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [378::Int32, 378::Int32],
    [0.5::Float32, 0.5::Float32, 0.5::Float32],
    [0.5::Float32, 0.5::Float32, 0.5::Float32]);

  -- Step 2: vision encoder → [1, 729, 2048] image features.
  DECLARE visual_features Float32[] = infer(
    'vision_encoder',
    tensor,
    [1::Int32, 3::Int32, 378::Int32, 378::Int32]);

  -- Step 3: tokenize the text portion of Moondream's prompt template.
  -- The literal newlines below are part of the training format.
  DECLARE text_part String = '

Question: ' || prompt || '

Answer:';

  DECLARE text_ids Int64[] = tokenizer.encode_bpe(
    text_part,
    'moondream2/vocab.json',
    'moondream2/merges.txt');
  DECLARE text_seq Int32 = cardinality(text_ids);

  -- Step 4: token ids → [1, text_seq, 2048] text embeddings.
  DECLARE text_embeds Float32[] = infer(
    'embed_tokens',
    text_ids,
    [1::Int32, text_seq]);

  -- Step 5: prefix = visual_features || text_embeds, fed to the decoder
  -- as inputs_embeds for step 0 (prefill). decode_decoder_only consumes
  -- the prefix length implicitly via the hidden_dim derived from the
  -- decoder's past_key_values.0.key shape.
  DECLARE prefix_embeds Float32[] = array_concat(visual_features, text_embeds);

  -- Step 6: KV-cached greedy decode. Phi-2 EOS = 50256 (<|endoftext|>).
  DECLARE token_ids Int64[] = decode_decoder_only(
    'decoder',
    'embed_tokens',
    prefix_embeds,
    50256::Int64,
    256::Int32);

  -- Step 7: Phi-2 uses GPT-2-style byte-level BPE. decode_bpe returns the
  -- raw byte-level-BPE mojibake; byte_level_decode inverts it and trims
  -- the leading-space artifact.
  DECLARE raw String = tokenizer.decode_bpe(
    token_ids,
    'moondream2/vocab.json',
    'moondream2/merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END
