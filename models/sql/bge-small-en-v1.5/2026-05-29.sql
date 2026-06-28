-- ============================================================================
-- BGE-Small EN v1.5 — 384-dim sentence embeddings, MIT.
-- ============================================================================
--
-- Catalog id:  bge-small-en-v1.5
-- ONNX file:   onnx/model.onnx
-- License:     MIT
-- Upstream:    https://huggingface.co/BAAI/bge-small-en-v1.5
--
-- BAAI's MTEB-leaderboard-grade English embedder at MiniLM size (~33M
-- params, 384-dim output). Better retrieval accuracy than all-MiniLM-L6-v2
-- on most benchmarks at roughly the same disk + latency cost. The
-- recommended English embedding default when accuracy matters more than
-- the marginal MiniLM compatibility benefit.
--
-- Pipeline: identical to all-minilm-l6-v2 — only the ONNX path differs
-- (BGE's HF repo ships ONNX in `onnx/` subdir, vocab.txt at root). See
-- models/sql/all-minilm-l6-v2.sql for full notes.
-- ============================================================================

CREATE OR REPLACE MODEL bge_small_en_v1_5(text String) RETURNS Float32[]
IMPLEMENTS TextEmbedder
USING 'bge-small-en-v1.5/2026-05-29/onnx/model.onnx'
AS BEGIN
  -- vocab.txt sits at the catalog root (`<id>/vocab.txt`), one directory up
  -- from the ONNX file (`<id>/onnx/model.onnx`). Relative paths resolve
  -- against the model's USING directory, so `../vocab.txt` walks back up.
  -- max_length caps the sequence at BGE's 512-slot position-embedding table.
  DECLARE encoded Struct = tokenizer.encode_bert(text, '../vocab.txt', max_length => 512);
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
    CAST(384 AS Int32));
  RETURN l2_normalize(pooled)
END
