-- ============================================================================
-- Toxic-BERT — 6-label toxicity classifier, Apache-2.0.
-- ============================================================================
--
-- Catalog id:  toxic-bert
-- ONNX file:   onnx/model.onnx
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/unitary/toxic-bert
--
-- Unitary's BERT-base fine-tuned on the Jigsaw toxic-comment dataset.
-- Six independent toxicity labels (not mutually exclusive): toxic,
-- severe_toxic, obscene, threat, insult, identity_hate. Each label has
-- its own sigmoid probability — emits any label whose probability is at
-- least the threshold (default 0.5).
--
-- Pipeline:
--   1. tokenizer.encode_bert   — WordPiece tokenize with [CLS]/[SEP],
--                                emit Struct{input_ids, attention_mask,
--                                token_type_ids}.
--   2. infer (multi-input)     — three-input BERT ONNX dispatch.
--   3. multilabel_classify     — sigmoid + per-label threshold + zip with
--                                labels into Array<ScoredLabel>. Surviving
--                                labels keep input order; downstream
--                                consumers can ORDER BY score DESC if
--                                ranked output is desired.
-- ============================================================================

CREATE OR REPLACE MODEL toxic_bert(
  text      String,
  threshold Float32 = CAST(0.5 AS Float32)
    CHECK (threshold BETWEEN 0.0 AND 1.0) STEP 0.05
    COMMENT 'Per-label sigmoid-probability floor for emitting that label.'
) RETURNS Array<ScoredLabel>
IMPLEMENTS LabeledTextMultiClassifier
USING 'toxic-bert/2026-05-29/onnx/model.onnx'
AS BEGIN
  -- vocab.txt sits at the catalog root, one directory up from the ONNX file.
  DECLARE encoded Struct = tokenizer.encode_bert(text, '../vocab.txt');
  DECLARE n Int32 = cardinality(encoded['input_ids']);
  DECLARE logits Float32[] = infer(
    encoded,
    {
      input_ids:      [CAST(1 AS Int32), n],
      attention_mask: [CAST(1 AS Int32), n],
      token_type_ids: [CAST(1 AS Int32), n]
    });
  -- Unitary's canonical label ordering (matches the Jigsaw competition
  -- column order). multilabel_classify maps logits[i] to labels[i].
  DECLARE labels Array<String> = ['toxic', 'severe_toxic', 'obscene', 'threat', 'insult', 'identity_hate'];
  RETURN multilabel_classify(logits, labels, threshold)
END
