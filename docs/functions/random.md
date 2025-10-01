---
title: Random & Sampling Functions
category: random
---

# Random & Sampling Functions

[← Back to Functions](string.md) · [SQL Reference](../sql/select.md) · [Compute Backend](../compute.md)

### hash_split

`hash_split(key, seed)` → Float32 | QU: 1

Deterministic float in [0, 1) from key and seed (XxHash64). Same (key, seed) pair always produces the same value. Enables reproducible train/val/test splits via `WHERE hash_split(id, 42) < 0.8`.

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

### random_int

`random_int(min, max)` → Float32 | QU: 1

Random integer in [min, max] (both inclusive).

### random_range

`random_range(min, max)` → Float32 | QU: 1

Random float in [min, max).

### random_normal

`random_normal(mean, stddev)` → Float32 | QU: 1

Sample from normal distribution N(mean, stddev) via Box-Muller.

### random_boolean

`random_boolean(probability)` → Boolean | QU: 1

Bernoulli trial -- returns true with probability p in [0, 1].

```sql
-- Random dropout mask
SELECT iif(random_boolean(0.1), 0, value) AS dropped
FROM activations
```

### random_truncated_normal

`random_truncated_normal(mean, stddev, min, max)` → Float32 | QU: 1

Sample from truncated normal, rejection-sampled to [min, max].

### random_log_normal

`random_log_normal(mean, stddev)` → Float32 | QU: 1

Sample from log-normal: exp(N(mean, stddev)).

### random_exponential

`random_exponential(rate)` → Float32 | QU: 1

Sample from exponential distribution with given rate.

### random_beta

`random_beta(alpha, beta)` → Float32 | QU: 1

Sample from Beta(α, β) distribution.

### random_poisson

`random_poisson(lambda)` → Float32 | QU: 1

Sample from Poisson(λ) distribution (integer count).

```sql
-- Synthetic count data
SELECT random_poisson(5) AS event_count FROM generate_series(1, 1000)
```

### random_categorical

`random_categorical(weights)` → Float32 | QU: 2

Draw a 0-based category index from weighted probabilities (Vector).

### random_vector

`random_vector(length)` → Vector | QU: 2

Vector of uniform random floats in [0, 1).

### random_normal_vector

`random_normal_vector(length, mean, stddev)` → Vector | QU: 2

Vector of Gaussian random floats N(mean, stddev).

```sql
-- Add Gaussian noise to embeddings
SELECT embedding + random_normal_vector(768, 0, 0.01) AS augmented
FROM features
```

### random_permutation

`random_permutation(length)` → Vector | QU: 2

Random permutation of [0, length) via Fisher-Yates.

### random_choice

`random_choice(array, count)` → Array | QU: 2

Sample count elements from array without replacement.

```sql
-- Random sample of 3 tags from each row
SELECT random_choice(tags, 3) AS sampled_tags FROM articles
```

## See Also

- [Numeric Functions](numeric.md) -- arithmetic and normalization functions for processing sampled values
- [Vector & Tensor Functions](vector.md) -- vector manipulation for working with random vectors
- [SQL Reference](../sql/tablesample.md) -- TABLESAMPLE clause for row-level sampling
