-- ============================================================================
-- MiniLM-L6 Sentence Embeddings — 384-dim text → vector for similarity search.
-- ============================================================================
--
-- Catalog id:  all-minilm-l6-v2        (models/catalog.json)
-- ONNX file:   model.onnx
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2
--
-- BERT-family transformer encoder. Takes one text string per row and emits a
-- 384-dimensional unit-length Float32[] sentence embedding. Pair with
-- cosine_similarity() for retrieval, clustering, or de-duplication.
--
-- Pipeline:
--   1. tokenizer.encode_bert — WordPiece tokenize with [CLS]/[SEP], emit
--      Struct{input_ids, attention_mask, token_type_ids} (BERT input bundle).
--   2. infer (multi-input)  — three-input ONNX dispatch via the (Struct,
--      Struct) form; shapes are [1, seq_len] for every input.
--   3. mean_pool_masked     — average the per-token last_hidden_state along
--      seq_len, weighting by attention_mask. Standard sentence-transformers
--      pooler.
--   4. l2_normalize         — project to the unit sphere so cosine similarity
--      reduces to a dot product downstream.
-- ============================================================================

CREATE OR REPLACE MODEL all_minilm_l6_v2(text String) RETURNS Float32[]
IMPLEMENTS TextEmbedder
USING 'all-minilm-l6-v2/model.onnx'
AS BEGIN
  -- Tokenize. The function returns the canonical BERT input bundle with
  -- field names matching the ONNX input names (input_ids / attention_mask /
  -- token_type_ids) so the (Struct, Struct) infer() form lines up by name.
  DECLARE encoded Struct = tokenizer.encode_bert(text, 'vocab.txt');

  -- All three inputs share the same shape [1, seq_len]. seq_len comes from
  -- the tokenizer; we read it once off input_ids.
  DECLARE n Int32 = array_length(encoded['input_ids']);

  -- Multi-input infer: per-field tensors + per-field shapes. Every input
  -- has two dynamic dims (batch + seq_len) so explicit shapes are required.
  DECLARE last_hidden_state Float32[] = infer(
    encoded,
    {
      input_ids:      [CAST(1 AS Int32), n],
      attention_mask: [CAST(1 AS Int32), n],
      token_type_ids: [CAST(1 AS Int32), n]
    });

  -- Mean-pool the per-token embeddings along seq_len, weighting by mask.
  -- MiniLM-L6 has 384 hidden units.
  DECLARE pooled Float32[] = mean_pool_masked(
    last_hidden_state,
    encoded['attention_mask'],
    CAST(384 AS Int32));

  RETURN l2_normalize(pooled)
END
