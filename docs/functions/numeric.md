---
title: Numeric Functions
category: numeric
---

# Numeric Functions

## Normalization

### min_max_normalize 

`min_max_normalize(val[, min, max])` → Float32

Normalize into the `[0, 1]` range via `(val - min) / (max - min)`. The
single-argument form accepts any integer kind and uses its natural range
(`UInt8` → 0..255, `Int16` → -32768..32767, etc.); `Float32` / `Decimal`
inputs require explicit `min` and `max`. Array overloads apply the transform
element-wise.

```sql
SELECT id, min_max_normalize(score, 0, 100) AS norm_score FROM data
```

### clamp

`clamp(val, min, max)` → Float32

Clamp value to [min, max]. Accepts a Float32 scalar or Float32[] vector;
multi-dim array shape is preserved. Throws when `min > max` or either bound
is NaN.

### denormalize

`denormalize(val, min, max)` → Float32

Inverse of `min_max_normalize`: maps a normalized `[0, 1]` value back to its
original range via `val * (max - min) + min`. Accepts a `Float32` scalar or
`Float32[]` vector.

## Basic Arithmetic

### abs

`abs(x)` → Float32

Absolute value. Element-wise for vectors/matrices/tensors.

```sql
SELECT abs(delta) FROM metrics
```

### sign

`sign(x)` → Float32

Returns -1, 0, or 1. Element-wise.

### negate

`negate(x)` → Float32

Negation (-x). Element-wise.

For binary arithmetic on scalars, vectors, matrices, and tensors, use the
infix operators `+`, `-`, `*`, `/`, and `%`. They broadcast scalars across
element-wise operands and are the preferred surface; named function forms
are not exposed.

## Powers, Roots & Logarithms

### sqrt

`sqrt(x)` → Float32

Square root.

```sql
SELECT sqrt(variance) FROM metrics
```

### cbrt

`cbrt(x)` → Float32

Cube root.

### square

`square(x)` → Float32

Square (x²).

### exp

`exp(x)` → Float32

Natural exponential (eˣ).

### exp2

`exp2(x)` → Float32

Base-2 exponential (2ˣ).

### ln

`ln(x)` → Float32

Natural logarithm.

### log2

`log2(x)` → Float32

Base-2 logarithm.

### log10

`log10(x)` → Float32

Base-10 logarithm.

### pow

`pow(base, exp)` → Float32

Power function. Element-wise with broadcast.

```sql
SELECT pow(base_val, 2) FROM metrics
```

### log

`log(x, base)` → Float32

Logarithm with custom base.

## Rounding & Quantization

### ceil

`ceil(x)` → Float32

Round up to nearest integer. Also available as `ceiling`.

### floor

`floor(x)` → Float32

Round down to nearest integer.

### truncate

`truncate(x)` → Float32

Remove fractional part toward zero. Also available as `trunc` (PostgreSQL spelling).

### round

`round(x, [decimals])` → Float32

Round to nearest integer or specified decimal places.

### quantize

`quantize(x, step)` → Float32

Round a Float32 scalar or Float32[] vector to the nearest multiple of `step`
(midpoint away from zero). `step` must be positive and finite.

### bucketize

`bucketize(val, boundaries)` → Int32

Assigns a Float32 value to a bucket index given a strictly-ascending Float32
boundary vector. Returns `0` below the first boundary through
`boundaries.length` above the last. Half-open: a value equal to a boundary
falls into the right-hand bucket.

### clip

`clip(x, min, max)` → Float32

Clip to range (alias for clamp).

## Softmax & Normalization

### softmax

`softmax(vec)` → Vector

Numerically stable softmax producing a probability vector.

```sql
SELECT softmax(logits) AS probabilities FROM predictions
```

### log_softmax

`log_softmax(vec)` → Vector

Log-softmax via log-sum-exp trick.

### l2_normalize

`l2_normalize(vec)` → Vector

L2 normalize to unit length.

## See Also

- [Trigonometric & Hyperbolic Functions](trigonometric.md) -- trig, inverse trig, hyperbolic, and angle conversion functions
- [ML Activation Functions](activation.md) -- sigmoid, ReLU, GELU, and other neural-network activations