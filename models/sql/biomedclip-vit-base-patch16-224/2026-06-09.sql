-- ============================================================================
-- BiomedCLIP — biomedical image-text contrastive embeddings, MIT.
-- ============================================================================
--
-- Catalog id:  biomedclip-vit-base-patch16-224  (models/catalog.json)
-- ONNX files:  onnx/vision_model.onnx + onnx/text_model.onnx
-- License:     MIT
-- Upstream:    https://huggingface.co/microsoft/BiomedCLIP-PubMedBERT_256-vit_base_patch16_224
--
-- CLIP-style joint image+text embedding tuned for biomedical figures
-- (radiology, histology, microscopy, charts from PMC papers). Vision
-- tower is ViT-B/16 at 224x224; text tower is PubMedBERT with a
-- biomedical WordPiece vocab. Both project into a shared 512-d space —
-- after L2-normalisation, dot product equals cosine similarity, so pair
-- with `dot_product` / `cosine_similarity` for biomedical zero-shot
-- classification, captioning retrieval, and cross-modal search.
--
-- BiomedCLIP inherits OpenAI CLIP's image preprocessing (same mean/std)
-- but swaps the text side to BERT/WordPiece with max context 256 (the
-- "_256" in the upstream repo name).
--
-- File layout (export-biomedclip.ps1 produces this; both precisions
-- ship in one bundle so a single catalog source serves both):
--   biomedclip-vit-base-patch16-224/onnx/vision_model.onnx
--   biomedclip-vit-base-patch16-224/onnx/vision_model_fp16.onnx
--   biomedclip-vit-base-patch16-224/onnx/text_model.onnx
--   biomedclip-vit-base-patch16-224/onnx/text_model_fp16.onnx
--   biomedclip-vit-base-patch16-224/vocab.txt
--   biomedclip-vit-base-patch16-224/tokenizer.json
--
-- Two SQL-visible models share the bundle: `biomedclip_image_embed`
-- for the vision side, `biomedclip_text_embed` for the text side.
-- Both emit a 512-d L2-normalised Float32 vector in the same space.
-- ============================================================================

-- ---- Image embedding ------------------------------------------------------

CREATE OR REPLACE MODEL biomedclip_image_embed(img Image) RETURNS Float32[]
IMPLEMENTS ImageEmbedder
USING 'biomedclip-vit-base-patch16-224/2026-06-09/onnx/vision_model.onnx'
AS BEGIN
  -- BiomedCLIP uses OpenAI CLIP's image normalisation stats unchanged —
  -- mean [0.48145466, 0.4578275, 0.40821073], std
  -- [0.26862954, 0.26130258, 0.27577711]. ViT-B/16 still reads 224x224.
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [224::Int32, 224::Int32],
    [0.48145466::Float32, 0.4578275::Float32, 0.40821073::Float32],
    [0.26862954::Float32, 0.26130258::Float32, 0.27577711::Float32]);
  -- Vision encoder output: [1, 512] image_embeds (the pooled patch token
  -- after the projection head; pre-normalisation).
  DECLARE image_embeds Float32[] = infer(
    tensor,
    [1::Int32, 3::Int32, 224::Int32, 224::Int32]);
  -- Project to the unit sphere so cosine similarity = dot product
  -- between matching image-text pairs.
  RETURN l2_normalize(image_embeds)
END;

-- ---- Text embedding -------------------------------------------------------

CREATE OR REPLACE MODEL biomedclip_text_embed(text String) RETURNS Float32[]
IMPLEMENTS TextEmbedder
USING 'biomedclip-vit-base-patch16-224/2026-06-09/onnx/text_model.onnx'
AS BEGIN
  -- WordPiece encode with [CLS]/[SEP]. vocab.txt sits at the bundle root,
  -- one level up from the ONNX file. max_length caps the sequence at the
  -- 256-row position-embedding table (the "_256" in the upstream repo name);
  -- longer inputs would index past the end and abort inside the ONNX
  -- embeddings layer.
  DECLARE encoded Struct = tokenizer.encode_bert(text, '../vocab.txt', max_length => 256);
  -- The text tower was exported with a single ONNX input (input_ids); no
  -- padding is used so attention_mask isn't required at the wire boundary.
  DECLARE input_ids Int64[] = encoded['input_ids'];
  DECLARE n Int32 = cardinality(input_ids);
  -- Text encoder output: [1, 512] text_embeds (CLS pooled + projection).
  DECLARE text_embeds Float32[] = infer(
    input_ids,
    [1::Int32, n]);
  RETURN l2_normalize(text_embeds)
END
