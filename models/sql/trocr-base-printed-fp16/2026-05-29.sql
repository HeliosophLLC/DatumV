-- ============================================================================
-- TrOCR Base Printed fp16 — half-precision sibling of trocr-base-printed
-- ============================================================================
--
-- Catalog id:  trocr-base-printed-fp16   (models/catalog.json)
-- License:     MIT
-- Upstream:    Xenova/trocr-base-printed (same repo, fp16-quantized weights)
--
-- Identical architecture and tokenizer to the fp32 entry; only the ONNX
-- file names differ (`_fp16` suffix on encoder + decoder). ~half the
-- on-disk size with negligible accuracy loss for printed-text OCR. On
-- GPU / NPU with native fp16, expect a modest speedup; on CPU runtimes
-- that upcast fp16 → fp32, expect identical latency to fp32.
--
-- See `trocr-base-printed.sql` for the pipeline walkthrough — the only
-- substantive difference here is the two encoder/decoder file paths.
-- ============================================================================

CREATE OR REPLACE MODEL trocr_printed_fp16(img Image) RETURNS String
IMPLEMENTS TextRecognizer
USING 'trocr-base-printed-fp16/2026-05-29/onnx/encoder_model_fp16.onnx' AS encoder,
      'trocr-base-printed-fp16/onnx/decoder_model_merged_fp16.onnx' AS decoder
AS BEGIN
  DECLARE tensor Float32[] = image_to_tensor_chw(
    img,
    [CAST(384 AS Int32), CAST(384 AS Int32)],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)]);

  DECLARE encoder_features Float32[] = infer(
    'encoder',
    tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(384 AS Int32), CAST(384 AS Int32)]);

  DECLARE token_ids Int64[] = decode_seq2seq(
    'decoder',
    encoder_features,
    NULL,
    [CAST(2 AS Int64)],
    CAST(2 AS Int64),
    CAST(20 AS Int32),
    true);

  DECLARE raw String = tokenizer.decode_bpe(
    token_ids,
    '../vocab.json',
    '../merges.txt');
  RETURN tokenizer.byte_level_decode(raw)
END
