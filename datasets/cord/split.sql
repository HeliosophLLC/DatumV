-- CORD receipt recipe. Shared by cord_train / cord_validation / cord_test.
--
-- Joins the receipt-image bag ($images => a zip of JPEGs) with the per-receipt
-- annotation stream ($annotations => gzipped JSONL: image_file, width, height,
-- ground_truth). The join key is the exact zip entry name, carried verbatim in
-- each JSONL row's image_file, so no filename parsing is needed.
--
-- ground_truth is CORD's full annotation, carried through as a Json cell:
--   * gt_parse    — the structured parse (menu lines, sub_total, total) used
--                   for key-information-extraction / post-OCR parsing.
--   * valid_line  — word-level OCR ground truth: each word's text, quad box,
--                   and category. This is the OCR-detection/recognition side.
--   * meta / roi  — image metadata + region of interest.
-- One output row per receipt: the decoded image plus its dimensions and the
-- ground_truth bundle.
SELECT
    a.image_file          AS receipt_id,
    image_decode(i.bytes) AS img,
    a.width               AS width,
    a.height              AS height,
    a.ground_truth        AS ground_truth
FROM open_jsonl($annotations) AS a
JOIN open_archive($images) AS i ON i.path = a.image_file
