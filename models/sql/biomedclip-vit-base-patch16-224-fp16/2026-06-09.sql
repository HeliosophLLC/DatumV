-- ============================================================================
-- BiomedCLIP fp16 — half-precision biomedical image-text embeddings, MIT.
-- ============================================================================
--
-- Catalog id:  biomedclip-vit-base-patch16-224-fp16  (models/catalog.json)
-- ONNX files:  onnx/vision_model_fp16.onnx + onnx/text_model_fp16.onnx
-- License:     MIT
-- Upstream:    https://huggingface.co/microsoft/BiomedCLIP-PubMedBERT_256-vit_base_patch16_224
--
-- Same architecture and embedding space as the fp32 build — only the
-- internal weights and activations run in half precision. Wire-boundary
-- tensors stay Float32 (the export keeps `keep_io_types=True`), so the
-- SQL bodies are byte-identical to the fp32 sibling apart from the ONNX
-- file paths and the model identifier suffix.
-- ============================================================================

-- ---- Image embedding ------------------------------------------------------

CREATE OR REPLACE MODEL biomedclip_image_embed_fp16(img Image) RETURNS Float32[]
IMPLEMENTS ImageEmbedder
USING 'biomedclip-vit-base-patch16-224-fp16/2026-06-09/onnx/vision_model_fp16.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [224::Int32, 224::Int32],
    [0.48145466::Float32, 0.4578275::Float32, 0.40821073::Float32],
    [0.26862954::Float32, 0.26130258::Float32, 0.27577711::Float32]);
  DECLARE image_embeds Float32[] = infer(
    tensor,
    [1::Int32, 3::Int32, 224::Int32, 224::Int32]);
  RETURN l2_normalize(image_embeds)
END;

-- ---- Text embedding -------------------------------------------------------

CREATE OR REPLACE MODEL biomedclip_text_embed_fp16(text String) RETURNS Float32[]
IMPLEMENTS TextEmbedder
USING 'biomedclip-vit-base-patch16-224-fp16/2026-06-09/onnx/text_model_fp16.onnx'
AS BEGIN
  -- max_length caps the sequence at the 256-row position-embedding table.
  DECLARE encoded Struct = tokenizer.encode_bert(text, '../vocab.txt', max_length => 256);
  DECLARE input_ids Int64[] = encoded['input_ids'];
  DECLARE n Int32 = cardinality(input_ids);
  DECLARE text_embeds Float32[] = infer(
    input_ids,
    [1::Int32, n]);
  RETURN l2_normalize(text_embeds)
END
