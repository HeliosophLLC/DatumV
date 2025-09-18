# Functions Reference

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Star Schema](star-schema.md) · [Language Server](language-server.md) · [Programmatic API](api.md) · [Compute Backend](compute.md)

DatumIngest provides a comprehensive function library for data transformation, ML feature engineering, and image processing. All functions can be composed in SQL expressions.

## Function Categories

Every function belongs to a single **category** that describes its operational domain. Categories are reported by the `.functions` REPL command, the `ListFunctions` gRPC RPC, and the language server autocomplete/hover.

| Category | Description |
|----------|-------------|
| **String** | Text manipulation, case conversion, search, and path utilities. |
| **Temporal** | Date, time, duration, and timestamp construction, extraction, and arithmetic. |
| **Numeric** | Arithmetic, rounding, powers, roots, logarithms, trigonometry, and constants. |
| **Activation** | ML activation functions (sigmoid, ReLU, GELU, etc.), softmax, and L2 normalization. |
| **Vector** | Vector and tensor operations: reductions, manipulation, distance, similarity, and introspection. |
| **Image** | Image metadata, loading, transforms, analysis, and perceptual hashing. |
| **Encoding** | UUID generation/inspection, cryptographic hashing (MD5/SHA/CRC), and base64/hex encoding. |
| **Categorical** | Categorical encoding: one-hot, label encoding (explicit domain), and feature hashing. |
| **Json** | JSON path access, existence testing, and array inspection. |
| **Array** | Typed array construction, inspection, search, manipulation, and string conversion. |
| **Conversion** | Explicit type conversion between data kinds. |
| **Utility** | General-purpose conditional, null-handling, and byte manipulation functions. |
| **Table** | Table-valued functions that produce multiple rows (used in FROM/JOIN clauses). |
| **Aggregate** | Aggregate functions that reduce multiple rows into a single result (COUNT, SUM, AVG, MIN, MAX, VARIANCE, STDDEV, MEDIAN, MODE, PERCENTILE_CONT, PERCENTILE_DISC, CORR, COVAR_POP, COVAR_SAMP, APPROX_MEDIAN, APPROX_PERCENTILE, STRING_AGG, ARRAY_AGG, ARG_MAX, ARG_MIN). |
| **Window** | Window functions that compute per-row results over a partition (ROW_NUMBER, RANK, DENSE_RANK, NTILE, LAG, LEAD, FIRST_VALUE, LAST_VALUE, NTH_VALUE, plus aggregates with OVER). |

> **Function costs (QU):** Each function has a Query Unit cost reflecting its computational weight. Tier 1 (QU 1) — trivial O(1) operations; Tier 2 (QU 2) — O(n) vector traversals and memory-intensive aggregates (MEDIAN, PERCENTILE, MODE, STRING_AGG — functions that buffer all rows per group or sort at finalization); Tier 3 (QU 5) — JSON document parsing; Tier 4 (QU 10 + ⌊px/100K⌋) — full-image pixel scans; Tier 5 (QU 50 + ⌊px/100K⌋) — image decode + transform + re-encode. px = width × height. QU costs are tracked per query and accumulated per session — see [Compute Backend — Resource Governance](compute.md#resource-governance) for budget enforcement and the `GetUsage` RPC.
>
> **Resolution-aware image costs:** Image analysis (Tier 4) and transform (Tier 5) functions incur a supplemental cost proportional to input resolution: `⌊width × height / 100,000⌋` additional QU per invocation. A 224×224 image adds 0 QU; a 1920×1080 image adds 20 QU; a 4K image adds 82 QU. The QU column for each affected function shows the full formula — base cost + supplemental.

## Numeric / Array

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `normalize` | `normalize(val, [min], [max])` | Normalize to 0–1 range. Byte/byte[]: default 0–255. Float32/Vector: requires min/max. | 1 |
| `clamp` | `clamp(val, min, max)` | Clamp value to [min, max]. Works on Float32, Vector, Matrix, Tensor. | 1 |
| `denormalize` | `denormalize(val, factor)` | Multiply by factor (reverse of normalize). | 1 |
| `reshape` | `reshape(tensor, dim1, dim2, ...)` | Reinterpret tensor shape without copying. Element count must match. | 1 |

## String

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `len` | `len(val)` | Length of string, collection, or array. | 1 |
| `mid` | `mid(str, start, length)` | Extract substring by position and length (1-based). | 1 |
| `substring` | `substring(str, start, [length])` | Extract substring from start position (1-based). | 1 |
| `upper` | `upper(str)` | Convert to uppercase (invariant). | 1 |
| `lower` | `lower(str)` | Convert to lowercase (invariant). | 1 |
| `trim` | `trim(str)` | Remove whitespace from both sides. | 1 |
| `ltrim` | `ltrim(str)` | Remove leading whitespace. | 1 |
| `rtrim` | `rtrim(str)` | Remove trailing whitespace. | 1 |
| `contains` | `contains(str, sub)` | Returns Boolean — whether str contains sub (ordinal). | 1 |
| `starts_with` | `starts_with(str, prefix)` | Returns Boolean — whether str starts with prefix (ordinal). | 1 |
| `ends_with` | `ends_with(str, suffix)` | Returns Boolean — whether str ends with suffix (ordinal). | 1 |
| `position` | `position(str, sub)` | 1-based index of first occurrence, or 0 if not found. | 1 |
| `replace` | `replace(str, old, new)` | Replace all occurrences of old with new (ordinal). | 1 |
| `concat` | `concat(a, b, ...)` | Concatenate two or more strings. Null args treated as empty. | 1 |
| `repeat` | `repeat(str, count)` | Repeat string count times. | 1 |
| `reverse` | `reverse(str)` | Reverse character order. | 1 |
| `left` | `left(str, n)` | First n characters. | 1 |
| `right` | `right(str, n)` | Last n characters. | 1 |
| `lpad` | `lpad(str, len, fill)` | Pad on the left to target length with fill string. | 1 |
| `rpad` | `rpad(str, len, fill)` | Pad on the right to target length with fill string. | 1 |
| `get_filename` | `get_filename(path)` | Return file name with extension from path. | 1 |
| `get_file_extension` | `get_file_extension(path)` | Return extension (with dot) from path. | 1 |
| `get_path` | `get_path(path)` | Return directory portion of path. | 1 |
| `regexp_extract` | `regexp_extract(str, pattern, [group])` | Extract first regex match. Optional 1-based group index returns a capture group. NULL if no match. | 1 |
| `regexp_replace` | `regexp_replace(str, pattern, replacement, [flags])` | Replace regex matches. Global by default; flags: `'g'` global, `'i'` case-insensitive. Without `'g'`, replaces first match only. | 1 |
| `concat_ws` | `concat_ws(sep, s1, s2, ...)` | Concatenate with separator, skipping nulls. | 1 |
| `split_part` | `split_part(str, delim, n)` | Split on delimiter, return the n-th field (1-based). Empty string if out of range. Negative n counts from end. | 1 |
| `initcap` | `initcap(str)` | Capitalize first letter of each word, lowercase the rest. | 1 |
| `translate` | `translate(str, from, to)` | Character-by-character substitution. Chars in `from` without a `to` counterpart are deleted. | 1 |
| `ascii` | `ascii(str)` | ASCII code of first character. Returns 0 for empty string. | 1 |
| `chr` | `chr(code)` | Character from ASCII/Unicode code point. | 1 |
| `btrim` | `btrim(str, [chars])` | Trim specified characters from both sides. Default: whitespace. | 1 |
| `word_count` | `word_count(str)` | Count whitespace-separated words. | 1 |

## JSON Column Functions

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `json_value` | `json_value(col, path)` | Extract scalar from JSON string at path. Returns String, Float32, or null. | 5 |
| `json_query` | `json_query(col, path)` | Extract JSON fragment (array/object). Returns JsonValue or Vector if all-numeric. | 5 |
| `json_exists` | `json_exists(col, path)` | Returns 1.0 if path exists in JSON, 0.0 otherwise. | 5 |
| `json_array_length` | `json_array_length(col, [path])` | Count elements in JSON array at root or path. | 5 |

## Array Functions

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|  
| `array` | `array(a, b, c, ...)` | Construct a typed Array from one or more values. All arguments must share the same data kind. | 1 |
| `array_length` | `array_length(arr)` | Number of elements in the array. | 1 |
| `array_join` | `array_join(arr, separator)` | Join elements into a String with separator. Null elements are skipped. String elements used directly; others converted via ToString. | 1 |
| `array_contains` | `array_contains(arr, value)` | Returns Boolean — whether the array contains the value (by equality). | 1 |
| `array_position` | `array_position(arr, value)` | 1-based index of the first matching element, or null if not found. | 1 |
| `array_sort` | `array_sort(arr)` | Sorted copy (ascending). Uses ORDER BY comparison semantics — nulls sort last. Supports Float32, UInt8, String, Date, DateTime elements. | 1 |
| `array_reverse` | `array_reverse(arr)` | Reversed copy of the array. | 1 |
| `array_distinct` | `array_distinct(arr)` | Remove duplicates, preserving first-occurrence order. Uses DataValue equality. | 1 |
| `array_slice` | `array_slice(arr, start, length)` | Sub-array extraction. 1-based start, clamped to bounds. Returns empty array if out of range. | 1 |
| `array_concat` | `array_concat(arr1, arr2)` | Concatenate two arrays. Both must share the same element kind. | 1 |
| `array_get` | `array_get(arr, index)` | Element at a 1-based index. Returns null if index is out of bounds or either argument is null. Return type matches the array's element kind. | 1 |
| `array_min` | `array_min(arr)` | Minimum element, skipping nulls. Returns null for an empty or all-null array. Return type matches the array's element kind. | 1 |
| `array_max` | `array_max(arr)` | Maximum element, skipping nulls. Returns null for an empty or all-null array. Return type matches the array's element kind. | 1 |
| `array_sum` | `array_sum(arr)` | Sum of numeric (Float32 or UInt8) elements, skipping nulls. Returns null for an empty or all-null array. Always returns Float32. | 1 |
| `array_avg` | `array_avg(arr)` | Average (mean) of numeric elements, skipping nulls. Returns null for an empty or all-null array. Always returns Float32. | 1 |
| `array_transform` | `array_transform(arr, element -> expr)` | Applies a lambda to each element, returning a new array of transformed values. | 1 |
| `array_filter` | `array_filter(arr, element -> Boolean)` | Filters an array, keeping only elements where the lambda predicate returns true. | 1 |

> **Tip:** `len(arr)` also works as an alias for `array_length(arr)` since `len()` supports Array inputs.

> **Tip:** Arrays can be constructed with bracket syntax: `[1, 2, 3]` is equivalent to `array(1, 2, 3)`.

## Type Conversion

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `cast` | `cast(val, targetKind)` | Explicit type conversion. Date→Float32 yields epoch days; DateTime→Float32 yields epoch seconds. Supports "uuid" and "bool" target types. | 1 |
| `to_epoch` | `to_epoch(val)` | Convert Date to epoch days or DateTime to epoch seconds (since 1970-01-01) as Float32. | 1 |

## UUID

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `uuid4` | `uuid4()` | Generate a random version 4 UUID. | 1 |
| `uuid7` | `uuid7()` | Generate a time-ordered version 7 UUID (monotonically increasing). | 1 |
| `is_uuid` | `is_uuid(str)` | Returns Boolean — whether the string is a valid UUID. | 1 |
| `uuid_str` | `uuid_str(uuid)` | Format UUID as lowercase hyphenated string. | 1 |
| `uuid_bytes` | `uuid_bytes(uuid)` | Extract UUID as 16-byte UInt8Array (big-endian). | 1 |
| `uuid_version` | `uuid_version(uuid)` | Extract version number as Float32 (4 for random, 7 for time-ordered). | 1 |
| `uuid_timestamp` | `uuid_timestamp(uuid)` | Extract embedded timestamp from v7 UUID as DateTime. Returns null for non-v7. | 1 |

### UUID examples

```sql
-- Generate identifiers
SELECT uuid4() AS random_id, uuid7() AS time_ordered_id FROM data

-- Parse UUID strings from source data
SELECT CAST(id_column AS Uuid) AS id FROM raw_data WHERE is_uuid(id_column)

-- Extract timestamp from v7 UUIDs for temporal analysis
SELECT uuid_timestamp(event_id) AS created_at FROM events

-- Hash composite keys using UUID bytes
SELECT sha256(bytes_concat(uuid_bytes(id1), uuid_bytes(id2))) AS composite_hash FROM joins
```

## Byte Array

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `bytes_concat` | `bytes_concat(a, b, ...)` | Concatenate two or more byte arrays. Null args treated as empty. | 1 |
| `bytes_slice` | `bytes_slice(bytes, start, len)` | Extract sub-array by position and length (0-based, clamped). | 1 |
| `bytes` | `bytes(a, b, ...)` | Construct a byte array from Float32 values (each 0–255). | 1 |

## Hashing

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `md5` | `md5(val)` | MD5 hash as UInt8Array. Accepts String (UTF-8) or UInt8Array. Use `hex_encode()` for hex digest. | 2 |
| `sha256` | `sha256(val)` | SHA-256 hash as UInt8Array. Accepts String (UTF-8) or UInt8Array. Use `hex_encode()` for hex digest. | 2 |
| `sha512` | `sha512(val)` | SHA-512 hash as UInt8Array. Accepts String (UTF-8) or UInt8Array. Use `hex_encode()` for hex digest. | 2 |
| `crc32` | `crc32(val)` | CRC-32 checksum as Float32. Accepts String (UTF-8) or UInt8Array. | 2 |

## Encoding

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `base64_encode` | `base64_encode(bytes)` | Encode byte array as Base64 string. | 1 |
| `base64_decode` | `base64_decode(str)` | Decode Base64 string to byte array. | 1 |
| `hex_encode` | `hex_encode(bytes)` | Encode byte array as lowercase hex string. | 1 |
| `hex_decode` | `hex_decode(str)` | Decode hex string to byte array. | 1 |

## Categorical Encoding (5)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|  
| `one_hot` | `one_hot(value, label1, label2, ...)` | One-hot encode against an explicit domain. Returns Vector[K] with 1.0 at matching index; zero vector for unknown values. | 1 |
| `one_hot_unk` | `one_hot_unk(value, label1, label2, ...)` | One-hot encode with unknown bucket. Returns Vector[K+1]; unknown values activate the last dimension. | 1 |
| `label_encode` | `label_encode(value, label1, label2, ...)` | Label-encode against an explicit domain. Returns the zero-based Float32 index; -1 for unknown values. | 1 |
| `label_encode_unk` | `label_encode_unk(value, label1, label2, ...)` | Label-encode with unknown bucket. Returns the zero-based Float32 index; K (domain size) for unknown values. | 1 |
| `hash_encode` | `hash_encode(value, num_buckets)` | Feature-hash a string into a fixed-size one-hot Vector. Uses XxHash32 modulo num_buckets. Handles any cardinality without an explicit domain. | 2 |

### Categorical encoding examples

```sql
-- Low-cardinality: explicit domain one-hot
SELECT one_hot(color, 'red', 'green', 'blue') AS color_vec
FROM products

-- With unknown bucket for unseen categories
SELECT one_hot_unk(species, 'cat', 'dog', 'bird') AS species_vec
FROM animals

-- Label encoding for ordinal features
SELECT label_encode(size, 'S', 'M', 'L', 'XL') AS size_idx
FROM orders

-- High-cardinality: feature hashing (no vocabulary needed)
SELECT hash_encode(zip_code, 256) AS zip_features
FROM addresses

-- Conditional selection with iif
SELECT iif(age > 18, 'adult', 'minor') AS age_group
FROM users
```

## Temporal Feature Extraction

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `date_part` | `date_part(part, val)` | Extract a named component from a Date or DateTime as Float32. | 1 |
| `cyclical_encode` | `cyclical_encode(val, period)` | Encode a Float32 as a 2-element Vector `[sin(2π·val/period), cos(2π·val/period)]`. | 1 |

### `date_part` supported parts

| Part Name | Returns | Example |
|-----------|---------|----------|
| `year` | Year number | 2026 |
| `month` | 1–12 | 3 |
| `day` | 1–31 | 16 |
| `day_of_week` | 0 (Sunday) – 6 (Saturday) | 1 (Monday) |
| `hour` | 0–23 (Date returns 0) | 14 |
| `minute` | 0–59 (Date returns 0) | 30 |
| `second` | 0–59 (Date returns 0) | 45 |
| `day_of_year` | 1–366 | 75 |
| `week_of_year` | 1–53 (ISO 8601) | 12 |
| `quarter` | 1–4 | 1 |
| `is_weekend` | 0 or 1 | 0 |

### Temporal ML encoding examples

```sql
-- Convert date to epoch days for use as a numeric feature
SELECT to_epoch(date_col) AS epoch_days FROM data

-- Equivalent via CAST
SELECT CAST(date_col AS Float32) AS epoch_days FROM data

-- Extract individual components
SELECT date_part('year', date_col) AS year,
       date_part('month', date_col) AS month,
       date_part('day_of_week', date_col) AS dow
FROM data

-- Cyclical encoding for periodic features (preserves month 12 → 1 proximity)
SELECT cyclical_encode(date_part('month', date_col), 12) AS month_encoded,
       cyclical_encode(date_part('hour', datetime_col), 24) AS hour_encoded
FROM data

-- Full temporal feature vector via concatenation
SELECT vec_concat(
    cyclical_encode(date_part('month', d), 12),
    cyclical_encode(date_part('day_of_week', d), 7),
    cyclical_encode(date_part('hour', d), 24)
) AS temporal_features
FROM data
```

## Date/Time — Extraction (9)

Shorthand functions for extracting individual components from Date, DateTime, or Time values. Each returns a Float32.

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `year` | `year(date)` | Extract year. | 1 |
| `month` | `month(date)` | Extract month (1–12). | 1 |
| `day` | `day(date)` | Extract day of month (1–31). | 1 |
| `hour` | `hour(date)` | Extract hour (0–23). Accepts Date, DateTime, or Time. Returns 0 for Date inputs. | 1 |
| `minute` | `minute(date)` | Extract minute (0–59). Accepts Date, DateTime, or Time. Returns 0 for Date inputs. | 1 |
| `second` | `second(date)` | Extract second (0–59). Accepts Date, DateTime, or Time. Returns 0 for Date inputs. | 1 |
| `quarter` | `quarter(date)` | Extract quarter (1–4). | 1 |
| `dayofweek` | `dayofweek(date)` | ISO 8601 day of week: 1 (Monday) through 7 (Sunday). | 1 |
| `dayofyear` | `dayofyear(date)` | Day of year (1–366). | 1 |

> **Note:** `dayofweek()` uses ISO 8601 convention (1=Monday, 7=Sunday). The older `date_part('day_of_week', ...)` uses .NET convention (0=Sunday, 6=Saturday). Prefer `dayofweek()` for new code.

## Date/Time — Construction & Arithmetic (12)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `now` | `now()` | Current UTC timestamp as DateTime. | 1 |
| `make_date` | `make_date(year, month, day)` | Construct a Date from components (all Float32). | 1 |
| `make_timestamp` | `make_timestamp(y, m, d, h, min, s)` | Construct a DateTime (UTC) from components (all Float32). | 1 |
| `date_diff` | `date_diff(part, start, end)` | Count of part boundaries crossed between two dates. Returns Float32. | 1 |
| `date_add` | `date_add(part, amount, date)` | Add amount of the specified part to a date. Preserves input kind. | 1 |
| `date_trunc` | `date_trunc(part, date)` | Truncate to the specified precision. Week uses ISO 8601 (Monday start). Preserves input kind. | 1 |
| `date_bucket` | `date_bucket(part, width, date [, origin])` | Bucket into fixed-width intervals. Default origin is 2000-01-01. Preserves input kind. | 1 |
| `make_time` | `make_time(hour, minute, second)` | Construct a Time from components (all Float32). | 1 |
| `current_time` | `current_time()` | Current UTC time of day as Time. | 1 |
| `date_span` | `date_span(start, end)` | Elapsed Duration between two Date or DateTime values. | 1 |
| `date_offset` | `date_offset(date, duration)` | Add a Duration to a Date, DateTime, or Time. Returns DateTime for Date/DateTime, Time for Time. | 1 |
| `time_diff` | `time_diff(start, end)` | Duration between two Time values (wraps forward through midnight). | 1 |

### Supported date parts

All date part arguments accept these names (case-insensitive) with aliases:

| Part | Aliases |
|------|---------|
| `year` | `years`, `y` |
| `quarter` | `quarters`, `q` |
| `month` | `months`, `m` |
| `week` | `weeks`, `w` |
| `day` | `days`, `d` |
| `hour` | `hours`, `h` |
| `minute` | `minutes`, `min` |
| `second` | `seconds`, `s` |
| `millisecond` | `milliseconds`, `ms` |

## Duration (5)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `make_duration` | `make_duration(days, hours, minutes, seconds)` | Construct a Duration from components (all Float32). | 1 |
| `duration_seconds` | `duration_seconds(dur)` | Total seconds in a Duration as Float32. | 1 |
| `duration_minutes` | `duration_minutes(dur)` | Total minutes in a Duration as Float32 (fractional). | 1 |
| `duration_hours` | `duration_hours(dur)` | Total hours in a Duration as Float32 (fractional). | 1 |
| `duration_days` | `duration_days(dur)` | Total days in a Duration as Float32 (fractional). | 1 |

## Date/Time — Formatting & Probing (2)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `strftime` | `strftime(date, format)` | Format a Date or DateTime as a String using .NET format strings (e.g., `"yyyy-MM-dd"`). | 1 |
| `is_date` | `is_date(expr)` | Returns 1 if the value is or can be parsed as a date, 0 otherwise. Accepts String, Date, DateTime. | 1 |

### Date/Time examples

```sql
-- Shorthand extraction
SELECT year(order_date) AS y, month(order_date) AS m, day(order_date) AS d FROM orders

-- ISO day of week (1=Monday)
SELECT dayofweek(event_date) AS dow FROM events

-- Date arithmetic
SELECT date_add('month', 3, start_date) AS extended FROM contracts
SELECT date_diff('day', hire_date, now()) AS tenure_days FROM employees

-- Truncation for grouping
SELECT date_trunc('month', sale_date) AS period, SUM(amount) FROM sales GROUP BY period

-- 15-minute bucketing
SELECT date_bucket('minute', 15, event_time) AS bucket, COUNT(*) FROM logs GROUP BY bucket

-- Construct dates from components
SELECT make_date(year_col, month_col, 1) AS first_of_month FROM data

-- Format for display
SELECT strftime(created_at, 'yyyy-MM-dd HH:mm') AS formatted FROM records

-- Data quality checks
SELECT * FROM raw_data WHERE is_date(date_column) = 0

-- Time construction and extraction
SELECT make_time(14, 30, 0) AS meeting_time FROM data
SELECT hour(time_col) AS h, minute(time_col) AS m FROM schedule
SELECT current_time() AS now_time

-- Duration arithmetic
SELECT date_span(start_date, end_date) AS elapsed FROM projects
SELECT duration_days(date_span(hire_date, now())) AS tenure FROM employees
SELECT date_offset(ship_date, make_duration(3, 0, 0, 0)) AS delivery_date FROM orders

-- Time + Duration
SELECT date_offset(shift_start, make_duration(0, 8, 0, 0)) AS shift_end FROM schedule

-- Duration arithmetic (preserves Duration type)
SELECT date_span(start_date, end_date) + make_duration(1, 0, 0, 0) AS extended FROM projects

-- Hash functions return raw bytes; compose with hex_encode for digest
SELECT hex_encode(sha256(name)) AS name_hash FROM users

-- Time difference (wraps through midnight)
SELECT time_diff(shift_start, shift_end) AS shift_length FROM shifts
```

## Table-Valued Functions

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `unnest` | `unnest(array_col)` | Expand array-valued column into separate rows. Works with Vector, UInt8Array, JsonValue arrays. | 1 |
| `range` | `range(start, end[, step])` | Generate a sequence of rows with a `Value` column from start to end (inclusive). Default step is 1. | 1 |

Table-valued functions can be used in FROM, CROSS JOIN, and LATERAL JOIN clauses. When used with `CROSS JOIN LATERAL` or `CROSS APPLY`, the function arguments can reference columns from the left-hand table, enabling per-row expansion of array or nested data:

```sql
-- Expand a vector column per row using lateral join
SELECT t.name, s.value
FROM data AS t
CROSS JOIN LATERAL UNNEST(t.scores) AS s
```

See [SQL Reference — LATERAL JOIN / APPLY](sql.md#lateral-join--apply) for full syntax and examples.

## Aggregate Functions

Aggregate functions reduce multiple rows into a single result per group. Used with `GROUP BY` or as global aggregations (see [SQL Reference — GROUP BY](sql.md#group-by--aggregation)).

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|  
| `COUNT` | `COUNT(*)` or `COUNT(expr)` | Count all rows (`*`) or non-null values (`expr`). | 1 |
| `SUM` | `SUM(expr)` | Sum of non-null `Float32` values. Returns null if all inputs are null. | 1 |
| `AVG` | `AVG(expr)` | Arithmetic mean of non-null `Float32` values. Nulls excluded from denominator. | 1 |
| `MIN` | `MIN(expr)` | Minimum value. Supports Float32, UInt8, String, Date, DateTime, Time. | 1 |
| `MAX` | `MAX(expr)` | Maximum value. Supports Float32, UInt8, String, Date, DateTime, Time. | 1 |
| `VARIANCE` | `VARIANCE(expr)` | Sample variance (N−1 denominator). Alias for `VAR_SAMP`. | 1 |
| `VAR_SAMP` | `VAR_SAMP(expr)` | Sample variance (N−1). Null for fewer than 2 values. | 1 |
| `VAR_POP` | `VAR_POP(expr)` | Population variance (N denominator). | 1 |
| `STDDEV` | `STDDEV(expr)` | Sample standard deviation (N−1). Alias for `STDDEV_SAMP`. | 1 |
| `STDDEV_SAMP` | `STDDEV_SAMP(expr)` | Sample standard deviation (N−1). Null for fewer than 2 values. | 1 |
| `STDDEV_POP` | `STDDEV_POP(expr)` | Population standard deviation (N denominator). | 1 |
| `MEDIAN` | `MEDIAN(expr)` | Median (50th percentile) of non-null `Float32` values. Averages two middle values for even counts. | 2 |
| `PERCENTILE_CONT` | `PERCENTILE_CONT(expr, fraction)` | Continuous percentile with linear interpolation. Fraction in [0, 1]. | 2 |
| `PERCENTILE_DISC` | `PERCENTILE_DISC(expr, fraction)` | Discrete percentile (nearest rank). Returns an actually observed value. Fraction in [0, 1]. | 2 |
| `MODE` | `MODE(expr)` | Most frequently occurring value. Ties broken by first occurrence. Works on any comparable type. | 2 |
| `CORR` | `CORR(y, x)` | Pearson correlation coefficient between two numeric columns. Returns value in [−1, 1]. | 1 |
| `COVAR_POP` | `COVAR_POP(y, x)` | Population covariance (N denominator) between two numeric columns. | 1 |
| `COVAR_SAMP` | `COVAR_SAMP(y, x)` | Sample covariance (N−1 denominator). Null for fewer than 2 pairs. | 1 |
| `APPROX_MEDIAN` | `APPROX_MEDIAN(expr)` | Approximate median using reservoir sampling. O(1) memory, ~1–5% error for large groups. | 2 |
| `APPROX_PERCENTILE` | `APPROX_PERCENTILE(expr, fraction)` | Approximate percentile using reservoir sampling. O(1) memory, ~1–5% error. | 2 |
| `STRING_AGG` | `STRING_AGG(expr, separator [ORDER BY ...])` | Concatenates non-null string values with a separator. Supports intra-aggregate ORDER BY. | 2 |
| `ARRAY_AGG` | `ARRAY_AGG(expr [ORDER BY ...])` | Collects non-null values into a typed `Array`. Accepts any data kind. Supports intra-aggregate ORDER BY and DISTINCT. Returns null if all inputs are null. | 1 |
| `ARG_MAX` | `ARG_MAX(value, key)` | Returns the `value` from the row where `key` is at its maximum. Null keys are skipped. Ties broken by first-encountered row. Supports intra-aggregate ORDER BY for deterministic tie-breaking. Key must be a comparable type. | 1 |
| `ARG_MIN` | `ARG_MIN(value, key)` | Returns the `value` from the row where `key` is at its minimum. Null keys are skipped. Ties broken by first-encountered row. Supports intra-aggregate ORDER BY for deterministic tie-breaking. Key must be a comparable type. | 1 |

All aggregate functions support the `DISTINCT` modifier (e.g. `COUNT(DISTINCT expr)`, `SUM(DISTINCT expr)`), which deduplicates argument values before accumulation. The DISTINCT deduplication adds no additional Query Units. `COUNT(DISTINCT *)` is not supported — use `COUNT(DISTINCT column)` instead.

## Window Functions

Window functions compute a value for each row based on a window of related rows defined by an `OVER` clause. Unlike aggregates with `GROUP BY`, window functions do not collapse rows — every input row produces an output row. See [SQL Reference — Window Functions](sql.md#window-functions) for full syntax.

### Dedicated Window Functions (9)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|  
| `ROW_NUMBER` | `ROW_NUMBER() OVER (...)` | Sequential integer 1..N per partition. | 1 |
| `RANK` | `RANK() OVER (...)` | Rank with gaps on ties (1, 1, 3). Requires ORDER BY. | 1 |
| `DENSE_RANK` | `DENSE_RANK() OVER (...)` | Rank without gaps (1, 1, 2). Requires ORDER BY. | 1 |
| `NTILE` | `NTILE(n) OVER (...)` | Distribute rows into `n` roughly equal buckets. | 1 |
| `LAG` | `LAG(expr [, offset [, default]]) OVER (...)` | Value from `offset` rows before current (default offset 1, default value NULL). | 1 |
| `LEAD` | `LEAD(expr [, offset [, default]]) OVER (...)` | Value from `offset` rows after current (default offset 1, default value NULL). | 1 |
| `FIRST_VALUE` | `FIRST_VALUE(expr) [IGNORE NULLS] OVER (...)` | Value from the first row in the window frame. `IGNORE NULLS` skips null values. | 1 |
| `LAST_VALUE` | `LAST_VALUE(expr) [IGNORE NULLS] OVER (...)` | Value from the last row in the window frame. `IGNORE NULLS` skips null values. | 1 |
| `NTH_VALUE` | `NTH_VALUE(expr, n) [FROM FIRST \| FROM LAST] [IGNORE NULLS] OVER (...)` | Value from the Nth row (1-based) in the window frame. `FROM LAST` counts from the end. | 1 |

### Aggregates as Window Functions

All single-argument aggregate functions (COUNT, SUM, AVG, MIN, MAX, VARIANCE, VAR_SAMP, VAR_POP, STDDEV, STDDEV_SAMP, STDDEV_POP, MEDIAN, MODE, PERCENTILE_CONT, PERCENTILE_DISC, APPROX_MEDIAN, APPROX_PERCENTILE) can also be used with an OVER clause to produce windowed results instead of grouped results. Two-argument aggregates (CORR, COVAR_POP, COVAR_SAMP, ARG_MAX, ARG_MIN), STRING_AGG, and ARRAY_AGG are not supported as window functions.

```sql
-- Running sum
SELECT SUM(amount) OVER (ORDER BY date ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM data

-- Partition-level average alongside each row
SELECT *, AVG(score) OVER (PARTITION BY category) AS category_avg FROM data
```

## Example SQL with functions

```sql
-- Normalize a numeric column
SELECT id, normalize(score, 0, 100) AS norm_score FROM data

-- JSON extraction
SELECT json_value(metadata, '$.category') AS cat FROM records

-- String manipulation
SELECT id, get_filename(file_path) AS name FROM files WHERE len(file_path) > 10

-- Reshape vectors
SELECT reshape(embedding, 16, 16) AS matrix_embed FROM features

-- Type casting
SELECT id, cast(score, 'UInt8') AS byte_score FROM data

-- Math functions
SELECT abs(delta), sqrt(variance), pow(base_val, 2) FROM metrics

-- ML activations on embeddings
SELECT sigmoid(score), relu(raw_output), gelu(activation) FROM model_outputs

-- Tensor introspection
SELECT rank(weights) AS ndim, shape(weights) AS dims, rdim(weights, 0) AS rows FROM features

-- Vector reductions
SELECT vec_mean(embedding), vec_norm(embedding), vec_std(features) FROM vectors

-- Distance computation
SELECT cosine_similarity(query_vec, doc_vec) AS similarity FROM search_results

-- Softmax normalization
SELECT softmax(logits) AS probabilities FROM predictions

-- Vector manipulation
SELECT vec_slice(embedding, 0, 128) AS half, vec_sort(scores) FROM data

-- Utility
SELECT coalesce(primary_score, fallback_score) AS score FROM results

-- Image preprocessing pipeline
SELECT resize(file_bytes, 224, 224) AS img, label FROM images
SELECT image_to_tensor_chw(resize(file_bytes, 224, 224)) AS pixels FROM images
SELECT image_to_tensor_hwc(resize(file_bytes, 224, 224)) AS pixels FROM images
SELECT width(file_bytes) AS w, height(file_bytes) AS h FROM images WHERE pixel_count(file_bytes) > 1000000

-- Image augmentation
SELECT noise(grayscale(file_bytes), 'gaussian', 5) AS augmented FROM training_images
```

## Math — Basic Arithmetic (8)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `abs` | `abs(x)` | Absolute value. Element-wise for vectors/matrices/tensors. | 1 |
| `sign` | `sign(x)` | Returns -1, 0, or 1. Element-wise. | 1 |
| `negate` | `negate(x)` | Negation (-x). Element-wise. | 1 |
| `mod` | `mod(a, b)` | Modulus (a % b). Element-wise with broadcast. | 1 |
| `add` | `add(a, b)` | Addition. Element-wise with scalar broadcast. | 1 |
| `subtract` | `subtract(a, b)` | Subtraction. Element-wise with scalar broadcast. | 1 |
| `multiply` | `multiply(a, b)` | Multiplication. Element-wise with scalar broadcast. | 1 |
| `divide` | `divide(a, b)` | Division. Element-wise with scalar broadcast. | 1 |

## Math — Powers, Roots & Logarithms (10)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `sqrt` | `sqrt(x)` | Square root. | 1 |
| `cbrt` | `cbrt(x)` | Cube root. | 1 |
| `square` | `square(x)` | Square (x²). | 1 |
| `exp` | `exp(x)` | Natural exponential (eˣ). | 1 |
| `exp2` | `exp2(x)` | Base-2 exponential (2ˣ). | 1 |
| `ln` | `ln(x)` | Natural logarithm. | 1 |
| `log2` | `log2(x)` | Base-2 logarithm. | 1 |
| `log10` | `log10(x)` | Base-10 logarithm. | 1 |
| `pow` | `pow(base, exp)` | Power function. Element-wise with broadcast. | 1 |
| `log` | `log(x, base)` | Logarithm with custom base. | 1 |

## Math — Trigonometric & Hyperbolic (14)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `sin` | `sin(x)` | Sine (radians). | 1 |
| `cos` | `cos(x)` | Cosine (radians). | 1 |
| `tan` | `tan(x)` | Tangent (radians). | 1 |
| `asin` | `asin(x)` | Arc sine → radians. | 1 |
| `acos` | `acos(x)` | Arc cosine → radians. | 1 |
| `atan` | `atan(x)` | Arc tangent → radians. | 1 |
| `atan2` | `atan2(y, x)` | Two-argument arc tangent. | 1 |
| `sinh` | `sinh(x)` | Hyperbolic sine. | 1 |
| `cosh` | `cosh(x)` | Hyperbolic cosine. | 1 |
| `tanh` | `tanh(x)` | Hyperbolic tangent. | 1 |
| `degrees` | `degrees(x)` | Radians → degrees. | 1 |
| `radians` | `radians(x)` | Degrees → radians. | 1 |
| `pi` | `pi()` | Returns π constant. | 1 |
| `euler` | `euler()` | Returns Euler's number e. | 1 |

## Math — Rounding & Quantization (7)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `ceil` | `ceil(x)` | Round up to nearest integer. | 1 |
| `floor` | `floor(x)` | Round down to nearest integer. | 1 |
| `truncate` | `truncate(x)` | Remove fractional part toward zero. | 1 |
| `round` | `round(x, [decimals])` | Round to nearest integer or specified decimal places. | 1 |
| `quantize` | `quantize(x, step)` | Round to nearest multiple of step. | 1 |
| `bucketize` | `bucketize(val, boundaries)` | Assign value to bucket index based on sorted boundary vector. | 1 |
| `clip` | `clip(x, min, max)` | Clip to range (alias for clamp). | 1 |

## Math — ML Activation Functions (12)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `sigmoid` | `sigmoid(x)` | Logistic sigmoid σ(x) = 1/(1+e⁻ˣ). | 1 |
| `relu` | `relu(x)` | Rectified Linear Unit max(0, x). | 1 |
| `selu` | `selu(x)` | Scaled Exponential Linear Unit. | 1 |
| `gelu` | `gelu(x)` | Gaussian Error Linear Unit (fast approximation). | 1 |
| `swish` | `swish(x)` | Swish activation x·σ(x). | 1 |
| `softplus` | `softplus(x)` | Softplus ln(1+eˣ). | 1 |
| `softsign` | `softsign(x)` | Softsign x/(1+\|x\|). | 1 |
| `mish` | `mish(x)` | Mish activation x·tanh(softplus(x)). | 1 |
| `hard_sigmoid` | `hard_sigmoid(x)` | Piecewise linear approximation of sigmoid. | 1 |
| `hard_swish` | `hard_swish(x)` | Hard Swish x·hard_sigmoid(x). | 1 |
| `leaky_relu` | `leaky_relu(x, [alpha])` | Leaky ReLU with configurable slope (default α=0.01). | 1 |
| `elu` | `elu(x, [alpha])` | Exponential Linear Unit (default α=1.0). | 1 |

## Math — Softmax & Normalization (3)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `softmax` | `softmax(vec)` | Numerically stable softmax → probability vector. | 2 |
| `log_softmax` | `log_softmax(vec)` | Log-softmax via log-sum-exp trick. | 2 |
| `l2_normalize` | `l2_normalize(vec)` | L2 normalize to unit length. | 2 |

## Math — Vector Reductions (14)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `vec_sum` | `vec_sum(x)` | Sum of all elements → Float32. | 2 |
| `vec_mean` | `vec_mean(x)` | Mean of all elements → Float32. | 2 |
| `vec_min` | `vec_min(x)` | Minimum element → Float32. | 2 |
| `vec_max` | `vec_max(x)` | Maximum element → Float32. | 2 |
| `vec_std` | `vec_std(x)` | Population standard deviation → Float32. | 2 |
| `vec_var` | `vec_var(x)` | Population variance → Float32. | 2 |
| `vec_median` | `vec_median(x)` | Median → Float32. | 2 |
| `vec_argmin` | `vec_argmin(x)` | Index of minimum element → Float32. | 2 |
| `vec_argmax` | `vec_argmax(x)` | Index of maximum element → Float32. | 2 |
| `vec_norm` | `vec_norm(x, [p])` | Lp norm (default p=2). p=∞ for max-norm. | 2 |
| `vec_count_nonzero` | `vec_count_nonzero(x)` | Count of non-zero elements → Float32. | 2 |
| `vec_any` | `vec_any(x)` | 1 if any element is non-zero, else 0. | 2 |
| `vec_all` | `vec_all(x)` | 1 if all elements are non-zero, else 0. | 2 |
| `vec_product` | `vec_product(x)` | Product of all elements → Float32. | 2 |

## Math — Tensor Introspection (3)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `rank` | `rank(x)` | Number of dimensions → Float32. Vector=1, Matrix=2, Tensor=N. | 1 |
| `rdim` | `rdim(x, axis)` | Size of a specific dimension → Float32. | 1 |
| `shape` | `shape(x)` | All dimension sizes → Vector. | 1 |

## Math — Vector Manipulation (12)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `vec` | `vec(a, b, ...)` | Construct a vector from scalars and/or vectors. Scalars contribute one element; vectors are flattened in order. | 2 |
| `tensor` | `tensor(v1, v2, ...)` | Stack two or more equal-length vectors as rows into a Matrix with shape [N, M]. | 2 |
| `vec_slice` | `vec_slice(vec, start, len)` | Extract sub-vector by position and length. | 2 |
| `vec_concat` | `vec_concat(v1, v2, ...)` | Concatenate two or more vectors. | 2 |
| `vec_reverse` | `vec_reverse(vec)` | Reverse element order. | 2 |
| `vec_sort` | `vec_sort(vec)` | Sort ascending (returns copy). | 2 |
| `vec_unique` | `vec_unique(vec)` | Unique elements preserving first-occurrence order. | 2 |
| `vec_flatten` | `vec_flatten(x)` | Flatten Matrix/Tensor to Vector. | 2 |
| `vec_pad` | `vec_pad(vec, len, fill)` | Pad vector to target length with fill value. | 2 |
| `vec_repeat` | `vec_repeat(vec, count)` | Repeat vector n times. | 2 |
| `linspace` | `linspace(start, stop, n)` | Generate n evenly spaced values from start to stop. | 2 |
| `arange` | `arange(start, stop, step)` | Generate values with fixed step (excludes stop). | 2 |

## Math — Distance & Similarity (5)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `cosine_similarity` | `cosine_similarity(a, b)` | Cosine similarity [-1, 1] between two vectors. | 2 |
| `euclidean_distance` | `euclidean_distance(a, b)` | Euclidean (L2) distance between two vectors. | 2 |
| `manhattan_distance` | `manhattan_distance(a, b)` | Manhattan (L1) distance between two vectors. | 2 |
| `dot` | `dot(a, b)` | Dot product of two vectors. | 2 |
| `hamming_distance` | `hamming_distance(a, b)` | Hamming distance between two strings. | 2 |

## Math — Utility & Conditional (11)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `coalesce` | `coalesce(a, b, ...)` | Returns first non-null argument. | 1 |
| `greatest` | `greatest(a, b, ...)` | Returns maximum of scalar or string arguments. | 1 |
| `least` | `least(a, b, ...)` | Returns minimum of scalar or string arguments. | 1 |
| `choose` | `choose(index, v1, v2, ...)` | Returns the value at 1-based index. NULL if out of range. | 1 |
| `is_nan` | `is_nan(x)` | Returns 1 if NaN, 0 otherwise. | 1 |
| `is_finite` | `is_finite(x)` | Returns 1 if finite, 0 if NaN or infinite. | 1 |
| `is_even` | `is_even(x)` | Returns 1 if x is an even integer, 0 otherwise. | 1 |
| `is_odd` | `is_odd(x)` | Returns 1 if x is an odd integer, 0 otherwise. | 1 |
| `if_null` | `if_null(x, default)` | Returns x if not null, otherwise default. | 1 |
| `iif` | `iif(cond, then, else)` | Returns then when cond is truthy (non-null, non-zero), else otherwise. For multi-branch conditionals, see [CASE expressions](sql.md#case-expressions). | 1 |
| `random` | `random()` | Random float in [0, 1). | 1 |

## Random & Sampling (15)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `hash_split` | `hash_split(key, seed)` | Deterministic float in [0, 1) from key and seed (XxHash64). Same (key, seed) pair always produces the same value. Enables reproducible train/val/test splits via `WHERE hash_split(id, 42) < 0.8`. | 1 |
| `random_int` | `random_int(min, max)` | Random integer in [min, max] (both inclusive). | 1 |
| `random_range` | `random_range(min, max)` | Random float in [min, max). | 1 |
| `random_normal` | `random_normal(mean, stddev)` | Sample from normal distribution N(mean, stddev) via Box-Muller. | 1 |
| `random_boolean` | `random_boolean(probability)` | Bernoulli trial — returns true with probability p ∈ [0, 1]. | 1 |
| `random_truncated_normal` | `random_truncated_normal(mean, stddev, min, max)` | Sample from truncated normal, rejection-sampled to [min, max]. | 1 |
| `random_log_normal` | `random_log_normal(mean, stddev)` | Sample from log-normal: exp(N(mean, stddev)). | 1 |
| `random_exponential` | `random_exponential(rate)` | Sample from exponential distribution with given rate. | 1 |
| `random_beta` | `random_beta(alpha, beta)` | Sample from Beta(α, β) distribution. | 1 |
| `random_poisson` | `random_poisson(lambda)` | Sample from Poisson(λ) distribution (integer count). | 1 |
| `random_categorical` | `random_categorical(weights)` | Draw a 0-based category index from weighted probabilities (Vector). | 2 |
| `random_vector` | `random_vector(length)` | Vector of uniform random floats in [0, 1). | 2 |
| `random_normal_vector` | `random_normal_vector(length, mean, stddev)` | Vector of Gaussian random floats N(mean, stddev). | 2 |
| `random_permutation` | `random_permutation(length)` | Random permutation of [0, length) via Fisher-Yates. | 2 |
| `random_choice` | `random_choice(array, count)` | Sample count elements from array without replacement. | 2 |

### Random & sampling examples

```sql
-- Reproducible 80/10/10 train/val/test split
SELECT *,
  CASE
    WHEN hash_split(id, 42) < 0.8 THEN 'train'
    WHEN hash_split(id, 42) < 0.9 THEN 'val'
    ELSE 'test'
  END AS split
FROM dataset

-- Add Gaussian noise to embeddings
SELECT embedding + random_normal_vector(768, 0, 0.01) AS augmented
FROM features

-- Random dropout mask
SELECT iif(random_boolean(0.1), 0, value) AS dropped
FROM activations

-- Synthetic count data
SELECT random_poisson(5) AS event_count FROM generate_series(1, 1000)

-- Random sample of 3 tags from each row
SELECT random_choice(tags, 3) AS sampled_tags FROM articles
```

## Image — Metadata (5)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `width` | `width(img)` | Image width in pixels (header-only, no full decode). | 1 |
| `height` | `height(img)` | Image height in pixels (header-only). | 1 |
| `channels` | `channels(img)` | Number of color channels (header-only). | 1 |
| `pixel_count` | `pixel_count(img)` | Total pixel count (width × height, header-only). | 1 |
| `dimensions` | `dimensions(img, format)` | Dimension vector in specified format: `'HWC'`, `'CHW'`, `'WH'`, or `'WHC'`. | 1 |

## Image — Analysis (5)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `image_brightness_mean` | `image_brightness_mean(img)` | Mean brightness (BT.601 luminance) across all pixels → Float32 0–255. | 10 + ⌊px/100K⌋ |
| `image_brightness_std` | `image_brightness_std(img)` | Standard deviation of brightness across all pixels → Float32. | 10 + ⌊px/100K⌋ |
| `image_brightness_histogram` | `image_brightness_histogram(img)` | 256-bin brightness histogram → Vector. Each element is the pixel count for that luminance bin. | 10 + ⌊px/100K⌋ |
| `detect_blur` | `detect_blur(img)` | Laplacian variance blur detector → Float32. Higher values = sharper image. | 10 + ⌊px/100K⌋ |
| `compression_artifact_score` | `compression_artifact_score(img)` | JPEG blockiness score → Float32 0–1. Measures 8×8 block boundary discontinuities. | 10 + ⌊px/100K⌋ |

## Image — Pixel Statistics (2)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `image_pixel_mean` | `image_pixel_mean(img[, channels])` | Mean pixel value. Without channels: overall mean → Float32. With channels vector (0=R,1=G,2=B,3=A): per-channel means → Vector. | 10 + ⌊px/100K⌋ |
| `image_pixel_std` | `image_pixel_std(img[, channels])` | Standard deviation of pixel values. Same signature as `image_pixel_mean`. | 10 + ⌊px/100K⌋ |

## Image — Loading & Decode (4)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `load_image` | `load_image(bytes)` | Load encoded bytes (UInt8Array from ZIP/binary column) as an Image for use with transform and analysis functions. No decode — wraps the bytes as an opaque Image value for the fused pipeline. | 1 |
| `image_to_bytes` | `image_to_bytes(img)` | Extract raw RGBA pixel bytes as UInt8Array (length H×W×4). | 50 + ⌊px/100K⌋ |
| `image_to_tensor_hwc` | `image_to_tensor_hwc(img)` | Decode to [H, W, 3] RGB float tensor (values 0–255). TensorFlow/NumPy layout. | 50 + ⌊px/100K⌋ |
| `image_to_tensor_chw` | `image_to_tensor_chw(img)` | Decode to [3, H, W] RGB float tensor (values 0–255). PyTorch layout. | 50 + ⌊px/100K⌋ |

## Image — Transforms (13)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `resize` | `resize(img, w, h[, fmt])` | Resize image to target width/height. | 50 + ⌊px/100K⌋ |
| `crop` | `crop(img, x, y, w, h[, fmt])` | Crop rectangular region. | 50 + ⌊px/100K⌋ |
| `grayscale` | `grayscale(img[, fmt])` | Convert to grayscale (BT.601 luminance). | 50 + ⌊px/100K⌋ |
| `rotate` | `rotate(img, degrees[, fmt])` | Rotate by arbitrary angle. Canvas expands for non-90° rotations. | 50 + ⌊px/100K⌋ |
| `noise` | `noise(img, type, val[, fmt])` | Add noise. Type: `'gaussian'` (val=stddev) or `'salt_pepper'` (val=ratio). | 50 + ⌊px/100K⌋ |
| `blur` | `blur(img, radius[, fmt])` | Gaussian blur with the given sigma radius. | 50 + ⌊px/100K⌋ |
| `brighten` | `brighten(img, intensity[, fmt])` | Increase brightness by adding intensity to RGB channels. | 50 + ⌊px/100K⌋ |
| `darken` | `darken(img, intensity[, fmt])` | Decrease brightness by subtracting intensity from RGB channels. | 50 + ⌊px/100K⌋ |
| `sobel` | `sobel(img[, fmt])` | Sobel edge detection → grayscale edge magnitude image. | 50 + ⌊px/100K⌋ |
| `resize_and_crop` | `resize_and_crop(img, w, h, gravity[, fmt])` | Resize to fill then crop to exact dimensions. Gravity: `'center'`, `'top'`, `'bottom'`, `'left'`, `'right'`. | 50 + ⌊px/100K⌋ |
| `affine_transform` | `affine_transform(img, angle, sx, sy, shx, shy[, fmt])` | Affine transformation with rotation (degrees), scale, and shear parameters. | 50 + ⌊px/100K⌋ |
| `elastic_deform` | `elastic_deform(img, alpha, sigma[, fmt])` | Elastic deformation (Simard et al.). Alpha = displacement intensity, sigma = smoothing. | 50 + ⌊px/100K⌋ |
| `perspective_warp` | `perspective_warp(img, intensity[, fmt])` or `perspective_warp(img, tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y[, fmt])` | Perspective distortion. Intensity mode: random warp. Explicit mode: normalized corner coordinates. | 50 + ⌊px/100K⌋ |

## Image — Hashing (1)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `perceptual_hash` | `perceptual_hash(img)` | Difference hash (dHash) → 64-element Vector of 0/1 bits. Use with `hamming_distance()` for similarity. | 10 + ⌊px/100K⌋ |

> All transform functions accept an optional trailing `fmt` argument (`'jpeg'`, `'png'`, `'webp'`) to control output encoding. Default preserves the original format.

## Fused image pipelines

When image transforms are nested — e.g. `resize(grayscale(crop(img, ...)), 224, 224)` — the engine automatically fuses the decode/encode cycle. Without fusion each function would decode the image from bytes, apply its transform, and re-encode to bytes, only for the next function to decode those bytes again. With fusion, only the first function in the chain decodes and only the final consumer encodes, eliminating N−1 redundant decode/encode cycles.

This is implemented via `ImageHandle`, a smart wrapper that carries either encoded bytes, a decoded `SKBitmap`, or both. Key properties:

- **Lazy decode** — `ImageHandle` created from encoded bytes defers decoding until a transform actually needs the bitmap.
- **Lazy encode** — `ImageHandle` created from a bitmap defers encoding until the bytes are needed (output writer, statistics, etc.).
- **Format propagation** — Each handle tracks the requested output format. Functions without a format argument inherit the input's format; functions with an explicit format argument override it. This matches the non-fused output byte-for-byte.
- **Deterministic disposal** — The expression evaluator disposes intermediate `ImageHandle` bitmaps as soon as they are consumed, releasing native SkiaSharp memory promptly rather than waiting for GC finalization.

Non-image consumers (`AsImage()` callers such as output writers, statistics accumulators, header-only metadata functions, and `CAST`) are transparent to the optimization — they receive encoded bytes on demand via lazy encoding.
