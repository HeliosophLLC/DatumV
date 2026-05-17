-- MNIST split recipe. Used by both mnist_train and mnist_test variants.
-- Opens the images IDX and labels IDX bound by the catalog as
-- $images and $labels, and joins by item index so each row carries
-- a 28×28 grayscale image alongside its digit class label.
SELECT
    i.idx AS idx,
    i.image AS img,
    l.label AS label
FROM open_idx_images($images) AS i
JOIN open_idx_labels($labels) AS l ON i.idx = l.idx
