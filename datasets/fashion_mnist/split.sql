-- Fashion-MNIST split recipe. Same shape as MNIST — images IDX joined
-- to labels IDX by item index. Labels are 0–9 referring to the ten
-- garment categories (T-shirt/top, Trouser, Pullover, Dress, Coat,
-- Sandal, Shirt, Sneaker, Bag, Ankle boot).
SELECT
    i.idx AS idx,
    i.image AS img,
    l.label AS label
FROM open_idx_images($images) AS i
JOIN open_idx_labels($labels) AS l ON i.idx = l.idx
