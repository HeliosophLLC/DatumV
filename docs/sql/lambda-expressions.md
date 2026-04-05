---
title: Lambda Expressions
---

## Why Use This

A lambda lets you supply a small, inline recipe to a function that knows how to apply it — transforming each element of an array, computing each frame of an animation, or producing each layer of a procedurally-generated drawing. Without lambdas, every such case would require a dedicated function shape, an out-of-band callback, or a UDF.

In this engine, lambdas are **first-class values** ([`DataKind.Lambda`](../data-value-layout.md)). They can be passed to functions, returned from functions, stored in struct fields, or carried through CASE expressions — but they are **row-scoped**: a lambda cannot be persisted to a column or written across rows, because its closure captures only make sense in the row that created it.

## Common Patterns

### Animate a Drawing over time

```sql
SELECT animate_frames(2.0, 12, point2d(32, 48),
    (t) -> draw_circle(
        point2d(16, 24),
        4 + sin(t * 6.28) * 2,
        color(255, 200, 50))
) AS pulsing_dot
```

The lambda receives `t` (current frame time in `[0, 1)`) and returns a `Drawing`. `animate_frames` invokes it once per frame and returns the rendered Images as an `Array<Image>` — every image function operates on individual frames naturally via `array_transform`. A single-Image animated-GIF output is planned for a future phase as `animate_gif(...)`.

### Compose visual layers

```sql
SELECT animate_frames(1.5, 12, point2d(32, 48),
    (t) -> draw_group([
        draw_rect(point2d(13, 24), point2d(6, 24), color(101, 67, 33)),
        draw_ellipse(
            point2d(16, 14),
            point2d(5, 10),
            color(255, 200, 50))
    ])
) AS torch
```

## Syntax

A single parameter needs no parentheses:

```sql
(t) -> oscillate(t, 0, 1, 1)
t -> oscillate(t, 0, 1, 1)
```

Parentheses are required for two or more parameters:

```sql
(a, b) -> a * b
```

The arrow operator is `->` (thin arrow). The body is any scalar expression.

### Closure capture

Lambda bodies can reference columns from the enclosing row:

```sql
SELECT animate_frames(1.0, 12, point2d(32, 32),
    (t) -> draw_rect(point2d(0, 0), point2d(32, 32), color(fill_red, fill_green, 0))) AS frames
FROM color_choices
```

Here `fill_red` and `fill_green` are columns on `color_choices`, captured at the moment each row's lambda is created. The capture is snapshotted once per row — the lambda body can be invoked many times (e.g. 12 times for a 1-second 12-fps animation), and every invocation sees the same captured values.

## Function contexts

Some functions only make sense inside a specific kind of lambda body. A consumer function declares a **function context** that its lambda parameter operates in; that context determines which functions are callable inside the body.

For example, `animate_frames`'s `render_frame` parameter expects a lambda in the **animation context**, which exposes:

- The canonical parameter `t Float32` (the LS pre-fills `t -> ` on completion; you can rename to `(u) -> ...` if you prefer).
- Animation primitives: `oscillate`, `wobble`, `lerp`, `bounce`, `fade_in`, `fade_out`, `random_walk`, `draw_particles`.
- Inherited from the parent **pure context**: arithmetic, math, string operations, drawing primitives (`draw_rect`, `draw_ellipse`, `color`, `point2d`, ...), and any other globally-visible deterministic functions.

A function tagged with a specific context — like `draw_particles` or `oscillate` — is **only callable inside lambda bodies whose parameter slot declared that context**. Calling `oscillate(...)` from top-level SQL fails to resolve, because outside an animation lambda the function has no meaningful `t` to operate against.

Globally-visible functions (the unmarked majority) are callable everywhere — inside any context's lambda body, and at the top level.

## Time as an explicit parameter

There is no implicit "current time" variable. `t` is a regular lambda parameter, threaded by the user to functions that need it:

```sql
draw_particles(t + 1.5, ...)   -- warmup: act as if 1.5s already elapsed
oscillate(t + phase_offset, ...)  -- phase-shifted oscillation
oscillate(mod(t, 0.5), ...)    -- seamless half-second loop
```

Arithmetic on `t` is how warmup, phase offsets, and loop wrapping are expressed. The engine does no time bookkeeping for you — and that's the point.

## Persistence

A lambda value cannot be:

- Stored in a column (INSERT throws at the arena boundary)
- Returned to a top-level SELECT projection (the row-write path throws)
- Carried across rows of the same query

Lambdas are intra-query intermediate values. The error message names them explicitly: *"Lambda values cannot be persisted to a column, output row, or any other arena-write boundary. They exist only as intra-query intermediate values flowing between higher-order functions and their consumers."*

Within a single row, lambdas flow freely — as function arguments, return values, struct fields, array elements, or CASE branches.

## Restrictions

- Lambdas cannot be persisted (see above). The arena-write boundary throws.
- Lambda parameter names shadow column names of the same name within the body.
- Context-scoped functions (`oscillate`, `draw_particles`, etc.) cannot be called outside their declared context.
- The DataValue evaluation path refuses to lower a `LambdaExpression` (the managed-payload closure only fits `ValueRef`); call sites that consume lambdas must evaluate via the ValueRef path. This is an internal evaluator constraint, not a user-visible one — but explains why a function takes a `Lambda` parameter rather than e.g. a `Json`-encoded recipe.

## See Also

- [Array, Struct & Index Literals](literals.md)
- [CASE Expressions](case-expressions.md)
- [SELECT](select.md)
- [Image Functions](../functions/image.md) — drawing primitives that compose with lambda-driven animation
