---
title: Video Functions
category: video
---

# Video Functions

Functions over `Video` values — encoded video containers (MP4, WebM, MKV, …) carried as a typed column. Container metadata is parsed once at ingest by FFmpeg and stamped inline, so the accessors below read it back without re-opening the file.

Frame-level work lives in two companion pages: [`video_unnest_frames`](table-valued.md#video_unnest_frames) enumerates a video as lazy `VideoFrame` handles, and [`video_frame_to_image`](image.md#video_frame_to_image) materialises a handle into a decoded `Image`. Together they turn a video into a queryable sequence of frames — see [Video frames as a queryable column](../examples/video-frames.md) for the full pattern.

## Metadata

### video_width

`video_width(video)` → Int32

Pixel width, read from the video value's inline metadata. [Elidable accessor](../technical/planner-time-elision.md) — the dimensions are stamped at ingest by the header parser, which opens the container with FFmpeg and reads the best video stream's `codecpar` without spinning up a decoder. Returns NULL when the dimensions weren't stamped (corrupt input, no video stream, or a value produced by a path that didn't parse the header).

```sql
SELECT video_width(video), video_height(video) FROM clips
```

### video_height

`video_height(video)` → Int32

Pixel height; companion to [`video_width`](#video_width) and stamped from the same header parse. Pair the two for aspect-preserving work (e.g. computing a resize target before `video_frame_to_image`).

```sql
SELECT path
FROM clips
WHERE video_width(video) >= 1920 AND video_height(video) >= 1080
```

### video_duration

`video_duration(video)` → Float64

Clip length in seconds. [Elidable accessor](../technical/planner-time-elision.md) — computed inline as `frame_count ÷ fps` from the stamped metadata. The inline fps is stored as 8.8 fixed-point, so a non-integer rate like 23.976 rounds and the inline value is approximate; for containers that don't record a frame count (some MKV variants, fragmented MP4), it falls back to a decode-free read of the container's authoritative recorded duration. Returns NULL only when no duration is recorded.

```sql
-- Clips longer than 30 seconds
SELECT path, video_duration(video) AS seconds
FROM clips
WHERE video_duration(video) > 30.0
ORDER BY seconds DESC
```

## Frames

These functions are documented on their category pages; the cross-links below are for convenience.

### video_unnest_frames

`video_unnest_frames(source [, start_frame [, stride [, max_frames]]])` → Rows

Enumerates frames of a video as lazy `VideoFrame` handles — one row of `(frame_index Int32, frame VideoFrame)` per frame, with no decoding until a downstream consumer asks for pixels. `source` is a STRING file path or a `Video` column value. See [Table-Valued Functions → video_unnest_frames](table-valued.md#video_unnest_frames) for parameters, sequential-access notes, and the zero-based frame-index convention.

### video_frame_to_image

`video_frame_to_image(frame [, target_width [, target_height]])` → Image

Materialises a `VideoFrame` handle into a decoded `Image`, optionally resizing inside the YUV→BGRA conversion. See [Image Functions → video_frame_to_image](image.md#video_frame_to_image) for the resize overloads and warm-decoder performance characteristics.

```sql
-- Every 10th frame from frame 100 onward, capped at 30 rows, resized to 384px wide
SELECT f.frame_index, video_frame_to_image(f.frame, 384) AS img
FROM video_unnest_frames('clip.mp4', 100, 10, 30) AS f
```

## See Also

- [Audio Functions](audio.md) — the parallel metadata + decode surface for `Audio` values, including `audio_duration`.
- [Image Functions](image.md) — `video_frame_to_image` and the broader image toolkit that decoded frames flow into.
- [Table-Valued Functions](table-valued.md) — `video_unnest_frames` and other row-producing functions.
- [Video frames as a queryable column](../examples/video-frames.md) — worked example: per-frame inference over a stored video.
- [Functions Reference](string.md) — complete function listing across all categories.
