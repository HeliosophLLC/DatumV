---
title: Examples
---

# Examples

A growing collection of interesting things you can do in DatumIngest. Each example is a single self-contained query — paste it into the CLI or the web shell and it runs.

## Procedural graphics

### Animated torch

![Animated torch](figures/torch.gif)

A looping GIF of a tongue of flame, rendered entirely from a particle system. Particles spawn at the base, rise upward, shrink as they age, and fade from yellow through orange to dark red. The `'add'` blend mode gives the additive glow where particle trails overlap; `warmup = lifetime` ensures the first frame is already fully ablaze, so the GIF loop has no empty seam.

```sql
SELECT
    frames_to_gif(
        animate_frames(1.0, 24, point2d(64, 64), (t) ->
            draw_particles(
                t,
                point2d(32, 56),
                40,
                0.4,
                point2d(0, -80),
                3.4,
                x -> blend(
                    draw_circle(
                        point2d(0, 0),
                        1 + 10 * (1.0 - x),
                        color(255, lerp(x, 220, 80), lerp(x, 100, 0))
                    ),
                    'add'
                ),
                42, 0.4
            )
        ),
        12
    )
```

See [`draw_particles`](functions/drawing.md#draw_particles), [`animate_frames`](functions/drawing.md#animate_frames), and [`frames_to_gif`](functions/drawing.md#frames_to_gif) for the building blocks.
