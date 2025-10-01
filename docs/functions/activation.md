---
title: ML Activation Functions
category: activation
---

# ML Activation Functions

[← Back to Functions](string.md) · [SQL Reference](../sql/select.md) · [Compute Backend](../compute.md)

### sigmoid

`sigmoid(x)` → Float32 | QU: 1

Logistic sigmoid σ(x) = 1/(1+e⁻ˣ).

```sql
SELECT sigmoid(score) FROM model_outputs
```

### relu

`relu(x)` → Float32 | QU: 1

Rectified Linear Unit max(0, x).

```sql
SELECT relu(raw_output) FROM model_outputs
```

### selu

`selu(x)` → Float32 | QU: 1

Scaled Exponential Linear Unit.

### gelu

`gelu(x)` → Float32 | QU: 1

Gaussian Error Linear Unit (fast approximation).

```sql
SELECT gelu(activation) FROM model_outputs
```

### swish

`swish(x)` → Float32 | QU: 1

Swish activation x·σ(x).

### softplus

`softplus(x)` → Float32 | QU: 1

Softplus ln(1+eˣ).

### softsign

`softsign(x)` → Float32 | QU: 1

Softsign x/(1+|x|).

### mish

`mish(x)` → Float32 | QU: 1

Mish activation x·tanh(softplus(x)).

### hard_sigmoid

`hard_sigmoid(x)` → Float32 | QU: 1

Piecewise linear approximation of sigmoid.

### hard_swish

`hard_swish(x)` → Float32 | QU: 1

Hard Swish x·hard_sigmoid(x).

### leaky_relu

`leaky_relu(x, [alpha])` → Float32 | QU: 1

Leaky ReLU with configurable slope (default α=0.01).

### elu

`elu(x, [alpha])` → Float32 | QU: 1

Exponential Linear Unit (default α=1.0).

## See Also

- [Numeric Functions](numeric.md) -- softmax, log_softmax, and l2_normalize for output normalization
- [Vector & Tensor Functions](vector.md) -- vector reductions and manipulation for working with activation outputs
- [Compute Backend -- Resource Governance](../compute.md#resource-governance) -- QU cost tracking and budget enforcement
