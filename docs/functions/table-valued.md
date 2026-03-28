---
title: Table-Valued Functions
category: table
---

# Table-Valued Functions

[< Back to Functions Reference](string.md) | [SQL Reference](../sql/select.md)

Table-valued functions produce multiple rows and are used in FROM, CROSS JOIN, and LATERAL JOIN clauses. When used with `CROSS JOIN LATERAL` or `CROSS APPLY`, the function arguments can reference columns from the left-hand table, enabling per-row expansion of array or nested data.

### unnest

`unnest(array_col)` -> Rows | QU: 1

Expand array-valued column into separate rows. Works with Vector, UInt8Array, JsonValue arrays.

```sql
-- Expand a vector column per row using lateral join
SELECT t.name, s.value
FROM data AS t
CROSS JOIN LATERAL UNNEST(t.scores) AS s
```

### range

`range(start, end[, step])` -> Rows | QU: 1

Generate a sequence of rows with a `Value` column from start to end (inclusive). Default step is 1.

See [SQL Reference -- LATERAL JOIN / APPLY](../sql/joins.md#lateral-join--apply) for full syntax and examples.

### video_unnest_frames

`video_unnest_frames(source [, start_frame [, stride [, max_frames]]])` -> Rows | QU: 1

Enumerates frames of a video as lazy `VideoFrame` handles. Each output row is `(frame_index Int32, frame VideoFrame)`. The function does no decoding — it opens the source once to read container metadata, then emits one handle per frame in stride order. Pixels are materialised only when a downstream consumer (typically `video_frame_to_image`) routes the handle back through the per-query video registry.

`source` is either a STRING file path or a `Video` column value. Sidecar-backed Video columns are read directly from the `.datum-blob` window via a seekable stream — no full-file copy. Arena-backed Video values are read into a managed buffer once and fed to FFmpeg via an in-memory stream.

`start_frame` defaults to 0; `stride` to 1; `max_frames` defaults to the container's reported frame count (when known — falls back to "all frames" semantics where the function emits handles until the source returns end-of-stream).

```sql
-- All frames of a stored video, decoded at source resolution
SELECT video_frame_to_image(f.frame) AS img
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video) AS f

-- Every 10th frame from frame 100 onward, capped at 30 rows
SELECT video_frame_to_image(f.frame, 384) AS img
FROM video_unnest_frames('clip.mp4', 100, 10, 30) AS f

-- Per-frame depth estimation, false-coloured
SELECT f.frame_index,
       apply_colormap(models.midas_small(video_frame_to_image(f.frame, 384)), 'turbo') AS depth_viz
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video) AS f
```

Sequential frame access (frame N → N+1 → N+2) hits a warm FFmpeg decoder; backward access seeks to the file head and decodes forward. Stay in `frame_index` order whenever possible.

See [Examples — Video frames as a queryable column](../sql/examples.md#video-frames-as-a-queryable-column) for full pipelines.

## See Also

- [Aggregate Functions](aggregate.md) -- grouping and reduction functions
- [Window Functions](window.md) -- per-row computations over partitions
- [SQL Reference](../sql/select.md) -- full SQL dialect documentation
