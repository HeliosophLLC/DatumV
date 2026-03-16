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
| `DateTime` | Date and time with UTC offset | `DateTimeOffset` |
| `Duration` | Elapsed time span | `TimeSpan` |
| `String` | Variable-length UTF-8 text | `string` |
| `Uuid` | 128-bit UUID (RFC 9562) | `Guid` |
| `Image` | Encoded image bytes (PNG, JPG, WebP, etc.) | `byte[]` |
| `Audio` | Encoded audio bytes (WAV, MP3, FLAC, OGG, M4A) | `byte[]` |
| `Video` | Encoded video bytes (MP4, WebM, AVI, MKV) | `byte[]` |
| `Json` | A JSON document, stored as canonical CBOR bytes | `byte[]` (CBOR) |
| `Struct` | Named, ordered collection of heterogeneous fields | `DataValue[]` (field names in `ColumnInfo.Fields`) |
| `Point2D` | 2D point with single-precision X, Y components | `System.Numerics.Vector2` (8 bytes inline) |
| `Point3D` | 3D point with single-precision X, Y, Z components | `System.Numerics.Vector3` (12 bytes inline) |
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

### Type Literals and typeof()

Type names (`Int32`, `Float64`, `String`, `Boolean`, `Date`, `DateTime`, etc.)
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
`Float64`, `Decimal`, `String`, `Date`, `DateTime`, `Time`, `Duration`,
`Uuid`, `Image`, `Audio`, `Video`, `Json`, `Struct`, `Point2D`, `Point3D`,
`Type`) are reserved in expression position. They produce a `Type` value that can be compared with
`typeof()` results using `=`, `!=`, `IN`, `CASE`, and `IS`.

To use a type name as a column alias or table name, double-quote it:

```sql
SELECT 1 AS "Int32"
CREATE TEMP TABLE "String" (id Int32, value String)
```

Type names in non-expression contexts (column names in DDL, aliases after `AS`,
table names after `FROM`) are accepted without quoting.

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
- **Any numeric/Boolean/Date/DateTime/Time/Duration/Uuid ↔ String** — formats or parses.
- **Date ↔ DateTime** — midnight UTC or drop time component.
- **DateTime → Time** — extract time-of-day.
- **Date/DateTime → numeric** — epoch days or epoch seconds.
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

### Transaction-Stable Temporal Constants

PostgreSQL-compatible keywords that return the current date/time. All references within a statement batch resolve to the **same value** (the batch start time), matching PostgreSQL's transaction-stable semantics.

```sql
CURRENT_DATE                -- Date (UTC)
CURRENT_TIME                -- Time (UTC)
CURRENT_TIME(precision)     -- Time truncated to p fractional-second digits (0–6)
CURRENT_TIMESTAMP           -- DateTime (UTC)
CURRENT_TIMESTAMP(precision)-- DateTime truncated to p fractional-second digits
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
| `timeofday()` | None | Like `clock_timestamp()`, but returns a formatted String instead of DateTime. |

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

`field` is a bare keyword (not a string) — any field supported by `date_part()`. `source` is a Date, DateTime, or Time expression.

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

Converts a `DateTime` value to a specific timezone. The instant in time is preserved — only the UTC offset (and therefore the displayed local time) changes. Uses IANA timezone names.

```sql
expr AT TIME ZONE 'timezone_name'
```

```sql
-- UTC pickup time → local New York time
SELECT pickup_datetime AT TIME ZONE 'America/New_York' AS local_time
FROM trips

-- Compare timestamps across zones (no parentheses needed)
SELECT * FROM events
WHERE created_at AT TIME ZONE 'America/New_York' = updated_at AT TIME ZONE 'UTC'

-- Extract local hour after converting
SELECT date_part('hour', pickup_datetime AT TIME ZONE 'America/New_York') AS local_hour
FROM trips

-- Extract UTC offset in seconds (e.g. -18000 for EST, -14400 for EDT)
SELECT date_part('timezone', pickup_datetime AT TIME ZONE 'America/New_York') AS tz_offset
FROM trips
```

Timezone names follow the [IANA tz database](https://www.iana.org/time-zones) (e.g. `America/New_York`, `Europe/London`, `Asia/Kolkata`, `UTC`). DST transitions are handled automatically.


## See Also

- [Parameterized Queries](parameters.md)
- [DDL / DML](ddl-dml.md)
- [SELECT](select.md)
