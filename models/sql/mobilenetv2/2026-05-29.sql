-- ============================================================================
-- MobileNetV2 — Apache-2.0 ImageNet-1K classifier.
-- ============================================================================
--
-- Catalog id:  mobilenetv2        (models/catalog.json)
-- ONNX file:   mobilenetv2-12.onnx
-- License:     Apache-2.0
-- Upstream:    https://github.com/onnx/models/tree/main/validated/vision/classification/mobilenet
--
-- 3.5M params. Tiny, fast, CPU-friendly. 224×224 stretch-resize input,
-- ImageNet-mean/std normalisation, 1000-way softmax over the standard
-- ImageNet-1K vocabulary.
--
-- Pipeline:
--   1. image_to_tensor_chw  — stretch-resize to 224×224, normalise with
--                             ImageNet mean/std, pack NCHW Float32.
--   2. infer                — single ONNX dispatch; output is a [1, 1000]
--                             logit vector flattened.
--   3. softmax + argmax     — pick the top class id + its probability.
--   4. read_string_list     — load imagenet-classes.json from the model's
--                             USING directory (cached process-wide).
--   5. RETURN {label, score} — ScoredLabel struct: human-readable class
--                              name + softmax probability.
-- ============================================================================

CREATE OR REPLACE MODEL mobilenetv2(img Image) RETURNS ScoredLabel
IMPLEMENTS LabeledImageClassifier
USING 'mobilenetv2/2026-05-29/mobilenetv2-12.onnx'
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(img, [224, 224], imagenet_mean(), imagenet_std());
  DECLARE logits Float32[] = infer(tensor);
  DECLARE probs  Float32[] = softmax(logits);
  DECLARE top    Int32     = argmax(probs);
  DECLARE labels Array<String> = read_string_list('imagenet-classes.json');
  RETURN {label: labels[top], score: probs[top]}
END
