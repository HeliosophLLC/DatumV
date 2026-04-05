---
title: Trigonometric Functions
category: trigonometry
---

# Trigonometric Functions

[← Back to Functions](string.md) · [SQL Reference](../sql/select.md)

All functions accept any numeric kind and return `Float64`. Angles are in radians.

### sin

`sin(x)` → Float64

Sine of `x` (radians).

### cos

`cos(x)` → Float64

Cosine of `x` (radians).

### tan

`tan(x)` → Float64

Tangent of `x` (radians).

### cot

`cot(x)` → Float64

Cotangent of `x` (radians); equivalent to `1 / tan(x)`. At integer multiples of π the result surfaces as ±∞ from floating-point division.

### asin

`asin(x)` → Float64

Arc sine returning radians. Inputs outside [-1, 1] surface as `NaN`.

### acos

`acos(x)` → Float64

Arc cosine returning radians. Inputs outside [-1, 1] surface as `NaN`.

### atan

`atan(x)` → Float64

Arc tangent returning radians in (-π/2, π/2).

### atan2

`atan2(y, x)` → Float64

Two-argument arc tangent: the angle in radians between the positive x-axis and the ray to the point `(x, y)`. Result range [-π, π]. A null in either argument yields null.

### sinh

`sinh(x)` → Float64

Hyperbolic sine.

### cosh

`cosh(x)` → Float64

Hyperbolic cosine.

### tanh

`tanh(x)` → Float64

Hyperbolic tangent.

### radians

`radians(x)` → Float64

Converts `x` from degrees to radians.

### degrees

`degrees(x)` → Float64

Converts `x` from radians to degrees.

### pi

`pi()` → Float64

Returns the constant π.

### euler

`euler()` → Float64

Returns Euler's number e.

## See Also

- [Numeric Functions](numeric.md) -- arithmetic, rounding, powers, roots, and logarithms
- [ML Activation Functions](activation.md) -- activation functions that build on trigonometric/hyperbolic operations
- [Vector & Tensor Functions](vector.md) -- element-wise vector operations and reductions
