---
title: Examples
---

# Examples

End-to-end SQL workflows that combine multiple primitives — type-system,
function-reference, and model-catalog pages each cover one piece in
isolation; this page shows them in motion against realistic data.

## Depth maps and 3D point clouds

Turn a folder of photos into 3D point clouds with a single SQL pipeline:
ingest the images, run a depth-estimator model, unproject into 3D,
inspect the geometry, and re-render the depth as false-colour.

### 1. Inspect a depth model's output

The depth-estimator models in the catalog (`models.midas_small`,
`models.dpt_large`, `models.zoedepth_nyu_kitti`, …) all `IMPLEMENTS DepthEstimator`
and return a grayscale-as-RGBA Image — one pixel per input pixel, brighter
intensity = closer surface. Render one to see what you're working with:

```sql
  SELECT
    file_name,
    models.midas_small(file_bytes) AS depth
  FROM photos
  LIMIT 1
```

The output is a single-channel depth image at the source dimensions —
useful, but visually hard to read because grayscale collapses depth
differences into mid-tones the eye doesn't resolve well.

### 2. False-colour the depth map

`apply_colormap` maps the red-channel intensity through a perceptual
palette — depth becomes hue, which the eye reads instantly:

```sql
  SELECT
    file_name,
    apply_colormap(models.midas_small(file_bytes), 'turbo') AS depth_viz
  FROM photos
  LIMIT 4
```

`turbo` is the default for depth (Google's perceptually-improved jet); `jet`
matches legacy MATLAB rainbow output; `gray` is the identity pass-through
useful for round-trip checks.

### 3. Build a point cloud

Two constructors unproject every pixel into a 3D point — they differ in
how (X, Y) positions scale with depth. The output cloud is always
**organized** — one point per pixel in row-major (u, v) order — so
consumers can derive implicit topology (implicit triangles per grid
cell) without extra metadata.

```sql
-- Orthographic projection (recommended for MiDaS / DPT / ZoeDepth):
-- each pixel's (X, Y) is fixed by its image position; depth only
-- pushes points forward or back along Z. Reads as a tilted heightfield.
SELECT
  file_name,
  point_cloud_from_depth_orthographic(file_bytes, models.midas_small(file_bytes), 60.0) AS cloud
FROM photos
LIMIT 1
```

```sql
-- Pinhole projection: angular position scales with depth (close pixels
-- cluster near the optical axis, far pixels spread to the frustum
-- edges). Physically correct when depth values are real-world
-- distances; for normalized inverse depth, the perspective effect is
-- a distortion artifact rather than honest geometry.
SELECT
  file_name,
  point_cloud_from_depth_pinhole(file_bytes, models.zoedepth_nyu_kitti(file_bytes), 60.0) AS cloud
FROM photos
LIMIT 1
```

The 60° FOV is a good default for phone wide cameras. Use ~40° for
portrait lenses, ~55–65° for depth cameras (RealSense, Kinect). The
exact value scales X/Y proportionally — it rarely matters for
visualisation, more for cross-image consistency.

### 4. Inspect the cloud's properties

Every `PointCloud` carries a header — point count, axis-aligned bounding
box, organisation flag, color flag, coordinate frame — readable through
single-arg accessors:

```sql
WITH clouds AS (
  SELECT
    file_name,
    point_cloud_from_depth_orthographic(file_bytes, models.midas_small(file_bytes), 60.0) AS cloud
  FROM photos
)
SELECT
  file_name,
  point_cloud_count(cloud)        AS points,
  point_cloud_width(cloud)        AS w,
  point_cloud_height(cloud)       AS h,
  point_cloud_is_organized(cloud) AS organized,
  point_cloud_bbox_min(cloud).x   AS x_min,
  point_cloud_bbox_max(cloud).x   AS x_max,
  point_cloud_bbox_min(cloud).z   AS near_z,
  point_cloud_bbox_max(cloud).z   AS far_z
FROM clouds
```

`point_count` matches `width × height` for organized clouds. The X/Y
bbox widens with FOV; the Z bbox spans `[-1, -0.1]` in the normalized
inverse-depth space (closer surfaces near `-0.1`, farther near `-1`).
The `-0.1` near plane keeps closest-intensity pixels at their correct
angular position rather than collapsing them to the camera origin.
Cloud coordinates are in the OpenGL camera frame: right-handed, +y up,
−z forward.

### 5. Compare depth models side by side

Stack two depth-model outputs as parallel point clouds for the same
image — useful for spot-checking model quality differences:

```sql
WITH first_photo AS (SELECT * FROM photos LIMIT 1)
SELECT
  file_name,
  point_cloud_from_depth_orthographic(file_bytes, models.midas_small(file_bytes), 60.0) AS midas_cloud,
  point_cloud_from_depth_orthographic(file_bytes, models.dpt_large(file_bytes), 60.0)   AS dpt_cloud,
  point_cloud_count(point_cloud_from_depth_orthographic(file_bytes, models.midas_small(file_bytes), 60.0)) AS n
FROM first_photo
```

`models.midas_small` is fast and small (~70 MB); `models.dpt_large` is
heavier (~1.3 GB) but sharper. Their bboxes differ — DPT typically
gives a wider Z range because it preserves more dynamic range
before normalization.

### 6. Round-trip: cloud → depth → false colour

`point_cloud_depth(pc)` is the inverse of the depth-unprojection
constructors — it reconstructs a depth Image from an organized cloud by
reading each point's Z value and re-normalizing to grayscale. Useful for
verifying geometry, swapping colormap palettes after the fact, or
extracting just the depth channel from a coloured cloud.

```sql
  SELECT
    file_name,
    apply_colormap(
      point_cloud_depth(
        point_cloud_from_depth_orthographic(file_bytes, models.dpt_large(file_bytes), 60.0)
      ),
      'turbo'
    ) AS depth_recovered
  FROM photos
  LIMIT 1
```

The round-trip is lossy at the per-pixel level (Z is re-normalized
per-cloud, then re-packed to 8-bit) but geometrically faithful — a
cloud that's flat in Z produces a uniform grey image; a cloud with a
gradient produces a gradient.

### 7. Filter cloud collection by geometry

Use the accessors as predicates to filter a cloud collection:

```sql
SELECT file_name
FROM (
  SELECT
    file_name,
    point_cloud_from_depth_orthographic(file_bytes, models.midas_small(file_bytes), 60.0) AS cloud
  FROM photos
)
WHERE point_cloud_count(cloud) > 500000           -- skip tiny thumbnails
  AND point_cloud_bbox_max(cloud).z - point_cloud_bbox_min(cloud).z > 0.5   -- skip near-flat scenes
ORDER BY point_cloud_count(cloud) DESC
```

This is the value proposition of `PointCloud` as a first-class kind —
geometry becomes a queryable column, not an opaque export blob. You
can sort by depth range, join clouds against classification labels,
or persist them to `.datum` and run analytics across thousands at a
time.

### 8. Promote to a Mesh and export as a real 3D asset

A `PointCloud` is renderable inside DatumIngest's viewer, but to share
the result — open in Blender, slice for 3D printing, hand to a colleague
who lives in Unity — you need a real 3D asset on disk. `mesh_from_organized`
promotes an organized cloud to an explicit triangle mesh (with per-vertex
normals computed from the topology, smooth shading included); the
`mesh_to_*` exporters serialize to industry-standard formats:

```sql
-- Blender / Unity / Three.js / browser-built-in 3D viewer
SELECT mesh_to_gltf(
  mesh_from_depth_orthographic(file_bytes, models.midas_small(file_bytes), 60.0)
)
FROM photos LIMIT 1

-- 3D-printer slicer (Bambu Studio / PrusaSlicer / Cura / Lychee / ChiTuBox)
SELECT mesh_to_stl(
  mesh_from_depth_orthographic(file_bytes, models.dpt_large(file_bytes), 60.0)
)
FROM photos LIMIT 1

-- MeshLab / CloudCompare / Open3D (preserves per-vertex colors via OBJ extension)
SELECT mesh_to_obj(
  mesh_from_depth_orthographic(file_bytes, models.midas_small(file_bytes), 60.0)
)
FROM photos LIMIT 1
```

`mesh_from_depth_orthographic` is the fused shortcut for the common case
(unproject + triangulate in one call); `mesh_from_organized(point_cloud)`
is the two-step form when you want to keep the intermediate cloud
around too.

Mesh triangulation skips cells whose four corner depths span more than
5% of the cloud's bbox Z range — depth edges produce topology breaks
rather than rubber-sheet skirts at object boundaries. The result reads
correctly in any 3D viewer with the expected sharp silhouettes.

### 9. Inspect a mesh's geometry

```sql
WITH meshes AS (
  SELECT
    file_name,
    mesh_from_depth_orthographic(file_bytes, models.midas_small(file_bytes), 60.0) AS m
  FROM photos
)
SELECT
  file_name,
  mesh_vertex_count(m)   AS verts,
  mesh_triangle_count(m) AS tris,
  mesh_has_color(m)      AS colored,
  mesh_has_normals(m)    AS shaded,
  mesh_bbox_max(m).z - mesh_bbox_min(m).z AS depth_range
FROM meshes
ORDER BY tris DESC
```

The mesh shares the cloud's coordinate frame and bbox conventions
(OpenGL right-handed, `[-1, -0.1]` Z range for Image-based depth
sources). The exporters automatically apply the standard glTF / STL /
OBJ +Y-up orientation regardless of the mesh's declared frame, so
saved files render correctly in any consumer.

For the full surface — every constructor, accessor, exporter, plus the
coordinate-frame rules — see [Spatial Types](spatial.md).

## Video frames as a queryable column

Treat a video as a stream of frames addressable from SQL — extract frames lazily, decode only the ones a downstream function actually consumes, and feed the result into any Image-consuming model (depth estimators, classifiers, captioners).

### 1. Enumerate frames

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

### 2. Materialize a frame as an Image

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

### 3. Compose with image-consuming models

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

### 4. Sequential access matters

The video registry keeps a warm FFmpeg decoder per registered source. Iterating frames in `frame_index` order (the default — `stride` is always positive forward) hits the fast path: ~3–5 ms per frame at 384px, ~11 ms at 1080p on a reference H.264 1080p clip.

Backward or out-of-order access (e.g. joining a frame-keyed table against `video_unnest_frames` in arbitrary order) seeks to the file head and decodes forward to each requested frame — orders of magnitude slower. Stay in order whenever possible.

### 5. Source types

`video_unnest_frames` accepts three sources, in order of how it materialises bytes for FFmpeg:

- **STRING file path** — `'C:/clips/scene.mp4'` or any path FFmpeg can open. The registry opens the file directly via libav.
- **`Video` column, sidecar-backed** — the typical case after ingesting videos into a `.datum` table. The registry wraps the `.datum-blob` window in a seekable stream so FFmpeg reads directly from the mmap region, no full-file copy.
- **`Video` column, arena-backed** — for arena-resident video bytes (mid-pipeline values that haven't been persisted yet). The bytes are copied to a managed buffer once and fed to FFmpeg via an in-memory stream.

The full reference for `video_unnest_frames` and `video_frame_to_image` is in [Table-Valued Functions](../functions/table-valued.md) and [Image Functions](../functions/image.md).

## Multi-dim arrays and bracket indexing

Columns declared with multiple dimensions (`Array<T>(N, M, …)`) carry an
explicit shape — elements are addressed by per-dim indices via bracket
syntax `m[y, x]`. The shape also survives `infer()` outputs whose ONNX
tensor rank is ≥ 2, so depth maps and feature tensors can be poked at
directly without manually flattening.

```sql
CREATE TABLE grids (m Array<Float32>(2, 3));
INSERT INTO grids VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0]);

-- Per-element access (zero-based, row-major). Out-of-range returns NULL.
SELECT m[0, 0]       AS top_left,    -- 1.0
       m[1, 2]       AS bottom_right -- 6.0
FROM grids;

-- Introspection: shape, ndim, total element count.
SELECT array_shape(m)         AS shape,       -- [2, 3]
       array_ndims(m)         AS ndim,        -- 2
       cardinality(m)         AS total,       -- 6
       array_length(m, 1)     AS rows,        -- 2
       array_length(m, 2)     AS cols         -- 3
FROM grids;
```

The same syntax works against multi-dim function outputs — useful for
plucking a single pixel out of a depth map without materialising the
intermediate tensor:

```sql
-- Pixel-at-center of a 384×384 depth output (after squeezing the leading
-- batch dim). Useful for spot-checking inference results.
SELECT models.midas_small(file_bytes)[0, 192, 192] AS center_depth
FROM photos
LIMIT 1;
```

See [Array Functions](../functions/array.md#multi-dim-arrays) for the
per-function multi-dim behavior table — which functions are shape-aware
vs. which silently flatten.

## See Also

- [Type System](type-system.md) — full `PointCloud` reference: storage
  layout, organized vs unorganized, the full accessor table.
- [Image Functions](../functions/image.md) — `apply_colormap`,
  `depth_map_to_image`, and the broader image-manipulation surface.
- [Models](../models.md) — depth estimators, including the
  metric-vs-relative distinction across `models.midas_small`,
  `models.dpt_large`, and `models.zoedepth_nyu_kitti`.
