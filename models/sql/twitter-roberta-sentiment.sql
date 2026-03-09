-- ============================================================================
-- Twitter-RoBERTa Sentiment — 3-class sentiment classifier, MIT.
-- ============================================================================
--
-- Catalog id:  twitter-roberta-sentiment
-- ONNX file:   onnx/model.onnx
-- License:     MIT
-- Upstream:    https://huggingface.co/cardiffnlp/twitter-roberta-base-sentiment-latest
--
-- Cardiff NLP's RoBERTa-base fine-tuned on TweetEval sentiment. Outputs
-- one of three labels: 'negative', 'neutral', 'positive'. Generalizes
-- reasonably well beyond Twitter (product reviews, news comments).
--
-- Pipeline:
--   1. tokenizer.encode_roberta — BPE tokenize with <s>/</s> wrapping,
--                                  emit Struct{input_ids, attention_mask}.
--                                  Note: RoBERTa has no token_type_ids
--                                  (unlike BERT-family encoders).
--   2. infer (multi-input)       — two-input ONNX dispatch via the
--                                  (Struct, Struct) form; shapes are
--                                  [1, seq_len] for both inputs.
--   3. softmax + argmax          — 3-class probability distribution; pick
--                                  the highest-scoring index.
--   4. RETURN {label, score}     — labels are baked into the body since
--                                  the 3-class set is fixed by the
--                                  training data (TweetEval).
-- ============================================================================

CREATE OR REPLACE MODEL twitter_roberta_sentiment(text String) RETURNS ScoredLabel
IMPLEMENTS LabeledTextClassifier
USING 'twitter-roberta-sentiment/onnx/model.onnx'
AS BEGIN
  -- tokenizer.json sits at the catalog root one directory up from
  -- the ONNX (which lives in `onnx/`), so the relative path is `../tokenizer.json`.
  DECLARE encoded Struct = tokenizer.encode_roberta(text, '../tokenizer.json');
  DECLARE n Int32 = array_length(encoded['input_ids']);
  -- Multi-input infer: RoBERTa takes input_ids + attention_mask only.
  -- Both share [1, seq_len] shape.
  DECLARE logits Float32[] = infer(
    encoded,
    {
      input_ids:      [CAST(1 AS Int32), n],
      attention_mask: [CAST(1 AS Int32), n]
    });
  DECLARE probs Float32[] = softmax(logits);
  DECLARE top Int32 = argmax(probs);
  -- TweetEval sentiment uses Cardiff NLP's canonical id→label mapping:
  --   0 = negative, 1 = neutral, 2 = positive.
  DECLARE labels Array<String> = ['negative', 'neutral', 'positive'];
  RETURN {label: labels[top], score: probs[top]}
END
