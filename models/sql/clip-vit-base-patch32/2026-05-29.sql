-- ============================================================================
-- CLIP ViT-B/32 — image-text contrastive embeddings, MIT.
-- ============================================================================
--
-- Catalog id:  clip-vit-base-patch32           (models/catalog.json)
-- ONNX files:  onnx/vision_model.onnx + onnx/text_model.onnx
-- License:     MIT
-- Upstream:    https://github.com/openai/CLIP
--              (Xenova ONNX export of openai/clip-vit-base-patch32)
--
-- OpenAI CLIP: a joint image+text embedding space where matching pairs
-- (image, caption) sit close together under cosine similarity. Two
-- separate ONNX sessions (vision encoder + text encoder), each emitting
-- a 512-dim Float32 vector. After L2-normalisation, dot product equals
-- cosine similarity — pair with `dot_product` / `cosine_similarity` for
-- zero-shot classification, cross-modal search, and de-duplication
-- across modalities.
--
-- File layout (Xenova export, files placed under the catalog folder):
--   clip-vit-base-patch32/onnx/vision_model.onnx
--   clip-vit-base-patch32/onnx/text_model.onnx
--   clip-vit-base-patch32/vocab.json
--   clip-vit-base-patch32/merges.txt
--
-- Two SQL-visible models share the bundle: `clip_image_embed` for the
-- vision side, `clip_text_embed` for the text side. Both emit a 512-dim
-- L2-normalised Float32 vector in the same embedding space.
-- ============================================================================

-- ---- Image embedding ------------------------------------------------------

CREATE OR REPLACE MODEL clip_image_embed(img Image) RETURNS Float32[]
IMPLEMENTS ImageEmbedder
USING 'clip-vit-base-patch32/onnx/vision_model.onnx'
AS BEGIN
  -- CLIP-specific normalisation stats — distinct from ImageNet's. The
  -- mean is [0.48145466, 0.4578275, 0.40821073], std is
  -- [0.26862954, 0.26130258, 0.27577711]; values come from the original
  -- OpenAI preprocessing pipeline.
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [224::Int32, 224::Int32],
    [0.48145466::Float32, 0.4578275::Float32, 0.40821073::Float32],
    [0.26862954::Float32, 0.26130258::Float32, 0.27577711::Float32]);
  -- Vision encoder output: [1, 512] image_embeds. ViT-B/32 emits a single
  -- pooled vector (the [CLS] token's projection).
  DECLARE image_embeds Float32[] = infer(
    tensor,
    [1::Int32, 3::Int32, 224::Int32, 224::Int32]);
  -- Project to the unit sphere so cosine similarity = dot product
  -- between matching pairs.
  RETURN l2_normalize(image_embeds)
END;

-- ---- Text embedding -------------------------------------------------------

CREATE OR REPLACE MODEL clip_text_embed(text String) RETURNS Float32[]
IMPLEMENTS TextEmbedder
USING 'clip-vit-base-patch32/onnx/text_model.onnx'
AS BEGIN
  -- CLIP uses byte-level BPE (same family as GPT-2 / RoBERTa), but
  -- prepends <|startoftext|> (id 49406) and appends <|endoftext|>
  -- (id 49407) — both are required for the text encoder to align with
  -- the trained position embeddings. The vocab/merges files live at
  -- the catalog folder root; from the ONNX file's directory
  -- (`onnx/text_model.onnx`) that's one level up.
  DECLARE raw_ids Int64[] = tokenizer.encode_bpe(
    text, '../vocab.json', '../merges.txt');
  DECLARE wrapped Int64[] = array_concat(
    array_concat([49406::Int64], raw_ids),
    [49407::Int64]);
  DECLARE n Int32 = cardinality(wrapped);
  -- The CLIP text encoder is trained at a max context of 77 tokens.
  -- Position embeddings exist only for positions 0..76 — passing a
  -- longer sequence would index out of bounds inside the model. We
  -- expect callers to keep prompts short (zero-shot classification
  -- usually fits in a dozen tokens). Trailing tokens beyond 77 should
  -- be trimmed by the caller for now; a future `array_slice(arr, 0, n)`
  -- primitive would let us truncate inline.
  -- Text encoder output: [1, 512] text_embeds (pooled).
  DECLARE text_embeds Float32[] = infer(
    wrapped,
    [1::Int32, n]);
  RETURN l2_normalize(text_embeds)
END
