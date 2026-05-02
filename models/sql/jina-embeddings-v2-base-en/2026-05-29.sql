-- ============================================================================
-- Jina Embeddings v2 (base-en) — 768-dim long-context sentence embeddings.
-- ============================================================================
--
-- Catalog id:  jina-embeddings-v2-base-en
-- ONNX file:   model.onnx
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/jinaai/jina-embeddings-v2-base-en
--
-- BERT-family encoder with 8K-token context (ALiBi positional encoding lets
-- it extrapolate far past the trained length). 768-dim sentence embedding;
-- pair with cosine_similarity() for similarity search. Pick this over
-- MiniLM/BGE when whole-document retrieval matters more than throughput.
--
-- Pipeline: identical to all-minilm-l6-v2 — only the embedding dim differs
-- (768 vs 384). See models/sql/all-minilm-l6-v2.sql for full notes.
-- ============================================================================

CREATE OR REPLACE MODEL jina_embeddings_v2_base_en(text String) RETURNS Float32[]
IMPLEMENTS TextEmbedder
USING 'jina-embeddings-v2-base-en/2026-05-29/model.onnx'
AS BEGIN
  -- Jina ships ONNX + vocab.txt at the repo root (no `onnx/` subdir like
  -- BGE), so the tokenizer file resolves directly relative to the model.
  DECLARE encoded Struct = tokenizer.encode_bert(text, 'vocab.txt');
  DECLARE n Int32 = cardinality(encoded['input_ids']);
  DECLARE last_hidden_state Float32[] = infer(
    encoded,
    {
      input_ids:      [1::Int32, n],
      attention_mask: [1::Int32, n],
      token_type_ids: [1::Int32, n]
    });
  DECLARE pooled Float32[] = mean_pool_masked(
    last_hidden_state,
    encoded['attention_mask'],
    768::Int32);
  RETURN l2_normalize(pooled)
END
