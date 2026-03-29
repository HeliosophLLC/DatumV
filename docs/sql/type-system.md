---
title: Type System
---

## Why Use This

DatumIngest has more types than standard SQL -- typed arrays for embeddings and tensors, durations for time spans, images as a first-class kind, structs for nested values. If you are coming from a regular SQL database, the type system is where you will notice the difference first. Understanding it helps you avoid unexpected cast errors and pick the right types for your ML features.

## Quick Start

Three common type tasks:

**"What type is this column?"**

```sql
SELECT typeof(column_name) FROM my_table LIMIT 1
```

**"Convert a string to a number"**

```sql
-- Hard cast: errors if conversion fails
SELECT CAST(score AS Float64) FROM data

-- Safe cast: returns NULL instead of erroring
SELECT try_cast(score, Float64) FROM data
```

**"Handle mixed types in a column"**

```sql
-- Filter to only rows where value is a number
SELECT * FROM data WHERE value IS Float64

-- Type-narrowing bind: check and use the typed value in one step
SELECT * FROM data WHERE value AS Float64 v AND v > 0.5
```

### DataKind values

| DataKind | Description | Internal representation |
|----------|-------------|------------------------|
| `Boolean` | True or false | `bool` |
| `UInt8` | Unsigned 8-bit integer (0–255) | `byte` |
| `UInt16` | Unsigned 16-bit integer | `ushort` |
| `UInt32` | Unsigned 32-bit integer | `uint` |
| `UInt64` | Unsigned 64-bit integer | `ulong` |
| `UInt128` | Unsigned 128-bit integer | `System.UInt128` |
| `Int8` | Signed 8-bit integer (−128–127) | `sbyte` |
| `Int16` | Signed 16-bit integer | `short` |
| `Int32` | Signed 32-bit integer | `int` |
| `Int64` | Signed 64-bit integer | `long` |
| `Int128` | Signed 128-bit integer | `System.Int128` |
| `Float16` | 16-bit IEEE 754 binary16 float | `System.Half` |
| `Float32` | 32-bit IEEE 754 float | `float` |
| `Float64` | 64-bit IEEE 754 double | `double` |
| `Decimal` | 128-bit decimal floating point | `decimal` |
| `Date` | Calendar date (no time component) | `DateOnly` |
| `Time` | Time of day (no date component) | `TimeOnly` |
| `Timestamp` | Naive wall-clock date and time, no time zone (PG `timestamp` / `timestamp without time zone`) | `DateTime` (`DateTimeKind.Unspecified`) |
| `TimestampTz` | Date and time with time zone (PG `timestamptz` / `timestamp with time zone`). Input offset is normalised to UTC at construction and discarded — two values for the same instant compare and hash equal regardless of input offset. | `DateTimeOffset` (offset always `TimeSpan.Zero` on readback) |
| `Duration` | Elapsed time span | `TimeSpan` |
| `String` | Variable-length UTF-8 text | `string` |
| `Uuid` | 128-bit UUID (RFC 9562) | `Guid` |
| `Image` | Encoded image bytes (PNG, JPG, WebP, etc.) | `byte[]` |
| `Audio` | Encoded audio bytes (WAV, MP3, FLAC, OGG, M4A) | `byte[]` |
| `AudioSlice` | Lazy handle to a windowed slice of `Audio` (runtime-only; reserved for audio inference workloads) | `(audio_id, start_sample, end_sample_exclusive)` inline |
| `Video` | Encoded video bytes (MP4, WebM, AVI, MKV) | `byte[]` |
| `VideoFrame` | Lazy handle to a single frame of `Video` (runtime-only) | `(video_id, frame_index)` inline |
| `VideoSlice` | Lazy handle to a frame-range of `Video` (runtime-only; reserved for temporal-model workloads) | `(video_id, start_frame, end_frame_exclusive)` inline |
| `Json` | A JSON document, stored as canonical CBOR bytes | `byte[]` (CBOR) |
| `Struct` | Named, ordered collection of heterogeneous fields | `DataValue[]` (field names in `ColumnInfo.Fields`) |
| `Point2D` | 2D point with single-precision X, Y components | `System.Numerics.Vector2` (8 bytes inline) |
| `Point3D` | 3D point with single-precision X, Y, Z components | `System.Numerics.Vector3` (12 bytes inline) |
| `PointCloud` | Dense 3D point collection with optional per-point color | `byte[]` (40-byte header + interleaved per-point payload) |
| `Mesh` | Triangulated 3D surface with optional per-vertex color, normals, UVs, embedded texture | `byte[]` (48-byte header + interleaved per-vertex payload + triangle indices + optional embedded texture) |
| `Type` | A type tag describing another DataKind | `DataKind` enum value (stored as byte) |

#### Arrays

Arrays are not a separate `DataKind`. Any primitive kind can carry an
orthogonal `IsArray` flag, turning a single value into an ordered sequence of
values of that kind. `Float32` with `IsArray` is what you'd call a vector;
`UInt8` with `IsArray` is a raw byte buffer.

Array columns can optionally declare a **fixed shape** at `CREATE TABLE` time —
useful for embeddings, kernels, and anything where every row must carry the
same dimensionality. Three equivalent surface forms:

```sql
CREATE TABLE t (
    embedding  Float32[384],              -- 1D fixed length
    weights    Array<Float32>(3, 3),      -- 2D shape (stored flat, 9 elements)
    feature    Array<Int8>(256),          -- generic-wrapper form
    bag        Float32[]                  -- no fixed shape (variable length)
);
```

`Float32[N]` and `Array<Float32>(N)` produce the same column; the bracket form
only carries a single dimension, the paren form generalises to N-D. Multi-dim
shapes are stored flat row-major — `Array<Float32>(3, 3)` is a 9-element
`Float32[]` with shape metadata on the side. INSERT-time enforcement rejects
any value whose element count doesn't match the declared product of dimensions.

#### String width — `VARCHAR(N)` / `CHAR(N)` / `TEXT`

`String` is a single kind but accepts SQL-standard aliases that pin its
declared maximum length:

| SQL syntax | Stored as | Semantics |
|------------|-----------|-----------|
| `String`, `TEXT`, `VARCHAR` (bare) | `String` with no max length | Unbounded UTF-8. |
| `String(N)`, `VARCHAR(N)` | `String` with `MaxLength = N` | INSERT rejects values longer than `N` characters. |
| `CHAR(N)` | `String` with `MaxLength = N` and blank-padding | Like `VARCHAR(N)` plus right-pad short values with spaces. |
| `CHAR` (bare) | `String` with `MaxLength = 1` and blank-padding | PG convention — bare `CHAR` is `CHAR(1)`. |

Lengths are measured in **characters**, not bytes — `VARCHAR(5)` accepts `'héllo'`
(5 chars, 6 UTF-8 bytes). `information_schema.columns` surfaces both the
PG-standard `data_type` (`'character varying'`, `'character'`, `'text'`) and the
DatumIngest-native `data_kind` (`'String'`), plus `character_maximum_length` and
`is_blank_padded` for full round-trip.

#### Video Frames

`VideoFrame` is a runtime-only handle into a registered video — `(video_id, frame_index)` packed inline in the `DataValue`, no pixel bytes attached. It exists so iterating frames of a long video doesn't materialise gigabytes of decoded pixels: each frame stays a 12-byte handle until something asks for its pixels.

You'll typically produce `VideoFrame` values from `video_unnest_frames(source [, start_frame [, stride [, max_frames]]])` and consume them with `video_frame_to_image(frame [, target_width [, target_height]])`:

```sql
-- All frames of a stored video, decoded at source resolution
SELECT video_frame_to_image(f.frame) AS img
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video) AS f

-- Every 5th frame, decoded at 384px width (height auto-preserves aspect)
SELECT video_frame_to_image(f.frame, 384) AS img
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video, 0, 5, 100) AS f
```

The `Video` source may be a column value (sidecar- or arena-backed), or a STRING file path. Inside a query, every `VideoFrame` resolves through a per-query video registry that holds a warm FFmpeg decoder per source — sequential frame access (frame N → N+1 → N+2) hits the fast path; backward access seeks to the file head and decodes forward.

Cells of kind `VideoFrame` render in the results pane as `videoframe[#42]` — the index identifies the frame within the source video. The pixel content is not implicit; call `video_frame_to_image` to surface an `Image` the viewer can display.

`AudioSlice` and `VideoSlice` follow the same pattern (lazy time-range handles into a registered source) and are reserved for upcoming audio-inference (Whisper, Silero VAD, pyannote) and temporal-video workloads.

#### Points

`Point2D` and `Point3D` are first-class scalar kinds for spatial coordinates.
Both store single-precision (`Float32`) components packed inline in the
`DataValue` (8 bytes for `Point2D`, 12 bytes for `Point3D`) — no arena
allocation, no sidecar reference. They round-trip through the `.datum` v2
format using the FixedWidth encoder.

Build points with the `point2d` / `point3d` constructors and project
components with `point_x` / `point_y` / `point_z`:

```sql
-- Construct a Point3D column from three numeric columns
SELECT point3d(x, y, z) AS pt FROM raw_lidar

-- Project the X axis back out for filtering
SELECT * FROM samples WHERE point_x(pt) > 0.0

-- Mixed numeric inputs widen to Float32
SELECT point2d(latitude, longitude) FROM cities  -- accepts Float64, Int32, etc.
```

`distance(a, b)` returns the Euclidean distance between two same-dimension
points as `Float32`; `distance_sq(a, b)` returns the squared distance and
skips the square root — useful for KNN ranking and threshold checks where
the absolute distance is not needed:

```sql
-- Find points within a radius
SELECT id, distance(pt, point3d(0, 0, 0)) AS r
FROM cloud
WHERE distance_sq(pt, point3d(0, 0, 0)) < 100.0
ORDER BY r

-- Mixing Point2D with Point3D is a function-argument error.
```

`Point2D` and `Point3D` participate in `typeof()`, `IS Type`, and `CAST` like
any other DataKind. `point_z` accepts only `Point3D` since `Point2D` has no Z
component.

#### Point Clouds and Meshes

`PointCloud` and `Mesh` are the container spatial kinds — byte-blob
payloads designed to turn 2D-per-pixel data (depth maps, RGB-D scans,
photogrammetry) into renderable / exportable 3D structure.

`PointCloud` is a dense collection of 3D points with optional per-point
color, designed for depth-map unprojection, LiDAR / RGB-D scans, and any
workflow that produces a point-per-pixel structure. Storage is a single
byte blob: a 40-byte header (point count, axis-aligned bounding box,
coordinate-frame tag, optional grid dimensions) followed by an
interleaved per-point payload at a fixed stride — 12 bytes (xyz
`Float32`) per point when position-only, 16 bytes (xyz `Float32` + rgba
`UInt8`) when color is present.

`Mesh` is the triangulated-surface companion — explicit topology
(triangle indices) on top of a vertex buffer, exportable as `.glb` /
`.stl` / `.obj` so the result survives outside DatumIngest. Per-vertex
attributes (position, optional color, optional unit normals, future UVs)
follow the same flag-derived-stride pattern as PointCloud.

Both `PointCloud` and `Mesh` participate in `typeof()`, `IS Type`, and
column DDL like any other DataKind. Neither can be used as a `PRIMARY
KEY` or composite-key column — see [DDL / DML](ddl-dml.md) for the
comparability rules.

A quick example to anchor what's possible — turn a photo into a
Blender-loadable 3D mesh in one statement:

```sql
SELECT mesh_to_gltf(mesh_from_depth_orthographic(file_bytes, models.midas_small(file_bytes), 60.0))
INTO 'scene.glb' FROM photos LIMIT 1
```

The full constructor / accessor / exporter reference for `PointCloud`
and `Mesh` — plus coordinate-frame details, the `point_cloud_from_depth_*`
+ `mesh_from_depth_*` shortcuts, and the choice between orthographic
and pinhole projection — lives in [Spatial Types](spatial.md).

### Type Literals and typeof()

Type names (`Int32`, `Float64`, `String`, `Boolean`, `Date`, `Timestamp`, `TimestampTz`, etc.)
are reserved keywords in expression position. They produce a `Type` value — a
first-class type tag rather than a string. This enables type-oriented
comparisons without string matching.

#### typeof()

`typeof(expr)` returns the runtime `DataKind` of its argument as a `Type` value.
The result can be projected, compared, and used in any expression context:

```sql
-- Project the runtime type as a column (displays "Float64", "String", etc.)
SELECT name, typeof(value) AS value_type FROM data

-- Filter rows by type
SELECT * FROM mixed_data WHERE typeof(value) = Int32

-- Multiple type check with IN
SELECT * FROM t WHERE typeof(col) IN (Int32, Int64, Float32, Float64)
```

#### can_cast()

`can_cast(expr, Type)` returns whether a value can be safely cast to the target
type without data loss, overflow, or parse failure. Unlike `typeof()` which
checks type identity, `can_cast` checks value representability:

```sql
-- typeof checks identity: is x already an Int32?
SELECT * FROM t WHERE typeof(x) = Int32

-- can_cast checks castability: will CAST succeed without error?
SELECT * FROM t WHERE can_cast(x, UInt8)       -- false for 5000, true for 200
SELECT * FROM t WHERE can_cast(x, Int32)       -- true for 3.14 (truncates to 3)
SELECT * FROM t WHERE can_cast(name, Date)     -- false for "abc", true for "2024-06-15"
```

`can_cast` matches CAST semantics: truncation of fractional parts is allowed
(it's not data loss — it's expected CAST behavior). Only overflow (value outside
the target range) and parse failures return false. Widening conversions (e.g.,
Int32 → Float64) always return true. Unsupported pairs (e.g., Image → Int32)
return false.

#### try_cast()

`try_cast(expr, Type)` attempts the same conversion as `CAST`, but returns NULL
on failure instead of throwing. It follows CAST semantics on success — including
numeric truncation (e.g., `try_cast(3.99, Int32)` returns `3`).

```sql
-- Returns NULL for unparseable strings instead of erroring
SELECT try_cast(raw_value, Int32) AS parsed_int FROM raw_data

-- Combine with COALESCE for defaults
SELECT COALESCE(try_cast(score, Float64), 0.0) AS safe_score FROM t

-- Filter rows that can be converted (similar to can_cast but gives you the value)
SELECT try_cast(x, Date) AS parsed_date FROM t WHERE try_cast(x, Date) IS NOT NULL
```

**can_cast vs try_cast:** Both follow CAST semantics — truncation is allowed
(e.g., `can_cast(3.14, Int32)` returns true because `CAST(3.14 AS Int32)`
succeeds with value 3). Only overflow and parse failures return false/NULL.
`can_cast` returns Boolean; `try_cast` returns the converted value or NULL.

#### IS [NOT] Type

The `IS` predicate provides a concise shorthand for type checks. It desugars to
a `typeof()` comparison — no new semantics, just cleaner syntax:

```sql
-- These pairs are equivalent:
SELECT * FROM t WHERE x IS Int32
SELECT * FROM t WHERE typeof(x) = Int32

SELECT * FROM t WHERE x IS NOT String
SELECT * FROM t WHERE typeof(x) != String
```

`IS NULL` / `IS NOT NULL` continue to work unchanged — `NULL` is a distinct
keyword, so there is no ambiguity.

#### Type-narrowing bind

When you need to both check a type and use the narrowed value, the
`expr AS Type name AND ...` syntax combines both in a single expression:

```sql
-- Check that x can be safely cast to Int32, then use the typed value
WHERE x AS Int32 y AND y > 0

-- Desugars to:
WHERE can_cast(x, Int32) AND CAST(x AS Int32) > 0
```

The guard uses `can_cast()`, not `typeof()` — it checks *value representability*,
not just type identity. For example, `5000 AS UInt8 y` fails because 5000 doesn't
fit in a UInt8, even though 5000 is a valid Int32. This prevents silent data loss
from overflow and truncation.

The binding name (`y`) is scoped to the right side of the same `AND` — it is
replaced with `CAST(source AS Type)` during parsing. The name does not exist in
SELECT, ORDER BY, or any other clause.

```sql
-- Compound conditions — y is substituted in all right-side predicates
WHERE x AS Int32 y AND y > 0 AND y < 100

-- OR branches — each bind is independent
WHERE (x AS Int32 y AND y > 0) OR (x AS String z AND len(z) > 3)

-- Complex source expressions — avoids repeating the expression
WHERE json_value(data, '$.score') AS Float64 score AND score > 0.5
```

The pattern is restricted: `AS Type name` must appear as the left operand of
`AND`. Using it standalone, in `OR` directly, or referencing the name outside
the AND body is a parse error.

#### CASE on type

Simple CASE with `typeof()` enables type-driven branching:

```sql
SELECT CASE typeof(x)
    WHEN Int32   THEN round(x, 0)
    WHEN Float64 THEN round(x, 2)
    WHEN String  THEN len(x)
    ELSE NULL
END AS result
FROM t
```

#### Type literal rules

`DataKind` names (`Boolean`, `Int8`, `Int16`, `Int32`, `Int64`, `Int128`,
`UInt8`, `UInt16`, `UInt32`, `UInt64`, `UInt128`, `Float16`, `Float32`,
`Float64`, `Decimal`, `String`, `Date`, `Timestamp`, `TimestampTz`, `Time`, `Duration`,
`Uuid`, `Image`, `Audio`, `AudioSlice`, `Video`, `VideoFrame`, `VideoSlice`,
`Json`, `Struct`, `Point2D`, `Point3D`, `PointCloud`, `Mesh`, `Type`) are
reserved in expression position. They produce a `Type` value that can be
compared with `typeof()` results using `=`, `!=`, `IN`, `CASE`, and `IS`.

Type names are accepted without quoting in name positions: column names in
DDL, aliases after `AS`, table names after `FROM`, and column names after a
dot in a qualified reference. Bare type names in expression position
(`CAST(x AS Video)`, `typeof(x) = Int32`) remain type literals.

```sql
SELECT t.video FROM media.video AS t       -- column `video` in table `video`
WHERE t.float32 > 0                         -- column named `float32`
SELECT CAST(x AS Video) FROM t              -- type literal in expression position

-- Aliasing-as is still legal:
SELECT 1 AS "Int32"
```

The handful of edge cases that still require quoting are pure-expression
positions where the parser must choose between a column reference and a type
literal — column aliases like `AS "Int32"` (where `Int32` would otherwise be
read as the cast target) and bare-identifier column references where the
column is named after a type. Qualified references (`t.video`) and DDL/FROM
positions never need quoting.

### Type conversions

#### Implicit widening

When an operator or function receives mixed types, the engine automatically
widens both sides to the narrowest common type. The numeric widening chain
is roughly:

```
Boolean → UInt8 ─→ Int16 ─→ Int32 ─→ Int64 ─→ Float64
           Int8 ↗    UInt16 ↗   UInt32 ↗   UInt64 ↗
                                            Float16 ↗
                                            Float32 ↗
                                           Duration ↗
```

Widening is transitive — `UInt8` can reach `Float64` by following the chain
through `Int16 → Int32 → Int64 → Float64`. Same-kind is always a no-op. The
128-bit kinds (`Int128`, `UInt128`, `Decimal`) participate as widening
targets from their respective lanes; element-wise widening across an array
applies to each element independently.

#### Explicit conversion (CAST)

`CAST` converts between any supported type pair. Three equivalent syntaxes:

```sql
CAST(x AS Int32)     -- SQL-standard syntax
x::Int32             -- PostgreSQL-style postfix cast
cast(x, Int32)       -- function-call syntax with type literal
```

The `::` form is pure syntactic sugar — it parses to the same AST as
`CAST(x AS Int32)`. It binds tighter than any binary operator, so
`a + b::Int32` is `a + (b::Int32)`. Chains left-to-right
(`x::Int32::String` is `(x::Int32)::String`) and interleaves with array
subscripts (`a[0]::Int32`, `x::Int32[]`).

Supported conversions include:

- **Any numeric ↔ any numeric** — truncates fractional parts, wraps on integer
  overflow (UInt8 saturates at 0–255 instead of wrapping).
- **Any numeric ↔ Boolean** — zero = false, non-zero = true.
- **Any numeric/Boolean/Date/Timestamp/TimestampTz/Time/Duration/Uuid ↔ String** — formats or parses.
- **Date ↔ Timestamp / TimestampTz** — midnight UTC or drop time component.
- **Timestamp ↔ TimestampTz** — reinterprets the wall-clock ticks as UTC. PG converts via the session time zone; until a session-TZ concept lands, DatumIngest assumes UTC for both directions.
- **Timestamp / TimestampTz → Time** — extract time-of-day.
- **Date / Timestamp / TimestampTz → numeric** — epoch days or epoch seconds.
- **Time/Duration ↔ numeric** — seconds since midnight or total seconds.
- **`UInt8` array ↔ Image / Audio / Video** — byte reinterpretation between a raw byte buffer and the corresponding encoded blob kind.
- **String ↔ Json** — `cast(text, Json)` parses the JSON text into canonical CBOR; `cast(json, String)` re-emits the canonical form as text. `try_cast` returns NULL on parse failure.

Use `can_cast(x, Type)` to check if a conversion is lossless before casting, or
`try_cast(x, Type)` to get NULL on failure instead of an error. The function-call
syntax composes naturally with both:

```sql
-- Safe conversion pipeline
SELECT try_cast(raw_value, Float64) AS parsed FROM t
SELECT * FROM t WHERE can_cast(score, Int32) AND cast(score, Int32) > 0
```

#### Arithmetic kind promotion

Binary arithmetic (`+ - * % / **`) and unary negate pick a result kind
from the operand kinds. The rules mirror C# integer-promotion semantics
plus a SQL-ergonomic adjustment for division:

| Operands | `+ - * %` | `/` | `**` |
| --- | --- | --- | --- |
| Two integers ≤ 32 bits (or with `Boolean`) | `Int32` | `Float32` | `Float32` |
| Any `UInt32` / `Int64` / `UInt64` operand | `Int64` | `Float32` | `Float32` |
| Any `Int128` / `UInt128` operand | `Int128` | `Float32` | `Float32` |
| Any `Decimal` operand | `Decimal` | `Decimal` | `Float64` |
| Any `Float64` operand | `Float64` | `Float64` | `Float64` |
| Any `Float32` operand (no `Float64`) | `Float32` | `Float32` | `Float32` |
| Any `Float16` operand (no wider float) | `Float32` | `Float32` | `Float32` |
| `Duration + Duration`, `Duration - Duration` | `Duration` | n/a | n/a |
| `Timestamp ± Duration`, `TimestampTz ± Duration` | same timestamp kind | n/a | n/a |
| `Timestamp - Timestamp`, `TimestampTz - TimestampTz` (same kind) | `Duration` | n/a | n/a |
| Any other `Time` / `Duration` mix | `Float32` | `Float32` | `Float32` |
| Any `String` operand | parsed → `Float64` | parsed → `Float64` | parsed → `Float64` |

So `Int64 + Int64 → Int64` (the wider integer is preserved),
`Decimal × Float → Float64`, `5 / 2 → Float32` (always float for SQL
ergonomics so users get `2.5`, not `2`), and `Int128 ± Int32 → Int128`.

If you want truncated integer division, cast the result:

```sql
SELECT CAST(a / b AS Int64) FROM t
```

The promoted result kind also flows through to schema introspection —
`SELECT a + b` reports the same kind in its output schema that the row
actually carries.

### Timestamp Semantics and Divergence from PostgreSQL

PostgreSQL `timestamp` (without time zone) and `timestamptz` (with time zone)
both store **8 bytes of microsecond/tick precision** and DatumIngest's
`Timestamp` / `TimestampTz` follow that convention. A few practical points:

- **Equality.** Two `TimestampTz` values for the same instant compare and
  hash equal regardless of the offset in the literal that produced them —
  `'2026-05-19T12:00:00-07:00'` and `'2026-05-19T19:00:00+00:00'` are
  byte-identical once stored. This matches PG.
- **Bare-literal time zone for `TimestampTz`.** PG assumes the **session
  time zone** when parsing a bare `'2026-05-19 12:00'` into a `timestamptz`.
  DatumIngest currently has no session-TZ concept and assumes **UTC** for
  bare literals. Literals with an explicit offset suffix
  (`'2026-05-19T12:00:00-07:00'`) work identically to PG. This is the
  one known PG-conformance gap; the workaround is to either include an
  explicit offset in every `timestamptz` literal or to use
  `AT TIME ZONE 'zone'` to re-anchor a value after construction.
- **`AT TIME ZONE`.** Kind-shifting operator — see [§ AT TIME
  ZONE](#at-time-zone) below.
- **`timestamp ↔ timestamptz` casts.** PG converts via the session TZ;
  DatumIngest assumes UTC for both directions. Explicit `AT TIME ZONE`
  is the recommended path for the few cases where this matters.

### Transaction-Stable Temporal Constants

PostgreSQL-compatible keywords that return the current date/time. All references within a statement batch resolve to the **same value** (the batch start time), matching PostgreSQL's transaction-stable semantics.

```sql
CURRENT_DATE                -- Date (UTC)
CURRENT_TIME                -- Time (UTC)
CURRENT_TIME(precision)     -- Time truncated to p fractional-second digits (0–6)
CURRENT_TIMESTAMP           -- TimestampTz (UTC)
CURRENT_TIMESTAMP(precision)-- TimestampTz truncated to p fractional-second digits
LOCALTIME                   -- Same as CURRENT_TIME (no session timezone)
LOCALTIME(precision)        -- Same as CURRENT_TIME(precision)
LOCALTIMESTAMP               -- Same as CURRENT_TIMESTAMP
LOCALTIMESTAMP(precision)   -- Same as CURRENT_TIMESTAMP(precision)
```

```sql
-- Filter to today's rows
SELECT * FROM orders WHERE order_date = CURRENT_DATE

-- Timestamp with millisecond precision
SELECT CURRENT_TIMESTAMP(3) AS ts

-- Both columns are identical (batch-stable)
SELECT CURRENT_TIMESTAMP AS a, CURRENT_TIMESTAMP AS b
```

`now()` and `current_time()` are also batch-stable — they resolve to the same constant at plan time. `LOCALTIME` / `LOCALTIMESTAMP` behave identically to `CURRENT_TIME` / `CURRENT_TIMESTAMP` because DatumIngest has no session timezone setting (all times are UTC).

#### Non-SQL-standard time functions

| Function | Stability | Description |
|----------|-----------|-------------|
| `transaction_timestamp()` | Batch | Same as `CURRENT_TIMESTAMP` / `now()`. Named to clearly reflect what it returns. |
| `statement_timestamp()` | Statement | Start time of the current statement. Same as `transaction_timestamp()` for the first statement in a batch; may differ for subsequent statements. |
| `clock_timestamp()` | None | Actual wall-clock time. Changes even within a single SQL statement. |
| `timeofday()` | None | Like `clock_timestamp()`, but returns a formatted String instead of TimestampTz. |

```sql
-- transaction_timestamp() = now() = CURRENT_TIMESTAMP within a batch
SELECT transaction_timestamp(), now(), CURRENT_TIMESTAMP

-- statement_timestamp() may differ from transaction_timestamp() in multi-statement batches
SELECT statement_timestamp()

-- clock_timestamp() changes between calls — useful for measuring elapsed time
SELECT clock_timestamp() AS before_work, clock_timestamp() AS after_work

-- timeofday() returns a human-readable string
SELECT timeofday()  -- e.g. '2026-04-15T14:30:45.1234567+00:00'
```

### EXTRACT

PostgreSQL-standard syntax for extracting date/time fields. Desugars to `date_part()` at parse time.

```sql
EXTRACT(field FROM source)
```

`field` is a bare keyword (not a string) — any field supported by `date_part()`. `source` is a Date, Timestamp, TimestampTz, or Time expression.

```sql
-- Extract year and month
SELECT EXTRACT(YEAR FROM order_date) AS y,
       EXTRACT(MONTH FROM order_date) AS m
FROM orders

-- ISO day of week (1=Monday, 7=Sunday)
SELECT EXTRACT(ISODOW FROM event_date) AS dow FROM events

-- Unix epoch seconds
SELECT EXTRACT(EPOCH FROM created_at) AS epoch_secs FROM logs

-- Century and millennium
SELECT EXTRACT(CENTURY FROM birth_date) AS century FROM people

-- Works with Time values
SELECT EXTRACT(HOUR FROM start_time) AS h FROM schedule
```

`EXTRACT(YEAR FROM x)` is exactly equivalent to `date_part('year', x)` — use whichever reads better in context. See the [full field list](../functions/temporal.md#date_part--extract-supported-parts) in the functions reference.

### AT TIME ZONE

PG-style kind-shifting operator that reinterprets a timestamp value against a named time zone. Uses IANA timezone names.

```sql
expr AT TIME ZONE 'timezone_name'
```

| Input kind | Result kind | Semantics |
|---|---|---|
| `TimestampTz` | `Timestamp` | "What does the clock read in this zone?" — converts the UTC instant to wall-clock time in `'timezone_name'` and returns a naive `Timestamp`. |
| `Timestamp` | `TimestampTz` | "Interpret this wall clock as being in this zone" — treats the naive value as a local time in `'timezone_name'`, converts to UTC, and returns a `TimestampTz`. |

```sql
-- TimestampTz → Timestamp: pickup_at is 2026-01-15 12:00 UTC, returns 07:00 (EST wall clock)
SELECT pickup_at AT TIME ZONE 'America/New_York' AS local_clock
FROM trips

-- Timestamp → TimestampTz: interpret a naive local time as NY-local, get the UTC instant
SELECT booking_local_time AT TIME ZONE 'America/New_York' AS utc_instant
FROM bookings

-- Round-trip back to the original instant by applying the same zone twice in opposite directions
SELECT (pickup_at AT TIME ZONE 'America/New_York') AT TIME ZONE 'America/New_York'
FROM trips
-- => Equal to pickup_at: NY wall clock → re-interpret as NY local → back to UTC
```

Timezone names follow the [IANA tz database](https://www.iana.org/time-zones) (e.g. `America/New_York`, `Europe/London`, `Asia/Kolkata`, `UTC`). DST transitions are handled automatically by the host's `TimeZoneInfo` lookup.


## See Also

- [Parameterized Queries](parameters.md)
- [DDL / DML](ddl-dml.md)
- [SELECT](select.md)
