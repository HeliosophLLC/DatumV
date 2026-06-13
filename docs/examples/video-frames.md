---
title: Video frames as a queryable column
---

Treat a video as a stream of frames addressable from SQL — extract frames lazily, decode only the ones a downstream function actually consumes, and feed the result into any Image-consuming model (depth estimators, classifiers, captioners).

## 1. Enumerate frames

`video_unnest_frames` turns a Video source into one row per frame. The output is a `(frame_index Int32, frame VideoFrame)` pair — the `VideoFrame` is a 12-byte lazy handle into the per-query video registry, not the decoded pixels.

```sql
-- File path source
SELECT frame_index, frame
FROM video_unnest_frames('clip.mp4')

-- Column source — videos table with one Video column per row
SELECT v.id, f.frame_index, f.frame
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video) AS f
```

Three optional arguments shape the emission: `video_unnest_frames(source, start_frame, stride, max_frames)`. Defaults are `(0, 1, all)`. To sample every 10th frame from frame 100 onward, capped at 30 rows:

```sql
SELECT frame_index, frame
FROM video_unnest_frames('clip.mp4', 100, 10, 30)
```

Iteration is lazy. The TVF emits handles in stride order at near-zero cost; FFmpeg only decodes when something downstream reads the frame's pixels. A `LIMIT 10` further upstream causes only 10 decodes — there's no need to bound `max_frames` for that reason alone.

## 2. Materialize a frame as an Image

`video_frame_to_image(frame)` resolves a `VideoFrame` handle through the registry and returns an `Image` you can feed to any image function. Single-argument form decodes at the source resolution:

```sql
SELECT video_frame_to_image(f.frame) AS img
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video, 0, 1, 5) AS f
```

Pass a `target_width` to resize while preserving the source aspect ratio (swscale does the resize fused with the YUV→BGRA decode, so it's effectively free):

```sql
-- 1920×1080 source → 384×216 output (aspect preserved)
SELECT video_frame_to_image(f.frame, 384) AS img
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video) AS f
```

Pass both dimensions for an exact size (aspect ratio not preserved):

```sql
-- 384×384 — typical depth-model input
SELECT video_frame_to_image(f.frame, 384, 384) AS img
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video) AS f
```

## 3. Compose with image-consuming models

Frames are normal `Image` values once materialised, so any image-consuming function works directly. Depth estimation on every frame of a clip, false-coloured:

```sql
SELECT
  f.frame_index,
  apply_colormap(models.midas_small(video_frame_to_image(f.frame, 384)), 'turbo') AS depth_viz
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video) AS f
ORDER BY f.frame_index
```

Or build a point cloud per frame for video-to-3D reconstruction work:

```sql
SELECT
  f.frame_index,
  point_cloud_from_depth_orthographic(
    video_frame_to_image(f.frame, 384),
    models.midas_small(video_frame_to_image(f.frame, 384)),
    60.0
  ) AS cloud
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video, 0, 5) AS f
```

## 4. Sequential access matters

The video registry keeps a warm FFmpeg decoder per registered source. Iterating frames in `frame_index` order (the default — `stride` is always positive forward) hits the fast path: ~3–5 ms per frame at 384px, ~11 ms at 1080p on a reference H.264 1080p clip.

Backward or out-of-order access (e.g. joining a frame-keyed table against `video_unnest_frames` in arbitrary order) seeks to the file head and decodes forward to each requested frame — orders of magnitude slower. Stay in order whenever possible.

## 5. Source types

`video_unnest_frames` accepts three sources, in order of how it materialises bytes for FFmpeg:

- **STRING file path** — `'C:/clips/scene.mp4'` or any path FFmpeg can open. The registry opens the file directly via libav.
- **`Video` column, sidecar-backed** — the typical case after ingesting videos into a `.datum` table. The registry wraps the `.datum-blob` window in a seekable stream so FFmpeg reads directly from the mmap region, no full-file copy.
- **`Video` column, arena-backed** — for arena-resident video bytes (mid-pipeline values that haven't been persisted yet). The bytes are copied to a managed buffer once and fed to FFmpeg via an in-memory stream.

The full reference for `video_unnest_frames` and `video_frame_to_image` is in [Table-Valued Functions](../functions/table-valued.md) and [Image Functions](../functions/image.md).
