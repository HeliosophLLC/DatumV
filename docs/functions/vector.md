---
title: Vector & Tensor Functions
category: vector
---

# Vector & Tensor Functions

[← Back to Functions](string.md) · [SQL Reference](../sql/select.md) · [Compute Backend](../compute.md)

## Reductions

### vec_sum

`vec_sum(x)` → Float32 | QU: 2

Sum of all elements.

### vec_mean

`vec_mean(x)` → Float32 | QU: 2

Mean of all elements.

```sql
SELECT vec_mean(embedding) FROM vectors
```

### vec_min

`vec_min(x)` → Float32 | QU: 2

Minimum element.

### vec_max

`vec_max(x)` → Float32 | QU: 2

Maximum element.

### vec_std

`vec_std(x)` → Float32 | QU: 2

Population standard deviation.

```sql
SELECT vec_std(features) FROM vectors
```

### vec_var

`vec_var(x)` → Float32 | QU: 2

Population variance.

### vec_median

`vec_median(x)` → Float32 | QU: 2

Median.

### vec_argmin

`vec_argmin(x)` → Float32 | QU: 2

Index of minimum element.

### vec_argmax

`vec_argmax(x)` → Float32 | QU: 2

Index of maximum element.

### vec_norm

`vec_norm(x, [p])` → Float32 | QU: 2

Lp norm (default p=2). p=∞ for max-norm.

```sql
SELECT vec_norm(embedding) FROM vectors
```

### vec_count_nonzero

`vec_count_nonzero(x)` → Float32 | QU: 2

Count of non-zero elements.

### vec_any

`vec_any(x)` → Float32 | QU: 2

1 if any element is non-zero, else 0.

### vec_all

`vec_all(x)` → Float32 | QU: 2

1 if all elements are non-zero, else 0.

### vec_product

`vec_product(x)` → Float32 | QU: 2

Product of all elements.

## Tensor Introspection

### rank

`rank(x)` → Float32 | QU: 1

Number of dimensions. Vector=1, Matrix=2, Tensor=N.

```sql
SELECT rank(weights) AS ndim FROM features
```

### rdim

`rdim(x, axis)` → Float32 | QU: 1

Size of a specific dimension.

```sql
SELECT rdim(weights, 0) AS rows FROM features
```

### shape

`shape(x)` → Vector | QU: 1

All dimension sizes as a Vector.

```sql
SELECT shape(weights) AS dims FROM features
```

## Manipulation

### vec

`vec(a, b, ...)` → Vector | QU: 2

Construct a vector from scalars and/or vectors. Scalars contribute one element; vectors are flattened in order.

### tensor

`tensor(v1, v2, ...)` → Matrix | QU: 2

Stack two or more equal-length vectors as rows into a Matrix with shape [N, M].

### vec_slice

`vec_slice(vec, start, len)` → Vector | QU: 2

Extract sub-vector by position and length.

```sql
SELECT vec_slice(embedding, 0, 128) AS half FROM data
```

### vec_concat

`vec_concat(v1, v2, ...)` → Vector | QU: 2

Concatenate two or more vectors.

### vec_reverse

`vec_reverse(vec)` → Vector | QU: 2

Reverse element order.

### vec_sort

`vec_sort(vec)` → Vector | QU: 2

Sort ascending (returns copy).

```sql
SELECT vec_sort(scores) FROM data
```

### vec_unique

`vec_unique(vec)` → Vector | QU: 2

Unique elements preserving first-occurrence order.

### vec_flatten

`vec_flatten(x)` → Vector | QU: 2

Flatten Matrix/Tensor to Vector.

### vec_pad

`vec_pad(vec, len, fill)` → Vector | QU: 2

Pad vector to target length with fill value.

### vec_repeat

`vec_repeat(vec, count)` → Vector | QU: 2

Repeat vector n times.

### linspace

`linspace(start, stop, n)` → Vector | QU: 2

Generate n evenly spaced values from start to stop.

### arange

`arange(start, stop, step)` → Vector | QU: 2

Generate values with fixed step (excludes stop).

## Distance & Similarity

### cosine_similarity

`cosine_similarity(a, b)` → Float32 | QU: 2

Cosine similarity [-1, 1] between two vectors.

```sql
SELECT cosine_similarity(query_vec, doc_vec) AS similarity FROM search_results
```

### euclidean_distance

`euclidean_distance(a, b)` → Float32 | QU: 2

Euclidean (L2) distance between two vectors.

### manhattan_distance

`manhattan_distance(a, b)` → Float32 | QU: 2

Manhattan (L1) distance between two vectors.

### dot_product

`dot_product(a, b)` → Float32 | QU: 2

Dot product of two Float32 vectors of equal length. Equivalent to `cosine_similarity` when both inputs are unit-normalised, and the preferred similarity primitive for pre-normalised embedding stores where the per-row sqrt is wasted work.

```sql
SELECT dot_product(query_vec, doc_vec) AS score FROM search_results
```

### hamming_distance

`hamming_distance(a, b)` → Float32 | QU: 2

Hamming distance between two strings.

## See Also

- [Numeric Functions](numeric.md) -- softmax, l2_normalize, and arithmetic operations applicable to vectors
- [ML Activation Functions](activation.md) -- element-wise activations for neural-network feature engineering
- [Image Functions](image.md) -- image_to_tensor_hwc and image_to_tensor_chw for converting images to tensors
