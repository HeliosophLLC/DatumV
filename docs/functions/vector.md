---
title: Vector Functions
category: vector
---

# Vector Functions

## Reductions

### vec_sum

`vec_sum(x)` → Float32

Sum of all elements.

### vec_mean

`vec_mean(x)` → Float32

Mean of all elements.

```sql
SELECT vec_mean(embedding) FROM vectors
```

### vec_min

`vec_min(x)` → Float32

Minimum element.

### vec_max

`vec_max(x)` → Float32

Maximum element.

### vec_std

`vec_std(x)` → Float32

Population standard deviation.

```sql
SELECT vec_std(features) FROM vectors
```

### vec_var

`vec_var(x)` → Float32

Population variance.

### vec_median

`vec_median(x)` → Float32

Median.

### vec_argmin

`vec_argmin(x)` → Int32

1-based index of the minimum element. Ties resolve to the lowest index. Empty input raises.

### vec_argmax

`vec_argmax(x)` → Int32

1-based index of the maximum element. Ties resolve to the lowest index. Empty input raises.

### vec_norm

`vec_norm(x, [p])` → Float32

Lp norm (default p=2). p=∞ for max-norm.

```sql
SELECT vec_norm(embedding) FROM vectors
```

### vec_count_nonzero

`vec_count_nonzero(x)` → Float32

Count of non-zero elements.

### vec_any

`vec_any(x)` → Float32

1 if any element is non-zero, else 0.

### vec_all

`vec_all(x)` → Float32

1 if all elements are non-zero, else 0.

### vec_product

`vec_product(x)` → Float32

Product of all elements.

## Manipulation

### vec

`vec(a, b, ...)` → Float32[]

Construct a vector from scalars and/or vectors. Scalars contribute one element; vectors are flattened in order.

### vec_slice

`vec_slice(vec, start, len)` → Float32[]

Extract sub-vector by 1-based position and length. Right edge clamps: over-length requests truncate to whatever fits.

```sql
SELECT vec_slice(embedding, 1, 128) AS half FROM data
```

### vec_concat

`vec_concat(v1, v2, ...)` → Float32[]

Concatenate two or more vectors.

### vec_reverse

`vec_reverse(vec)` → Float32[]

Reverse element order.

### vec_sort

`vec_sort(vec)` → Float32[]

Sort ascending (returns copy).

```sql
SELECT vec_sort(scores) FROM data
```

### vec_unique

`vec_unique(vec)` → Float32[]

Unique elements preserving first-occurrence order.

### vec_pad

`vec_pad(vec, len, fill)` → Float32[]

Pad vector to target length with fill value.

### vec_repeat

`vec_repeat(vec, count)` → Float32[]

Repeat vector n times.

### linspace

`linspace(start, stop, n)` → Float32[]

Generate n evenly spaced values from start to stop.

### arange

`arange(start, stop, step)` → Float32[]

Generate values with fixed step (excludes stop).

## Distance & Similarity

### cosine_similarity

`cosine_similarity(a, b)` → Float32

Cosine similarity [-1, 1] between two vectors.

```sql
SELECT cosine_similarity(query_vec, doc_vec) AS similarity FROM search_results
```

### euclidean_distance

`euclidean_distance(a, b)` → Float32

Euclidean (L2) distance between two vectors.

### manhattan_distance

`manhattan_distance(a, b)` → Float32

Manhattan (L1) distance between two vectors.

### dot_product

`dot_product(a, b)` → Float32

Dot product of two Float32 vectors of equal length. Equivalent to `cosine_similarity` when both inputs are unit-normalised, and the preferred similarity primitive for pre-normalised embedding stores where the per-row sqrt is wasted work.

```sql
SELECT dot_product(query_vec, doc_vec) AS score FROM search_results
```

### hamming_distance

`hamming_distance(a, b)` → Float32

Hamming distance between two strings.

## Dimensionality Reduction

### pca_project

`pca_project(model, vec)` → Float32[]

Projects a vector into the k-dimensional space of a [pca_fit_agg](aggregate.md#pca_fit_agg) model: subtracts the model's `mean`, then dots the centered vector with each row of `components`. Returns the k projection coordinates.

Model fields are resolved by name, so any struct carrying `mean` and `components` works — including models stored in a table and loaded later. The vector's dimensionality must match the model's; mismatches raise. Null model or vector returns null.

```sql
-- Fit once over the group, project every row to 2-D
SELECT id, pca_project(m, embedding) AS xy
FROM (SELECT id, embedding, pca_fit_agg(embedding, 2) OVER () AS m FROM docs) s
```

## See Also

- [Aggregate Functions](aggregate.md) -- pca_fit_agg for fitting the PCA model consumed by pca_project
- [Numeric Functions](numeric.md) -- softmax, l2_normalize, and arithmetic operations applicable to vectors
- [ML Activation Functions](activation.md) -- element-wise activations for neural-network feature engineering
- [Image Functions](image.md) -- image_to_tensor_hwc and image_to_tensor_chw for converting images to tensors
