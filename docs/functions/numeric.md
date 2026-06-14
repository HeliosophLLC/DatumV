---
title: Numeric Functions
category: numeric
---

# Numeric Functions

## Normalization

### min_max_normalize

`min_max_normalize(val, [min], [max])` → Float32 | QU: 1

Normalize to 0--1 range. Byte/byte[]: default 0--255. Float32/Vector: requires min/max.

```sql
SELECT id, min_max_normalize(score, 0, 100) AS norm_score FROM data
```

### clamp

`clamp(val, min, max)` → Float32 | QU: 1

Clamp value to [min, max]. Works on Float32, Vector, Matrix, Tensor.

### denormalize

`denormalize(val, factor)` → Float32 | QU: 1

Multiply by factor (reverse of min_max_normalize).

### reshape

`reshape(tensor, dim1, dim2, ...)` → Tensor | QU: 1

Reinterpret tensor shape without copying. Element count must match.

```sql
SELECT reshape(embedding, 16, 16) AS matrix_embed FROM features
```

## Basic Arithmetic

### abs

`abs(x)` → Float32 | QU: 1

Absolute value. Element-wise for vectors/matrices/tensors.

```sql
SELECT abs(delta) FROM metrics
```

### sign

`sign(x)` → Float32 | QU: 1

Returns -1, 0, or 1. Element-wise.

### negate

`negate(x)` → Float32 | QU: 1

Negation (-x). Element-wise.

### mod

`mod(a, b)` → Float32 | QU: 1

Modulus (a % b). Element-wise with broadcast.

### add

`add(a, b)` → Float32 | QU: 1

Addition. Element-wise with scalar broadcast.

### subtract

`subtract(a, b)` → Float32 | QU: 1

Subtraction. Element-wise with scalar broadcast.

### multiply

`multiply(a, b)` → Float32 | QU: 1

Multiplication. Element-wise with scalar broadcast.

### divide

`divide(a, b)` → Float32 | QU: 1

Division. Element-wise with scalar broadcast.

## Powers, Roots & Logarithms

### sqrt

`sqrt(x)` → Float32 | QU: 1

Square root.

```sql
SELECT sqrt(variance) FROM metrics
```

### cbrt

`cbrt(x)` → Float32 | QU: 1

Cube root.

### square

`square(x)` → Float32 | QU: 1

Square (x²).

### exp

`exp(x)` → Float32 | QU: 1

Natural exponential (eˣ).

### exp2

`exp2(x)` → Float32 | QU: 1

Base-2 exponential (2ˣ).

### ln

`ln(x)` → Float32 | QU: 1

Natural logarithm.

### log2

`log2(x)` → Float32 | QU: 1

Base-2 logarithm.

### log10

`log10(x)` → Float32 | QU: 1

Base-10 logarithm.

### pow

`pow(base, exp)` → Float32 | QU: 1

Power function. Element-wise with broadcast.

```sql
SELECT pow(base_val, 2) FROM metrics
```

### log

`log(x, base)` → Float32 | QU: 1

Logarithm with custom base.

## Rounding & Quantization

### ceil

`ceil(x)` → Float32 | QU: 1

Round up to nearest integer.

### floor

`floor(x)` → Float32 | QU: 1

Round down to nearest integer.

### truncate

`truncate(x)` → Float32 | QU: 1

Remove fractional part toward zero.

### round

`round(x, [decimals])` → Float32 | QU: 1

Round to nearest integer or specified decimal places.

### quantize

`quantize(x, step)` → Float32 | QU: 1

Round to nearest multiple of step.

### bucketize

`bucketize(val, boundaries)` → Float32 | QU: 1

Assign value to bucket index based on sorted boundary vector.

### clip

`clip(x, min, max)` → Float32 | QU: 1

Clip to range (alias for clamp).

## Softmax & Normalization

### softmax

`softmax(vec)` → Vector | QU: 2

Numerically stable softmax producing a probability vector.

```sql
SELECT softmax(logits) AS probabilities FROM predictions
```

### log_softmax

`log_softmax(vec)` → Vector | QU: 2

Log-softmax via log-sum-exp trick.

### l2_normalize

`l2_normalize(vec)` → Vector | QU: 2

L2 normalize to unit length.

## See Also

- [Trigonometric & Hyperbolic Functions](trigonometric.md) -- trig, inverse trig, hyperbolic, and angle conversion functions
- [ML Activation Functions](activation.md) -- sigmoid, ReLU, GELU, and other neural-network activations
- [Compute Backend -- Resource Governance](../compute.md#resource-governance) -- QU cost tracking and budget enforcement
