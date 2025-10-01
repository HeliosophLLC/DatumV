---
title: Type System
---

### DataKind values

| DataKind | Description | Internal representation |
|----------|-------------|------------------------|
| `Boolean` | True or false | `bool` |
| `UInt8` | Unsigned 8-bit integer (0–255) | `byte` |
| `Int8` | Signed 8-bit integer (−128–127) | `sbyte` |
| `Int16` | Signed 16-bit integer | `short` |
| `UInt16` | Unsigned 16-bit integer | `ushort` |
| `Int32` | Signed 32-bit integer | `int` |
| `UInt32` | Unsigned 32-bit integer | `uint` |
| `Int64` | Signed 64-bit integer | `long` |
| `UInt64` | Unsigned 64-bit integer | `ulong` |
| `Float32` | 32-bit IEEE 754 float | `float` |
| `Float64` | 64-bit IEEE 754 double | `double` |
| `String` | Variable-length UTF-8 text | `string` |
| `Date` | Calendar date (no time component) | `DateOnly` |
| `DateTime` | Date and time with UTC offset | `DateTimeOffset` |
| `Time` | Time of day (no date component) | `TimeOnly` |
| `Duration` | Elapsed time span | `TimeSpan` |
| `Uuid` | 128-bit UUID (RFC 9562) | `Guid` |
| `JsonValue` | Raw JSON string for deferred parsing | `string` |
| `Vector` | Rank-1 float array | `float[]` |
| `Matrix` | Rank-2 float array | `float[]` + shape `[rows, cols]` |
| `Tensor` | N-dimensional float array | `float[]` + `int[]` shape |
| `UInt8Array` | Raw byte array | `byte[]` |
| `Image` | Encoded image bytes | `byte[]` |
| `Array` | Ordered sequence of same-typed values | `DataValue[]` |
| `Struct` | Named, ordered collection of heterogeneous fields | `DataValue[]` (field names in `ColumnInfo.Fields`) |
| `Type` | A type tag describing another DataKind | `DataKind` enum value (stored as byte) |

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
Int32 → Float64) always return true. Unsupported pairs (e.g., Vector → Int32)
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

All `DataKind` names (`Boolean`, `Int8`, `Int16`, `Int32`, `Int64`, `UInt8`,
`UInt16`, `UInt32`, `UInt64`, `Float32`, `Float64`, `String`, `Date`,
`DateTime`, `Time`, `Duration`, `Uuid`, `JsonValue`, `Vector`, `Matrix`,
`Tensor`, `UInt8Array`, `Image`, `Array`, `Struct`, `Type`) are reserved in
expression position. They produce a `Type` value that can be compared with
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
widens both sides to the narrowest common type. The widening chain is:

```
Boolean → UInt8 ─→ Int16 ─→ Int32 ─→ Int64 ─→ Float64 → Vector → Tensor
           Int8 ↗    UInt16 ↗   UInt32 ↗   UInt64 ↗
                                             Float32 ↗
                                            Duration ↗
                                      Matrix ──────────→ Tensor
```

Widening is transitive — `UInt8` can reach `Float64` by following the chain
through `Int16 → Int32 → Int64 → Float64`. Same-kind is always a no-op.

#### Explicit conversion (CAST)

`CAST` converts between any supported type pair. Two equivalent syntaxes:

```sql
CAST(x AS Int32)     -- SQL-standard syntax
cast(x, Int32)       -- function-call syntax with type literal
```

Supported conversions include:

- **Any numeric ↔ any numeric** — truncates fractional parts, wraps on integer
  overflow (UInt8 saturates at 0–255 instead of wrapping).
- **Any numeric ↔ Boolean** — zero = false, non-zero = true.
- **Any numeric/Boolean/Date/DateTime/Time/Duration/Uuid ↔ String** — formats or parses.
- **Date ↔ DateTime** — midnight UTC or drop time component.
- **DateTime → Time** — extract time-of-day.
- **Date/DateTime → numeric** — epoch days or epoch seconds.
- **Time/Duration ↔ numeric** — seconds since midnight or total seconds.
- **String ↔ JsonValue** — text reinterpretation.
- **UInt8Array ↔ Image** — byte reinterpretation.

Use `can_cast(x, Type)` to check if a conversion is lossless before casting, or
`try_cast(x, Type)` to get NULL on failure instead of an error. The function-call
syntax composes naturally with both:

```sql
-- Safe conversion pipeline
SELECT try_cast(raw_value, Float64) AS parsed FROM t
SELECT * FROM t WHERE can_cast(score, Int32) AND cast(score, Int32) > 0
```

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

### Vector, Matrix, and Tensor relationship

All three store a flat `float[]` buffer internally:
- **Vector**: `float[]` with implicit shape `[length]`
- **Matrix**: `float[]` with shape `[rows, cols]`
- **Tensor**: `float[]` with arbitrary `int[]` shape

Conversion between them is zero-copy when ranks match. Use `reshape()` to reinterpret shape without copying (element count must match).

## See Also

- [Parameterized Queries](parameters.md)
- [DDL / DML](ddl-dml.md)
- [SELECT](select.md)
