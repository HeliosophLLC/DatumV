-- EMNIST split recipe. Shared by every emnist_* variant. Reads the images
-- IDX ($images) and labels IDX ($labels) — the same MNIST-style ubyte layout
-- open_idx_* already handles — and joins by item index so each row carries a
-- 28×28 grayscale glyph alongside its integer class label.
--
-- EMNIST stores its glyphs transposed relative to display orientation: the
-- source pixels are rotated 90° and mirrored, a quirk of the NIST → MNIST
-- conversion. image_transpose() reflects each glyph back across the main
-- diagonal so the ingested image is upright. The label vocabulary depends on
-- the split (balanced/byclass: 0-indexed; letters: 1..26) — see the card for
-- the class → character mapping.
SELECT
    i.idx AS idx,
    image_transpose(i.image) AS img,
    l.label AS label
FROM open_idx_images($images) AS i
JOIN open_idx_labels($labels) AS l ON i.idx = l.idx
