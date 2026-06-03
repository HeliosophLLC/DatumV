# ML Activation Functions

[← Back to Functions](string.md) · [SQL Reference](../sql/select.md)

All activations are element-wise and accept either a `Float32` scalar
(returning `Float32`) or a `Float32[]` vector (returning `Float32[]`).
Examples below use vector inputs; substitute a scalar column where
appropriate.

### softmax

`softmax(values FLOAT32[])` → Float32[] · `softmax(x FLOAT32)` → Float32

Numerically-stable softmax over a Float32 vector — output sums to 1.0.
The canonical normalization for classifier logits. Scalar input is the
degenerate identity that always returns 1.0.

```sql
SELECT softmax(logits) AS probs FROM model_outputs
```

### sigmoid

`sigmoid(values)` → Float32[] · `sigmoid(x)` → Float32

Logistic sigmoid σ(x) = 1/(1+e⁻ˣ). Classifier-head activation for binary
or multi-label outputs.

```sql
SELECT sigmoid(logits) FROM model_outputs
```

### relu

`relu(values)` → Float32[] · `relu(x)` → Float32

Rectified Linear Unit max(0, x).

```sql
SELECT relu(activations) FROM hidden_layer
```

### leaky_relu

`leaky_relu(values, [alpha])` → Float32[] · `leaky_relu(x, [alpha])` → Float32

Leaky ReLU with configurable negative slope (default α = 0.01).

### elu

`elu(values, [alpha])` → Float32[] · `elu(x, [alpha])` → Float32

Exponential Linear Unit (default α = 1.0).

### selu

`selu(values)` → Float32[] · `selu(x)` → Float32

Scaled Exponential Linear Unit with the self-normalizing constants from
Klambauer et al. (2017): α ≈ 1.6732632, λ ≈ 1.0507010.

### gelu

`gelu(values)` → Float32[] · `gelu(x)` → Float32

Gaussian Error Linear Unit (tanh-based fast approximation). Standard
activation in modern transformer FFN blocks.

```sql
SELECT gelu(hidden) FROM model_outputs
```

### swish

`swish(values)` → Float32[] · `swish(x)` → Float32

Swish activation x · σ(x), also known as SiLU.

### softplus

`softplus(values)` → Float32[] · `softplus(x)` → Float32

Softplus ln(1 + eˣ), a smooth ReLU approximation.

### softsign

`softsign(values)` → Float32[] · `softsign(x)` → Float32

Softsign x / (1 + |x|).

### mish

`mish(values)` → Float32[] · `mish(x)` → Float32

Mish activation x · tanh(softplus(x)).

### hard_sigmoid

`hard_sigmoid(values)` → Float32[] · `hard_sigmoid(x)` → Float32

Piecewise-linear sigmoid approximation max(0, min(1, 0.2·x + 0.5)).

### hard_swish

`hard_swish(values)` → Float32[] · `hard_swish(x)` → Float32

Hard Swish x · max(0, min(1, (x + 3) / 6)) — the MobileNetV3 definition.

## See Also

- [Numeric Functions](numeric.md) -- log_softmax and l2_normalize for output normalization
- [Vector & Tensor Functions](vector.md) -- vector reductions and manipulation for working with activation outputs
