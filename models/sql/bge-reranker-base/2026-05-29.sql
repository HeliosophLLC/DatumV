-- ============================================================================
-- BGE Reranker (base) — XLM-RoBERTa cross-encoder for query/passage relevance.
-- ============================================================================
--
-- Catalog id:  bge-reranker-base
-- ONNX file:   onnx/model.onnx
-- License:     MIT
-- Upstream:    https://huggingface.co/BAAI/bge-reranker-base
--              https://huggingface.co/Xenova/bge-reranker-base (ONNX export)
--
-- BAAI's XLM-RoBERTa-base cross-encoder fine-tuned for retrieval reranking.
-- Scores the relevance of a passage to a query as a single Float32 logit —
-- higher is more relevant. Use after a fast bi-encoder retrieval step (e.g.
-- models.all_minilm_l6_v2 + cosine_similarity) to re-order the top-k
-- candidates with substantially better accuracy than embedding similarity
-- alone. The XLM-RoBERTa backbone makes it multilingual.
--
-- Output range. The raw logit is unbounded; ordering by it produces the
-- right ranking. If you want a 0-1 probability, wrap the call in
-- sigmoid() — the BAAI README's recommended mapping.
--
-- Pipeline:
--   1. tokenizer.encode_xlm_roberta_pair — Unigram (SentencePiece) encode of
--                                   "<s> query </s></s> passage </s>" with
--                                   the all-1s attention_mask. XLM-RoBERTa is
--                                   not a BERT-family WordPiece model: it has
--                                   no token_type_ids (type_vocab_size = 1)
--                                   and its tokenizer is a SentencePiece
--                                   Unigram model (tokenizer.json), not a
--                                   vocab.txt.
--   2. infer (multi-input)        — two-input XLM-RoBERTa ONNX dispatch
--                                   (input_ids + attention_mask). Output is
--                                   [1, 1] = single relevance logit;
--                                   shape-product 1 surfaces as a Float32
--                                   scalar.
--
-- Example: rerank top-10 hits from a bi-encoder.
--
--   WITH bi AS (
--     SELECT id, body,
--            cosine_similarity(models.all_minilm_l6_v2(body),
--                              models.all_minilm_l6_v2(@query)) AS sim
--     FROM passages
--     ORDER BY sim DESC LIMIT 10)
--   SELECT id, models.bge_reranker_base(@query, body) AS score
--   FROM bi
--   ORDER BY score DESC;
-- ============================================================================

CREATE OR REPLACE MODEL bge_reranker_base(
  query   String,
  passage String
) RETURNS Float32
IMPLEMENTS TextPairScorer
USING 'bge-reranker-base/2026-05-29/onnx/model.onnx'
AS BEGIN
  -- tokenizer.json sits at the bundle root (sibling to the onnx/ folder).
  -- max_length caps the assembled pair at the 512-slot position-embedding
  -- table; longest-first truncation trims the longer side first.
  DECLARE encoded Struct = tokenizer.encode_xlm_roberta_pair(query, passage, '../tokenizer.json', max_length => 512);
  DECLARE n Int32 = cardinality(encoded['input_ids']);
  RETURN infer(
    encoded,
    {
      input_ids:      [1::Int32, n],
      attention_mask: [1::Int32, n]
    })
END
