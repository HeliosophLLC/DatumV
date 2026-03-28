---
title: Spatial Types
---

# Spatial Types

DatumIngest carries four first-class spatial kinds:

| Kind | What it is | When to reach for it |
|------|-----------|----------------------|
| `Point2D` | A single 2D point (X, Y) | 2D coordinates — image pixels, geographic latitude/longitude, screen positions |
| `Point3D` | A single 3D point (X, Y, Z) | 3D coordinates — vertices, sensor readings, individual sample points |
| `PointCloud` | Dense 3D point collection with optional per-point color | Depth-map unprojection, LiDAR / RGB-D scans, photogrammetry — anything turning 2D-per-pixel data into 3D structure |
| `Mesh` | Triangulated 3D surface with optional per-vertex color + normals + (Phase 2) UVs + texture | Surfaces you want to export as real 3D assets — `.glb` for Blender / Unity / web viewers, `.stl` for 3D printing, `.obj` for MeshLab / Open3D |

The pattern across the four: `Point2D` and `Point3D` are atomic scalars
(fits inline in a `DataValue`), while `PointCloud` and `Mesh` are
container kinds — byte-blob payloads with a small fixed header followed
by interleaved per-element data, designed to round-trip through `.datum`
files like `Image` / `Audio` / `Video` do.

## Quick Start

A single SQL statement that turns a photo into a 3D mesh and writes it
to a glTF file ready for Blender:

```sql
SELECT mesh_to_gltf(
  mesh_from_depth_orthographic(file_bytes, models.midas_small(file_bytes), 60.0)
)
FROM photos LIMIT 1
```

Same shape for 3D printing:

```sql
SELECT mesh_to_stl(
  mesh_from_depth_orthographic(file_bytes, models.dpt_large(file_bytes), 60.0)
)
FROM photos LIMIT 1
```

End-to-end walkthroughs of these workflows live in
[Examples — Depth maps and 3D point clouds](examples.md#depth-maps-and-3d-point-clouds).

## Points

`Point2D` and `Point3D` are first-class scalar kinds for spatial
coordinates. Both store single-precision (`Float32`) components packed
inline in the `DataValue` (8 bytes for `Point2D`, 12 bytes for `Point3D`)
— no arena allocation, no sidecar reference. They round-trip through the
`.datum` v2 format using the FixedWidth encoder.

### Constructors

```sql
-- Build a Point3D column from three numeric columns
SELECT point3d(x, y, z) AS pt FROM raw_lidar

-- Build a Point2D from latitude/longitude pairs
SELECT point2d(latitude, longitude) FROM cities
```

`point2d(x, y)` and `point3d(x, y, z)` accept any mixed numeric inputs
(Float64, Int32, etc.) and widen to `Float32`.

### Accessors

| Function | Returns | Notes |
|----------|---------|-------|
| `point_x(p)` | `Float32` | X component of a `Point2D` or `Point3D`. |
| `point_y(p)` | `Float32` | Y component. |
| `point_z(p)` | `Float32` | Z component. **`Point3D` only** — `Point2D` has no Z. |
| `distance(a, b)` | `Float32` | Euclidean distance between two same-dimension points. |
| `distance_sq(a, b)` | `Float32` | Squared distance — skips the square root for KNN-ranking / threshold checks where absolute distance isn't needed. |

```sql
-- Find points within a radius
SELECT id, distance(pt, point3d(0, 0, 0)) AS r
FROM cloud
WHERE distance_sq(pt, point3d(0, 0, 0)) < 100.0
ORDER BY r
```

Mixing `Point2D` with `Point3D` in `distance` / `distance_sq` is a
function-argument error.

Both kinds also participate in `typeof()`, `IS Type`, and `CAST` like
any other DataKind. Field access via `.x` / `.y` / `.z` is sugar for the
respective `point_*` function.

## Point Clouds

`PointCloud` is a dense collection of 3D points with optional per-point
color, designed for depth-map unprojection, LiDAR / RGB-D scans, and any
workflow that turns 2D-per-pixel data into 3D structure. Storage is a
single byte blob: a 40-byte header (point count, axis-aligned bounding
box, coordinate-frame tag, organization dimensions, has-color flag)
followed by an interleaved per-point payload at a fixed stride — 12
bytes (xyz `Float32`) per point when position-only, 16 bytes (xyz +
RGBA uint8) when color is present.

A point cloud is **organized** when its declared `width × height` matches
its point count — points are laid out row-major in (u, v) order and
consumers can derive implicit topology (e.g. two triangles per grid cell)
without any extra metadata. Constructors that work pixel-by-pixel (like
`point_cloud_from_depth_orthographic`) produce organized clouds; LiDAR
scans, photogrammetry outputs, and decimated clouds leave both dimensions
at 0 and the cloud is treated as an unstructured point set.

### Constructors

Two constructors unproject a per-pixel depth Image into a 3D point
cloud — they differ in how (X, Y) pixel positions scale with depth:

```sql
-- Orthographic projection (recommended for MiDaS / DPT / ZoeDepth visualization):
-- each pixel's (X, Y) is fixed by its image position; depth only pushes
-- points forward or back along Z. The "honest" interpretation when depth
-- values are normalized inverse depth (a relative ordering, not real
-- distances). Reads as a tilted heightfield.
SELECT point_cloud_from_depth_orthographic(image, models.midas_small(image), 60.0)
FROM photos LIMIT 1

-- Pinhole projection: angular position scales with depth (close pixels
-- cluster near the optical axis, far pixels spread to the frustum
-- edges). Physically correct when depth values represent real-world
-- distances (metric depth, RGB-D sensors, LiDAR).
SELECT point_cloud_from_depth_pinhole(image, models.zoedepth_nyu_kitti_meters(image), 60.0)
FROM photos LIMIT 1
```

The third argument is the vertical field-of-view in degrees, matching
the Three.js `PerspectiveCamera` convention. Typical values: 60° for
phone wide cameras, 40° for portrait lenses, 55–65° for depth cameras
(RealSense, Kinect). The exact value scales X/Y proportionally —
rarely matters for visualisation, more for cross-image consistency.
Accepts any numeric kind (`60`, `60.0`, `cast(60 as Float32)`, …); no
explicit cast required.

The depth Image must be a grayscale-as-RGBA inverse-depth map (the
standard output of `depth_map_to_image` on MiDaS / DPT / ZoeDepth
models) at the same dimensions as the color Image. Both constructors
also accept a shape-aware `Array<Float32>(h, w)` depth instead of an
Image — pair with the metric model variants
(`zoedepth_nyu_kitti_meters`, `glpn_nyu_meters`) to preserve real-world
distances. Use the `_pinhole` variant for the metric path: physical
distances make pinhole projection the geometrically correct choice.

Both Image-based variants produce clouds in a normalized `[-1, -0.1]`
Z range in the OpenGL camera frame (right-handed, +y up, −z forward) —
the `-0.1` near plane keeps brightest-intensity (closest) pixels at
their correct angular position rather than collapsing them all to the
camera origin. Absolute world-space scale is not preserved for the
Image variant (`depth_map_to_image`'s per-image min-max normalization
discards units before the constructor sees the depth).

### Fold primitives

Two seed/combine functions for accumulating clouds across rows — the
canonical pair for a `SCAN` fold that grows one cloud from a stream of
per-row inputs (e.g. unprojecting a depth model frame-by-frame and
combining them into a single artifact).

```sql
SELECT SCAN world = pc_fuse(world, point_cloud_from_depth_orthographic(image, models.midas_small(image), 60))
  INIT pc_empty()
  OVER (ORDER BY idx)
  AS world_t
FROM frames
```

| Function | Returns | Description |
|----------|---------|-------------|
| `pc_empty()` | `PointCloud` | Zero-point, position-only, unorganized cloud. The SCAN INIT seed for PointCloud accumulator folds — the first `pc_fuse` adopts the producer's coordinate frame. |
| `pc_fuse(a, b)` | `PointCloud` | Concatenates two PointClouds (a.points ++ b.points), unioning bounding boxes. Output is always unorganized. Output carries color iff both inputs do; mixed (one with, one without) drops color rather than inventing it. Coordinate frames must agree, or one side must be `Unspecified`. No deduplication, no voxel-grid downsample. |
| `pc_transform(pc, pose)` | `PointCloud` | Applies a 4×4 affine transformation matrix (`pose` is a 16-element row-major `Float32[]`) to every position; rows 0–2 hold rotation+translation, row 3 is ignored (no projective division). Translation lives in the 4th column of each row. Color/normals preserved verbatim; coordinate frame tag preserved; bbox recomputed exactly. |
| `pose_translate(dx, dy, dz)` | `Float32[]` | Builds a 4×4 affine translation matrix as a 16-element row-major `Float32[]`. Equivalent to the literal `[1,0,0,dx, 0,1,0,dy, 0,0,1,dz, 0,0,0,1]::Float32[]`. |

A typical per-frame layout — fold N depth-derived clouds into a shared
world by shifting each frame's cloud by its frame index along Z:

```sql
SELECT SCAN world = pc_fuse(world, pc_transform(pc, pose_translate(0, 0, -idx * 0.1)))
  INIT (pc_empty())
  OVER (ORDER BY idx)
  AS world_t
FROM (
    SELECT ROW_NUMBER() OVER (ORDER BY file_name) AS idx,
           point_cloud_from_depth_orthographic(file, models.midas_small(file), 60) AS pc
    FROM frames
) t
```

### Accessors

| Function | Returns | Description |
|----------|---------|-------------|
| `point_cloud_count(pc)` | `Int32` | Number of points in the cloud. |
| `point_cloud_width(pc)` | `Int32` | Grid width for organized clouds; `0` for unorganized. |
| `point_cloud_height(pc)` | `Int32` | Grid height for organized clouds; `0` for unorganized. |
| `point_cloud_is_organized(pc)` | `Boolean` | True when `width × height` matches the point count. |
| `point_cloud_has_color(pc)` | `Boolean` | True when the cloud carries per-point RGBA color. |
| `point_cloud_bbox_min(pc)` | `Point3D` | Component-wise minimum corner of the axis-aligned bounding box. |
| `point_cloud_bbox_max(pc)` | `Point3D` | Component-wise maximum corner of the axis-aligned bounding box. |
| `point_cloud_depth(pc)` | `Image` | Reconstructs a grayscale-as-RGBA depth Image from an organized cloud (inverse of `point_cloud_from_depth_*`); throws for unorganized clouds. |

### Exporters

| Function | Format | Best for |
|----------|--------|----------|
| `point_cloud_to_ply(pc)` | Binary PLY (`.ply`) | Universal point-cloud interchange: MeshLab, CloudCompare, Open3D, PCL, Blender's PLY importer. Emits `binary_little_endian` with x/y/z floats and (when present) red/green/blue uchar per point; alpha is dropped. Always emits in OpenGL right-handed +Y-up frame; auto-converts from `CameraOpenCv` source clouds. |

Returns `UInt8[]` so it composes with the `INTO 'file.ext'` clause the
same way the mesh exporters do.

```sql
SELECT point_cloud_to_ply(world_t)
FROM (
    SELECT SCAN world = pc_fuse(world, point_cloud_from_depth_orthographic(image, models.midas_small(image), 60))
      INIT pc_empty()
      OVER (ORDER BY idx)
      AS world_t
    FROM frames
)
ORDER BY idx DESC LIMIT 1
```

```sql
-- Inspect a cloud's shape
SELECT
  point_cloud_count(cloud)        AS points,
  point_cloud_width(cloud)        AS w,
  point_cloud_height(cloud)       AS h,
  point_cloud_bbox_min(cloud).z   AS near_z,
  point_cloud_bbox_max(cloud).z   AS far_z
FROM (SELECT point_cloud_from_depth_orthographic(image, models.midas_small(image), 60.0) AS cloud
      FROM photos LIMIT 1)
```

## Meshes

`Mesh` is a triangulated 3D surface — explicit topology (triangle
indices) on top of a vertex buffer. Storage is a single byte blob: a
48-byte header (vertex count, triangle count, axis-aligned bounding
box, coordinate-frame tag, flag bits) followed by an interleaved
per-vertex payload at a flag-derived stride, then triangle indices (3
× `uint32` per triangle).

Per-vertex stride builds from enabled flag bits:
- 12 bytes for position (always)
- + 4 bytes when `has_color` (RGBA uint8)
- + 12 bytes when `has_normals` (XYZ float32, unit length)

A mesh built from a depth-derived organized cloud is 28 bytes per vertex
(position + color + normals). The format reserves additional bytes for
per-vertex UVs and an embedded texture image at the blob tail — those
are populated when an ONNX mesh-from-image model produces them.

### Constructors

```sql
-- Promote an organized PointCloud to an explicit triangle mesh.
-- Each grid cell becomes two triangles; cells whose corner depths span
-- more than 5% of the cloud's bbox Z range are skipped, so depth edges
-- produce topology breaks rather than rubber-sheet skirts.
SELECT mesh_from_organized(cloud)
FROM (SELECT point_cloud_from_depth_orthographic(image, models.midas_small(image), 60.0) AS cloud
      FROM photos LIMIT 1)

-- Shortcuts that fuse the unprojection + triangulation steps. Same
-- two-variant signature as point_cloud_from_depth_* — Image depth from
-- the visualization model variants:
SELECT mesh_from_depth_orthographic(image, models.midas_small(image), 60) FROM photos LIMIT 1

-- ...or shape-aware Array<Float32>(h, w) depth from the *_meters model
-- variants (metric path, preserves real-world distances; pinhole is the
-- geometrically correct choice for physical-distance values):
SELECT mesh_from_depth_pinhole(image, models.zoedepth_nyu_kitti_meters(image), 60)
FROM photos LIMIT 1

-- Recompute per-vertex normals from a mesh's triangle topology
-- (normalized sum of adjacent face normals). Useful for meshes ingested
-- without normals, or to refresh after a topology-changing op.
SELECT mesh_compute_normals(m) FROM (SELECT mesh_from_organized(cloud) AS m FROM ...)
```

`mesh_from_organized` inherits color from the source cloud (when present)
and the coordinate frame unchanged. It always emits per-vertex normals
computed from the triangle topology, giving smooth shading across
continuous surfaces and sharp edges at the discontinuity-threshold
breaks. Orphan vertices (corners of skipped cells) are tolerated — they
remain in the vertex buffer but no triangle references them.

### Accessors

| Function | Returns | Description |
|----------|---------|-------------|
| `mesh_vertex_count(m)` | `Int32` | Number of vertices. |
| `mesh_triangle_count(m)` | `Int32` | Number of triangles. |
| `mesh_bbox_min(m)` | `Point3D` | Component-wise minimum corner of the axis-aligned bounding box. |
| `mesh_bbox_max(m)` | `Point3D` | Component-wise maximum corner. |
| `mesh_has_color(m)` | `Boolean` | True when the mesh has per-vertex RGBA color. |
| `mesh_has_normals(m)` | `Boolean` | True when the mesh has per-vertex unit normals. |
| `mesh_has_uvs(m)` | `Boolean` | True when the mesh has per-vertex UV texture coordinates. |
| `mesh_has_texture(m)` | `Boolean` | True when the mesh has an embedded encoded texture image. |

### Exporters

| Function | Format | Best for |
|----------|--------|----------|
| `mesh_to_gltf(m)` | Binary glTF 2.0 (`.glb`) | Blender / Unity / Three.js / web 3D viewers / VS Code's glTF preview extension. Vertex colors emitted via `KHR_materials_unlit` so they render correctly regardless of the consumer's default lighting. |
| `mesh_to_stl(m)` | Binary STL | Universal 3D-printing format. Read by every slicer (Bambu Studio, PrusaSlicer, Cura, Lychee, ChiTuBox). Loses color and per-vertex normals — STL stores only triangle positions + face normals. |
| `mesh_to_obj(m)` | Wavefront OBJ (UTF-8 text) | Interchange with MeshLab / CloudCompare / Open3D / Blender. Per-vertex colors via the `v X Y Z R G B` extension recognized by those tools; bare-spec OBJ readers ignore the trailing color triplet. |

All three return `UInt8[]` so they compose with the `INTO 'file.ext'`
clause uniformly:

```sql
SELECT mesh_to_gltf(mesh_from_depth_orthographic(image, models.midas_small(image), 60.0))
FROM photos LIMIT 1
```

Every exporter emits in the destination format's canonical orientation
— right-handed, +Y up, −Z forward (the glTF / OBJ / STL standard). When
the source mesh declares `CameraOpenCv` as its coordinate frame, the
exporters automatically apply the 180° rotation around the X axis so
the output renders correctly in any consumer.

## Coordinate Frames

Both `PointCloud` and `Mesh` tag their blob with a coordinate-frame
identifier so producers can self-describe and consumers can transform
without out-of-band agreement:

| Frame | Convention | Used by |
|-------|------------|---------|
| `CameraOpenGl` | Right-handed, +x right, +y up, −z forward | Default for depth-map unprojection. Matches OpenGL / Three.js / glTF camera-space conventions; renderers in this family upload positions without a basis swap. |
| `CameraOpenCv` | Right-handed, +x right, +y down, +z forward | Classical RGB-D unprojection (RealSense, Kinect). Exporters automatically convert to `CameraOpenGl` orientation on output. |
| `Unspecified` | No commitment | Used by hand-built / test values where the coordinate frame is irrelevant. |

The frame is exposed indirectly — accessors return raw position values
in the cloud / mesh's declared frame; the exporters and the built-in 3D
viewer handle frame conversion implicitly so most callers don't think
about it.

## Constraints

`PointCloud` and `Mesh` cannot be used as a `PRIMARY KEY` or composite-
key column. Both kinds are blob payloads with no canonical sort
encoding. The full list of permitted key kinds lives in
[DDL / DML — Column modifiers](ddl-dml.md#column-modifiers).

`Point2D` and `Point3D` are also rejected as key columns — they have no
canonical sort order across components — but participate in every other
expression context like any other DataKind.

## See Also

- [Type System](type-system.md) — overall DataKind reference; spatial entries link back here.
- [Examples — Depth maps and 3D point clouds](examples.md#depth-maps-and-3d-point-clouds) — step-by-step workflow walkthrough.
- [Models](../models.md) — depth estimators (`midas-small`, `dpt-large`, `zoedepth-nyu-kitti` + metric variants, `glpn-nyu` + metric variant).
- [Image Functions](../functions/image.md) — `apply_colormap`, `depth_map_to_image`, and the broader image-manipulation surface.
- [`.datum` Format](../datum-format.md) — blob storage layout.
