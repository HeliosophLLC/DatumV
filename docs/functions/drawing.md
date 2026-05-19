---
title: Drawing Functions
category: drawing
---

# Drawing Functions

[← Back to Functions](string.md) · [SQL Reference](../sql/select.md) · [Image Functions](image.md) · [Lambda Expressions](../sql/lambda-expressions.md)

The drawing function family produces and consumes values of [`DataKind.Drawing`](../technical/data-value-layout.md) — procedural-visual recipes that describe what to draw without committing to a specific output size or encoding. A Drawing is a small tree of shape / text / image-stamp / group / transformed nodes; the universal rasterizer [`render(drawing, size)`](#render) walks the tree onto a bitmap.

A Drawing is the substrate for procedural-graphics workflows:

- **Static rendering** — `SELECT render(my_drawing, point2d(64, 64))` turns the recipe into an [`Image`](image.md).
- **Animation** — `animate_gif(duration, fps, size, lambda)` (Phase D) drives a [Lambda](../sql/lambda-expressions.md) that returns a fresh Drawing per frame, then encodes the sequence as an animated GIF.
- **Reusable visual components** — a UDF that returns a Drawing can be called from any of the above contexts. The same `my_torch(t, intensity)` UDF works in animations, static renders, or as a column value flowing through queries within a single row.

Drawings are **row-scoped** — they cannot be persisted to a column or written across rows. To produce a persistable visual, call `render(...)` to turn the recipe into an Image, which has full storage semantics.

## Universal renderer

### render

`render(drawing, size)` → Image

Rasterises a `Drawing` recipe onto an RGBA8888 `Image` of the requested width × height. The `size` argument is a `Point2D` where `x` = width and `y` = height in pixels.

The background defaults to **transparent** (alpha = 0). To produce a solid background, composite a full-canvas rectangle as the first child of the top-level group.

```sql
-- Static render of a Drawing value
SELECT render(my_drawing, point2d(64, 64)) AS thumb FROM drawings
```

Width and height must be positive integers; non-positive values raise an error. A null drawing or null size produces a null Image.

## Color primitives

### color

`color(r, g, b)` → Color
`color(r, g, b, a)` → Color

Constructs a 32-bit RGBA color from byte components. Each component must be in `[0, 255]`; out-of-range values raise an error. The three-argument form sets alpha to 255 (fully opaque).

```sql
color(255, 200, 50)        -- warm yellow, opaque
color(0, 0, 0, 128)        -- semi-transparent black
```

### color_hex

`color_hex(string)` → Color

Parses a CSS-style hex string. Leading `#` is optional. Recognised forms: `'#rgb'`, `'#rgba'` (3 / 4 digits, each digit doubled), `'#rrggbb'`, `'#rrggbbaa'` (6 / 8 digits, byte-precise). Hex digits are case-insensitive.

```sql
color_hex('#ff8800')      -- (255, 136, 0, 255)
color_hex('f80')          -- (255, 136, 0, 255)  — leading # optional, 3-digit form
color_hex('#1234abcd')    -- (18, 52, 171, 205)  — 8-digit with alpha
```

Invalid formats raise an error.

### color_interpolate

`color_interpolate(from Color, to Color, t)` → Color

Linearly blends `from` toward `to` by fraction `t`: `t = 0` returns `from`, `t = 1` returns `to`, intermediate values mix the R, G, B, and A channels independently. `t` outside `[0, 1]` clamps to the endpoints rather than extrapolating, so passing an animation curve directly without bounds-management is safe.

Pairs naturally with animation lambdas, waveform column positions, or any other normalised parameter:

```sql
-- Horizontal gradient across an animation
color_interpolate(color_hex('#00d4ff'), color_hex('#ff00aa'), t)

-- Amplitude-keyed gradient for waveform bars (loud parts pop)
color_interpolate(color_hex('#333'), color_hex('#fff'), hi - lo)
```

Null in any argument propagates as null. Interpolation runs in sRGB byte space — fast and matches the look of CSS / canvas gradients, and consistent with the gradient computed by `draw_line`'s five-argument form. A perceptual-uniform variant can land separately when a real consumer asks for one.

## Shape primitives

All shape primitives return a `Drawing` value. Geometry is in pixel coordinates relative to the eventual render target — the rasterizer does not apply implicit scaling.

### draw_rect

`draw_rect(at Point2D, size Point2D, fill Color)` → Drawing

Axis-aligned filled rectangle. `at` is the top-left corner; `size` is `(width, height)` in pixels.

```sql
draw_rect(point2d(13, 24), point2d(6, 24), color(101, 67, 33))
```

### stroke_rect

`stroke_rect(at Point2D, size Point2D, stroke Color, width)` → Drawing

Axis-aligned outlined (unfilled) rectangle. `at` and `size` follow the same convention as `draw_rect`; `width` is the stroke thickness in pixels. Negative width raises an error. The stroke is centred on the rectangle's geometric edge (Skia's default), so half the stroke width extends outside the `at..at+size` box.

```sql
stroke_rect(point2d(13, 24), point2d(6, 24), color(255, 200, 50), 1.5)
```

### draw_ellipse

`draw_ellipse(at Point2D, radii Point2D, fill Color)` → Drawing

Ellipse centred at `at`, with X-radius and Y-radius taken from `radii`.

```sql
draw_ellipse(point2d(16, 14), point2d(5, 10), color(255, 200, 50))
```

### draw_circle

`draw_circle(at Point2D, radius, fill Color)` → Drawing

Filled circle. Sugar over `draw_ellipse` with equal X and Y radii. The `radius` argument is numeric; floats are accepted. Negative radius raises an error.

```sql
draw_circle(point2d(16, 16), 4, color(255, 220, 100))
```

### draw_line

`draw_line(start Point2D, end Point2D, stroke Color, width)` → Drawing
`draw_line(start Point2D, end Point2D, start_color Color, end_color Color, width)` → Drawing

Stroked line segment from `start` to `end` with the given stroke width (pixels). Negative width raises an error.

The four-argument form paints a uniform colour. The five-argument form paints a **linear gradient** along the segment from `start_color` at `start` to `end_color` at `end` — useful for stacked rows of gradient bars (audio waveforms, spectrum plots, equalizers) without exploding the Drawing tree into many separate segments.

```sql
-- Uniform stroke
draw_line(point2d(0, 16), point2d(32, 16), color(80, 80, 80), 1.5)

-- Vertical gradient bar: bright at the top, dim at the bottom
draw_line(
    point2d(t * 1200, 0),
    point2d(t * 1200, 240),
    color_hex('#00d4ff'),    -- start (top)
    color_hex('#1a3a5c'),    -- end (bottom)
    1)
```

The gradient is computed in sRGB byte space, matching `color_interpolate`'s convention; mixing the two functions in the same picture produces consistent colour transitions.

### draw_polygon

`draw_polygon(points Array<Point2D>, fill Color)` → Drawing

Closed filled polygon with vertices in draw order. The polygon auto-closes (last vertex connects back to first). Requires at least 3 vertices; fewer raises an error.

```sql
draw_polygon(
    [point2d(0, 0), point2d(10, 0), point2d(5, 10)],
    color(0, 100, 200))
```

## Composition wrappers

### draw_group

`draw_group(children Array<Drawing>)` → Drawing

Composes a list of Drawings into a single Drawing. Children render in declared order — later children composite on top of earlier ones (back-to-front).

Null children are silently skipped, so a CASE expression that may produce a null Drawing for some rows composes cleanly without a separate filter step.

```sql
draw_group([
    draw_rect(point2d(0, 0), point2d(32, 48), color(20, 20, 30)),    -- background
    draw_ellipse(point2d(16, 24), point2d(8, 8), color(255, 200, 50)) -- foreground
])
```

### draw_transformed

`draw_transformed(content Drawing, anchor Point2D, rotation, scale, opacity)` → Drawing

Wraps a child Drawing with a 2D transform:

- `anchor` is the inner-content pixel position that lands at the canvas origin after the transform.
- `rotation` is in degrees, clockwise.
- `scale` is a single uniform factor (one number for both axes).
- `opacity` is multiplicative in `[0, 1]`. Values outside the range clamp.

```sql
draw_transformed(
    draw_ellipse(point2d(0, 0), point2d(5, 10), color(255, 200, 50)),
    point2d(16, 14),  -- anchor
    0,                -- rotation in degrees
    1.5,              -- uniform scale
    1.0)              -- opacity
```

## Animation

### animate_frames

`animate_frames(duration, fps, size, render_frame)` → Array&lt;Image&gt;

Drives an animation lambda over `frameCount = round(duration × fps)` evenly-spaced time values in `[0, 1)`, rasterising the `Drawing` each invocation produces into an `Image` of the requested size. Returns the per-frame Images as a flat `Array<Image>` — no encoding, no GIF, no composition into a single output value.

The `render_frame` lambda operates in the **animation context**: it receives `t Float32` (the current frame's normalised time) and must return a `Drawing`. The body can call any drawing primitive (and any globally-visible pure function) plus animation-specific functions tagged for this context.

```sql
-- A pulsing yellow circle over 1 second at 12 fps
SELECT animate_frames(1.0, 12, point2d(32, 32),
    (t) -> draw_ellipse(
        point2d(16, 16),
        point2d(4 + sin(t * 6.28) * 2, 4 + sin(t * 6.28) * 2),
        color(255, 200, 50))
) AS pulsing_dot
```

The result is an `Array<Image>`. Downstream consumers transform, preview, or encode it:

- **Per-frame transforms** via `array_transform(animate_frames(...), f -> sobel(f))` — every image function operates on individual frames naturally; no silent first-frame collapse.
- **Per-frame inspection** via `unnest(...)` — see [Inspecting frames with `unnest`](#inspecting-frames-with-unnest) below.
- **Static thumbnail strips** via the IDE preview (rendered as a horizontal sprite sheet).
- **Animated GIF** via [`frames_to_gif(frames, fps)`](#frames_to_gif) — the canonical pairing.

Validation: `duration` and `fps` must be positive; `size` must be positive in both dimensions; `duration × fps` must round to ≥ 1 frame. Null `render_frame` returns a null Array<Image>.

### frames_to_gif

`frames_to_gif(frames Array<Image>, fps)` → Image

Encodes an array of equally-sized frame Images as a single animated GIF89a Image, looping forever at the supplied frame rate. Natural pairing with `animate_frames`:

```sql
SELECT frames_to_gif(
    animate_frames(1.0, 12, point2d(64, 64),
        (t) -> draw_ellipse(
            point2d(32, 32),
            point2d(8 + sin(t * 6.28) * 4, 8 + sin(t * 6.28) * 4),
            color(255, 200, 50))),
    12) AS pulsing_dot_gif
```

The `fps` argument independently sets the per-frame delay in the GIF — it can differ from the `fps` passed to `animate_frames`, which lets you produce a "slow motion" or "fast preview" GIF from the same frame array without re-rendering. `fps` must be positive.

**Encoding details (v1):**

- **Palette:** single shared 256-colour global table, chosen by median-cut over the union of opaque pixels across all frames. Suitable for procedurally-rendered drawings whose palette barely shifts between frames; will band on smooth photographic gradients.
- **Transparency:** palette index 0 is reserved for transparent pixels (alpha &lt; 128). Frames use disposal method "restore to background", so transparent regions in one frame don't bleed into the next.
- **Compression:** standard GIF LZW with 9–12 bit codes and clear-on-full dictionary.
- **Looping:** Netscape 2.0 looping extension is always emitted (infinite loop).
- **No dithering or inter-frame diffs** in this version — both are deferred until a real workload asks.

**Null handling:** null `frames` array, null `fps`, or an entirely-empty / entirely-null array all return a null Image. Mixed-with-nulls arrays substitute each null with a fully-transparent frame at the canvas size, so a `CASE` expression that occasionally yields a null Drawing doesn't poison the encode. Mismatched frame dimensions are a hard error.

### Inspecting frames with `unnest`

`unnest(array Array<T>)` → table — expands any array-valued expression into one row per element. The output schema is a single column named `value` with the array's element kind. This is the canonical way to view animation frames one-by-one:

```sql
SELECT value AS frame
FROM unnest(
    animate_frames(1.0, 12, point2d(32, 32),
        (t) -> draw_ellipse(
            point2d(16, 16),
            point2d(4 + sin(t * 6.28) * 2, 4 + sin(t * 6.28) * 2),
            color(255, 200, 50))
    )
)
```

Each row's `value` is an Image, so all image functions apply per frame:

```sql
SELECT sobel(value) AS edges
FROM unnest(
    animate_frames(1.0, 12, point2d(32, 32),
        (t) -> draw_ellipse(
            point2d(16, 16),
            point2d(4 + sin(t * 6.28) * 2, 4 + sin(t * 6.28) * 2),
            color(255, 200, 50))
    )
)
```

Null arrays yield no rows (PostgreSQL semantics). `unnest` works on any `Array<T>` of object-backed elements (Image, Drawing, String, Struct, Lambda, …); primitive-array payloads (`Int32[]`, `Float32[]`, …) aren't supported through this path today.

### Closure capture in animation lambdas

The `render_frame` lambda captures the row in scope at the call site. Each row's animation sees that row's column values; the per-frame iteration within a row reuses the same captured environment:

```sql
SELECT animate_frames(1.0, 8, point2d(32, 32),
    (t) -> draw_rect(point2d(0, 0), point2d(32, 32), color(red_value, 0, 0))
) AS frames
FROM color_choices
```

Here `red_value` is a column on `color_choices`; each row produces an animation whose colour matches that row's value.

For the substrate that makes this possible (Lambda DataKind, FunctionContext, signature validation), see [Lambda Expressions](../sql/lambda-expressions.md).

## Animation curves

A set of pure functions of the animation lambda's `t` parameter that produce numeric values for driving Drawing parameters — positions, sizes, opacities. Each returns `Float32` and is restricted to the animation lambda body (LS completion only suggests them when the cursor is inside an `animate_frames` lambda).

All curves treat null inputs as null outputs (PG semantics) and throw on out-of-range arguments (`duration ≤ 0`, `bounces ≤ 0`, etc.).

### lerp

`lerp(t, low, high)` → Float32

Linear interpolation: `low + (high - low) * t`. `t` outside `[0, 1]` extrapolates rather than clamping.

```sql
draw_circle(point2d(lerp(t, 8, 56), 32), 4, color(255, 200, 50))
-- circle slides from x=8 at t=0 to x=56 at t=1
```

### oscillate

`oscillate(t, low, high)` → Float32 — one full sine cycle.
`oscillate(t, low, high, frequency)` → Float32 — `frequency` cycles in `[0, 1]`.

Starts at the midpoint of `[low, high]`, peaks at `t = 0.25 / frequency`, returns to midpoint at `0.5 / frequency`, troughs at `0.75 / frequency`, returns to midpoint at `1 / frequency`. `frequency` must be positive.

```sql
-- Pulsing radius between 4 and 8 pixels, 2 cycles over the animation
draw_circle(point2d(16, 16), oscillate(t, 4, 8, 2), color(255, 200, 50))
```

### bounce

`bounce(t, low, high)` → Float32 — ease-out bouncing from `low` (at `t = 0`) to `high` (at `t = 1`), with diminishing bounces.
`bounce(t, low, high, bounces)` → Float32 — explicit bounce count (default 3).

Uses the standard CSS `easeOutBounce` shape for the 3-bounce case; other counts use a damped-cosine envelope. Always reaches `high` exactly at `t = 1`.

```sql
-- Ball dropping into place
draw_circle(point2d(16, bounce(t, 0, 28)), 4, color(200, 80, 40))
```

### fade_in / fade_out

`fade_in(t)` → Float32 — opacity ramp `0 → 1` over the first 25% of the animation.
`fade_in(t, duration)` → Float32 — custom duration in `(0, 1]`.

`fade_out(t)` / `fade_out(t, duration)` — mirror, ramping `1 → 0` over the last 25% (or `duration`) of the animation.

Both return clamped `[0, 1]` values suitable for the `opacity` argument of `draw_transformed`.

```sql
draw_transformed(my_content, point2d(16, 16), 0, 1.0, fade_in(t) * fade_out(t))
-- content fades in over [0, 0.25] and fades out over [0.75, 1]
```

### wobble

`wobble(t, low, high)` → Float32 — irregular organic oscillation between `low` and `high`.
`wobble(t, low, high, seed)` → Float32 — deterministic variant chosen by `seed` (default 0).

Sum of three sine waves at `1×`, `2×`, `4×` frequencies with seed-derived phase offsets. Produces a more "alive" feel than `oscillate` while remaining purely a function of `t` — same `(t, seed)` always gives the same value.

```sql
-- Flame-like wobble on a flickering circle
draw_circle(point2d(16, 16), wobble(t, 6, 10, 42), color(255, 180, 80))
```

### random_walk

`random_walk(t, low, high)` → Float32 — deterministic random walk between `low` and `high`, sampled at 10 evenly-spaced steps.
`random_walk(t, low, high, steps)` → Float32 — explicit step count.
`random_walk(t, low, high, steps, seed)` → Float32 — full control.

Per-step values are deterministically hashed from `(seed, step_index)`; intermediate `t` values lerp linearly between adjacent steps. Same `seed` gives the same shape across runs; changing `steps` resamples the same underlying randomness at a different density.

```sql
-- Particle-like jitter
draw_circle(point2d(random_walk(t, 0, 32, 20, 1), random_walk(t, 0, 32, 20, 2)), 2, color(255, 255, 255))
```

Common composition pattern — chain a curve through `lerp` to map its output to whatever range a drawing primitive expects:

```sql
draw_transformed(
    my_drawing,
    point2d(16, 16),
    lerp(oscillate(t, 0, 1), -15, 15),  -- rotation in degrees: ±15° swing
    1.0,
    fade_in(t))
```

## Drawing as a reusable component

Because `Drawing` is a regular DataKind, a UDF can return one. The same UDF then composes naturally with both animation and static rendering:

```sql
CREATE FUNCTION my_badge(label String, hue Int32) RETURNS Drawing AS
    draw_group([
        draw_rect(point2d(0, 0), point2d(48, 16), color_hex('#222222')),
        draw_circle(point2d(8, 8), 4, color(hue, 200, 200))
    ]);

-- Use it in a static render
SELECT render(my_badge('OK', 100), point2d(48, 16)) AS img
FROM rows;

-- Or hand it to an animation as a non-time-varying sub-piece
SELECT animate_gif(1.0, 12, point2d(48, 16),
    (t) -> draw_transformed(my_badge('OK', 100), point2d(24, 8), t * 360, 1.0, 1.0)
) AS spinning_badge;
```

The Drawing value flows through the same channels as any other value — function arguments, return values, struct fields, array elements, CASE branches. It just doesn't persist across rows; `render(...)` is the persistence boundary.

## Content primitives

### draw_text

`draw_text(text, at Point2D, size, fill Color)` → Drawing
`draw_text(text, at Point2D, size, fill Color, font_family String)` → Drawing
`draw_text(text, at Point2D, size, fill Color, h_align String, v_align String)` → Drawing
`draw_text(text, at Point2D, size, fill Color, h_align String, v_align String, font_family String)` → Drawing

Renders text at the supplied anchor. The four- and five-argument forms anchor on the **baseline** — text ascends above the anchor and (for letters with descenders) drops below it. The six- and seven-argument forms accept explicit alignment:

- `h_align` ∈ `left` · `center` · `right` (case-insensitive; `centre` accepted)
- `v_align` ∈ `top` · `middle` · `baseline` · `bottom` (case-insensitive; `center`/`centre` accepted as a synonym for `middle`)

Vertical offsets are computed from the font's ascent/descent metrics — `top` puts the top of the ascent on the anchor, `bottom` puts the descender bottom on it, `middle` centres the body, and `baseline` matches the original anchor semantics.

`size` must be positive. When `font_family` is omitted (or names an uninstalled family), Skia falls back to the platform default — animations stay portable across machines without raising errors.

```sql
draw_text('HELLO', point2d(8, 16 + 14), 14, color(255, 200, 50))
-- 14pt warm yellow text at top-left (8, 16) — baseline anchor

draw_text('HELLO', point2d(64, 32), 14, color(255, 200, 50), 'center', 'middle')
-- 14pt text visually centred on (64, 32)

draw_text('HELLO', point2d(8, 8), 14, color(255, 200, 50), 'left', 'top')
-- 14pt text with the top-left of the rendered glyphs at (8, 8)
```

### draw_image

`draw_image(image Image, at Point2D)` → Drawing — stamps an Image at the supplied position, anchored at the top-left.
`draw_image(image Image, at Point2D, anchor Point2D)` → Drawing — places the image with a custom anchor in `[0, 1]` coordinates relative to the image dimensions.

The image's decoded bitmap is reused as the stamp source — no re-encode round-trip. Useful for composing existing renders or photos into a drawing tree.

```sql
-- Centre a 64×64 thumbnail at (32, 32)
draw_image(my_thumb, point2d(32, 32), point2d(0.5, 0.5))
```

### draw_path / stroke_path / fill_path

`draw_path(commands String, stroke Color, width)` → Drawing — strokes the supplied path.
`stroke_path(commands String, stroke Color, width)` → Drawing — alias for `draw_path`, the natural pair-name for `fill_path`.
`fill_path(commands String, fill Color)` → Drawing — fills the closed region of the path.

Path commands use an **SVG-subset string** with absolute coordinates:

| Command | Args | Meaning |
|---|---|---|
| `M` | `x y` | Move to `(x, y)` (starts a new sub-path) |
| `L` | `x y` | Line to `(x, y)` |
| `Q` | `cx cy x y` | Quadratic bezier through control `(cx, cy)` to `(x, y)` |
| `C` | `c1x c1y c2x c2y x y` | Cubic bezier through two controls to `(x, y)` |
| `Z` | — | Close the current sub-path |

Whitespace and commas both separate tokens. **Uppercase only** — relative-coordinate (lowercase) commands are not supported in v1. Unknown commands, malformed numbers, and missing coordinates all raise `FunctionArgumentException` with the offending value.

```sql
-- Heart-ish curve: cubic loops + line back
fill_path('M 16 28 C 0 16 0 4 16 12 C 32 4 32 16 16 28 Z', color(220, 60, 60))

-- Open stroked curve
draw_path('M 0 16 Q 16 0 32 16', color(80, 80, 200), 2)
```

Unclosed sub-paths are auto-closed for fill purposes (Skia default). To get crisp open outlines, prefer `draw_path` or add an explicit `Z`.

## 3D rotation

### spin_y / spin_x

`spin_y(content Drawing, anchor Point2D, angle_deg)` → Drawing — rotates the content around a **vertical** axis through `anchor`. Left and right edges swing toward and away from the viewer.
`spin_x(content Drawing, anchor Point2D, angle_deg)` → Drawing — rotates the content around a **horizontal** axis through `anchor`. Top and bottom edges tilt toward and away from the viewer.

Both use Skia's perspective matrix slots, so foreshortening is real — points further from the viewer compress; the result actually looks 3D, not just flat-scaled. `0°` = face-on, `±90°` = edge-on (collapses to a line through the anchor), `±180°` = mirrored.

```sql
-- The canonical Geocities marquee spin: text rotating around its centre
animate_frames(2.0, 24, point2d(128, 32), (t) -> spin_y(
    draw_text('HELLO WORLD', point2d(8, 22), 16, color(255, 200, 50)),
    point2d(64, 16),
    lerp(t, 0, 360)))
```

```sql
-- Gentle rocking — oscillate gives a back-and-forth instead of a full rotation
spin_y(my_drawing, point2d(32, 32), oscillate(t, -25, 25))
```

```sql
-- Vertical flip — `spin_x` rotates around horizontal axis
spin_x(my_drawing, point2d(32, 32), lerp(t, 0, 360))
```

Compose `spin_x` and `spin_y` by nesting to get tumbling motion:

```sql
spin_y(spin_x(my_drawing, point2d(32, 32), lerp(t, 0, 360)), point2d(32, 32), lerp(t, 0, 180))
```

The rotation matrix's effect on alpha is preserved, so spinning a `blend(...)`-wrapped Drawing keeps its compositing mode through the rotation.

## Blending

### blend

`blend(content Drawing, mode String)` → Drawing

Wraps a Drawing with a Porter–Duff / photographer blend mode. The inner drawing renders into a fresh transparent layer; when the layer composites back onto its parent canvas, the supplied `mode` determines how its pixels combine with what's already there.

**Mode strings** (case-insensitive; hyphens and underscores both work):

| Mode | Aliases | Effect |
|---|---|---|
| `normal` | `source-over`, `src-over` | Default alpha-over compositing |
| `multiply` | | Darkens — multiplies layer × backdrop |
| `screen` | | Lightens — inverse multiply |
| `overlay` | | Multiply on dark, screen on light |
| `darken` / `lighten` | | Per-channel min / max |
| `add` | `plus`, `additive` | Additive blending — **the canonical "glow" mode** |
| `difference` / `exclusion` | | Absolute / smoothed difference |
| `soft-light` / `hard-light` | | Soft / hard spotlight effects |
| `color-dodge` / `color-burn` | | Brighten / darken by reference |
| `hue` / `saturation` / `color` / `luminosity` | | HSL component blends |

Unknown mode strings raise `FunctionArgumentException`.

**Layer semantics.** The blend mode applies at the layer boundary, not per child. Children of a `draw_group([...])` wrapped by `blend(...)` blend with each other under normal alpha-over inside the layer; only the final layer composites with the requested mode. For per-particle additive glow, wrap the **sprite** in `blend`, not each particle.

```sql
-- Glowy sparks: each particle is a small bright dot that adds onto whatever's behind it.
draw_group([
    draw_rect(point2d(0, 0), point2d(64, 64), color(8, 4, 12)),  -- dark background
    draw_particles(
        t, point2d(32, 56), 30, 0.4,
        point2d(0, -100), 6,
        x -> blend(
            draw_circle(point2d(0, 0), 2, color(255, 200, 80)),
            'add'))
])
```

```sql
-- Multiply a coloured tint over a rendered scene for a unified colour grade.
blend(draw_rect(point2d(0, 0), point2d(64, 64), color(180, 120, 90, 200)), 'multiply')
```

## Particles

### draw_particles

`draw_particles(t, emit_at Point2D, rate, lifetime, velocity Point2D, jitter, sprite Drawing)` → Drawing
`draw_particles(..., seed)` → Drawing — deterministic variant.
`draw_particles(..., seed, warmup)` → Drawing — pre-warmed (see below).
`draw_particles(t, emit_at, rate, lifetime, velocity, jitter, sprite_fn Lambda<particle, Drawing>)` → Drawing — per-particle sprite.
`draw_particles(..., sprite_fn, seed)` → Drawing — both.
`draw_particles(..., sprite_fn, seed, warmup)` → Drawing — both, pre-warmed.

Restricted to `AnimationContext`. Emits particles deterministically over time and returns the set currently alive at frame time `t` as a `GroupDrawing` of transformed copies of the `sprite`. Each particle:

- Born at `t = i / rate` (where `i` is its index — `rate` particles per unit-`t`).
- Lives for `lifetime` units of `t` (must be in `(0, 1]`).
- Travels from a jittered spawn point at the (jittered) velocity.
- Has its opacity auto-ramped: `0 → 1` over the first 20% of its life, held at `1` for the middle 60%, `1 → 0` over the last 20%.

`jitter` may be a single scalar (symmetric — X and Y axes jitter by the same amount) or a `Point2D` (per-axis — `jitter.x` scales horizontal jitter, `jitter.y` scales vertical). Larger values produce both wider emission spread and more varied trajectories.

```sql
-- Tall narrow fountain: no horizontal spread, lots of vertical.
draw_particles(t, point2d(32, 56), 30, 0.4,
    point2d(0, -100), point2d(0, 8), ...)
```

```sql
-- Sparks shooting upward from (32, 56) over 1 second
SELECT animate_frames(1.0, 24, point2d(64, 64),
    (t) -> draw_particles(
        t,
        point2d(32, 56),       -- emit from
        20,                    -- 20 particles per second
        0.4,                   -- each lives 40% of the animation
        point2d(0, -80),       -- upward velocity (px/unit-t)
        4,                     -- spawn + velocity jitter
        draw_circle(point2d(0, 0), 1.5, color(255, 200, 80)))
) FROM dual
```

The total particle count per call is capped at **4096** to catch runaway `rate` arguments. Hitting the cap throws — adjust `rate`, `lifetime`, or `warmup` downward.

### Pre-warming with `warmup`

Without it, animations start visibly empty: at the lambda's `t = 0` only particle 0 has been born and has age 0 (opacity 0), so the first ~`lifetime`-worth of frames render an empty field while particles spawn and grow into the steady-state. For GIF loops this is jarring — the loop seam reveals "no flame, then flame".

`warmup` shifts the simulation forward: at `t = 0` the function pretends it's actually been running for `warmup` time, so the field already contains particles emitted during `[0, warmup]` at various points in their lifecycle. Set `warmup = lifetime` for a fully-populated field at the loop start (this is the typical setting):

```sql
-- Looping flame that starts already burning, no empty seam.
draw_particles(
    t, point2d(32, 56), 30, 0.4,
    point2d(0, -100), 6,
    x -> blend(
        draw_circle(point2d(0, 0), 2, color(255, 200 - x * 140, 50)),
        'add'),
    42,                                     -- seed
    0.4)                                    -- warmup = lifetime
```

Must be non-negative.

### Per-particle sprite via lambda

The `sprite_fn` variant accepts a lambda in `ParticleContext` whose parameter `x` is the **particle's normalised age** in `[0, 1]`. The lambda is invoked once per alive particle per frame, and its Drawing result is used as that particle's sprite. This lets each particle's appearance vary over its own life — colour shift, size shrink, rotation, …

```sql
-- Flame: particles fade from yellow → orange → red over their lifetime,
-- and shrink as they rise.
draw_particles(
    t, point2d(32, 56), 20, 0.4,
    point2d(0, -80), 4,
    x -> draw_circle(
        point2d(0, 0),
        lerp(x, 2, 0.5),                    -- radius shrinks from 2 to 0.5
        color(255, lerp(x, 220, 60), lerp(x, 100, 0))))  -- yellow → red
FROM dual
```

`ParticleContext` inherits from `AnimationContext`, so all animation curves (`lerp`, `oscillate`, `wobble`, `bounce`, `fade_in`, `fade_out`, `random_walk`) are callable on `x` inside the sprite lambda — applying them to `x` operates over the **particle's** lifetime rather than the animation's. Drawing primitives are globally visible and work as expected.

The lambda is a pure function of `x`; the particle's seed-derived per-particle jitter (position + velocity) isn't exposed to the lambda. If you want per-particle colour randomness rather than age-driven, compose `wobble(x, ...)` or `random_walk(x, ...)` with different seeds picked from outer SQL.

A subtle but important property: `draw_particles` is a pure function of `(t, seed)`. The same call site at the same frame always returns the same particle field, which is what makes the per-frame outputs stack into a coherent animation rather than re-rolling each frame.


## See Also

- [Lambda Expressions](../sql/lambda-expressions.md) — the substrate for animation drivers and procedural-component UDFs.
- [Image Functions](image.md) — what `render(...)` produces, and what subsequent pipelines consume.
- [Audio Functions](audio.md) — the `audio_waveform_*` family produces Drawings via a `waveform` lambda context that inherits from `AnimationContext`.
- [Functions Reference](string.md) — complete function listing across all categories.
