---
title: Random & Sampling Functions
category: random
---

# Random & Sampling Functions

### random

`random()` → Float64

`random(min, max)` → Int64 or Float64

`random(min, max, seed)` → Int64 or Float64

Uniform random number. The no-arg form returns Float64 in `[0, 1)`. The bounded forms return Int64 in `[min, max)` when both bounds are integer-family kinds, otherwise Float64 in `[min, max)`. The three-argument form is deterministic for the given integer seed. Null arguments propagate to a null result.

### hash_split

`hash_split(key, seed)` → Float32

Deterministic float in `[0, 1)` from key and seed (XxHash64). Same (key, seed) pair always produces the same value. Enables reproducible train/val/test splits via `WHERE hash_split(id, 42) < 0.8`.

```sql
-- Reproducible 80/10/10 train/val/test split
SELECT *,
  CASE
    WHEN hash_split(id, 42) < 0.8 THEN 'train'
    WHEN hash_split(id, 42) < 0.9 THEN 'val'
    ELSE 'test'
  END AS split
FROM dataset
```

### random_normal

`random_normal(mean, stddev)` → Float32

`random_normal(mean, stddev, seed)` → Float32

Sample from normal distribution N(mean, stddev) via Box-Muller. The three-argument form is deterministic for the given integer seed.

### random_truncated_normal

`random_truncated_normal(mean, stddev, min, max)` → Float32

`random_truncated_normal(mean, stddev, min, max, seed)` → Float32

Sample from N(mean, stddev), rejection-sampled to `[min, max]`. After 1000 rejections, falls back to a single clamped sample.

### random_log_normal

`random_log_normal(mean, stddev)` → Float32

`random_log_normal(mean, stddev, seed)` → Float32

Sample from log-normal: `exp(N(mean, stddev))`. Always positive.

### random_exponential

`random_exponential(rate)` → Float32

`random_exponential(rate, seed)` → Float32

Sample from exponential distribution with given rate. Useful for modelling inter-arrival times.

### random_beta

`random_beta(alpha, beta)` → Float32

`random_beta(alpha, beta, seed)` → Float32

Sample from Beta(α, β) distribution. Result lies in `[0, 1]`. Useful for priors over probabilities and mixup augmentation.

### random_poisson

`random_poisson(lambda)` → Int32

`random_poisson(lambda, seed)` → Int32

Sample from Poisson(λ) distribution as a non-negative integer count.

```sql
-- Synthetic count data
SELECT random_poisson(5) AS event_count FROM generate_series(1, 1000)
```

### random_boolean

`random_boolean(probability)` → Boolean

`random_boolean(probability, seed)` → Boolean

Bernoulli trial — returns true with probability `p` in `[0, 1]`.

```sql
-- Random dropout mask
SELECT iif(random_boolean(0.1), 0, value) AS dropped
FROM activations
```

### random_categorical

`random_categorical(weights)` → Int32

`random_categorical(weights, seed)` → Int32

Draws a zero-based category index from a `Float32[]` of non-negative weights. Weights need not sum to 1 — they are normalised internally. The discrete cousin of `random_choice`: same idea but with caller-supplied probabilities instead of uniform.

```sql
-- 70/20/10 class split for synthetic labels:
SELECT random_categorical([7.0::Float32, 2.0::Float32, 1.0::Float32]) AS class_id
FROM generate_series(1, 10000)
```

Empty weights, any negative weight, or weights that sum to zero raise an error. Null weights or null seed propagate to a null result.

### random_vector

`random_vector(length)` → Float32[]

`random_vector(length, seed)` → Float32[]

Returns a `Float32[]` of the given length filled with uniform random values in `[0, 1)`. The two-argument form is deterministic for the given integer seed.

### random_normal_vector

`random_normal_vector(length, mean, stddev)` → Float32[]

`random_normal_vector(length, mean, stddev, seed)` → Float32[]

Returns a `Float32[]` of the given length filled with samples from N(mean, stddev). Useful for adding Gaussian noise to embeddings.

```sql
-- Add Gaussian noise to a 768-dim embedding (axpy: y + a * x):
SELECT array_axpy(embedding, 1.0::Float32,
                  random_normal_vector(768, 0.0::Float32, 0.01::Float32)) AS augmented
FROM features
```

### random_choice

`random_choice(array)` → element kind

`random_choice(array, seed)` → element kind

Returns one element of an array selected uniformly at random. The element kind matches the array's element kind. The two-argument form is deterministic for the given integer seed. An empty array or null input yields a null result of the element kind.

## See Also

- [Numeric Functions](numeric.md) -- arithmetic and normalization functions for processing sampled values
- [Vector & Tensor Functions](vector.md) -- vector manipulation
- [SQL Reference](../sql/tablesample.md) -- TABLESAMPLE clause for row-level sampling
