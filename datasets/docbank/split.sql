-- DocBank split recipe. Shared by docbank_train and docbank_test.
--
-- Joins the page-image bag ($images => a zip of full-page PNGs) with the
-- per-page annotation stream ($annotations => gzipped JSONL, one page per
-- line with keys: page_id, image_file, width, height, text, tokens[]). The
-- join key is the exact zip entry name, carried verbatim in each JSONL row's
-- image_file, so no filename parsing is needed.
--
-- Each output row is one document page: the decoded image, the full-page
-- reading-order text (space-joined tokens — ready for an OCR-vs-truth diff),
-- the page dimensions, and the token array (each {text, x0, y0, x1, y1,
-- label, font}, bboxes normalized to 0-1000) as a Json cell for box- and
-- label-level evaluation.
SELECT
    a.page_id             AS page_id,
    image_decode(i.bytes) AS img,
    a."text"              AS "text",
    a.width               AS width,
    a.height              AS height,
    a.tokens              AS tokens
FROM open_jsonl($annotations) AS a
JOIN open_archive($images) AS i ON i.path = a.image_file
