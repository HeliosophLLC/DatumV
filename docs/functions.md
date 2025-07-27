# Functions Reference

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Language Server](language-server.md) · [Programmatic API](api.md) · [Compute Backend](compute.md)

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
| **Conversion** | Explicit type conversion between data kinds. |
| **Utility** | General-purpose conditional, null-handling, and byte manipulation functions. |
| **Table** | Table-valued functions that produce multiple rows (used in FROM/JOIN clauses). |
| **Aggregate** | Aggregate functions that reduce multiple rows into a single result (COUNT, SUM, AVG, MIN, MAX). |

> **Function costs (QU):** Each function has a Query Unit cost reflecting its computational weight. Tier 1 (QU 1) — trivial O(1) operations; Tier 2 (QU 2) — O(n) vector traversals; Tier 3 (QU 5) — JSON document parsing; Tier 4 (QU 10) — full-image pixel scans; Tier 5 (QU 50) — image decode + transform + re-encode. QU costs are tracked per query and accumulated per session — see [Compute Backend — Resource Governance](compute.md#resource-governance) for budget enforcement and the `GetUsage` RPC.
>
> **Resolution-aware image costs:** Image analysis (Tier 4) and transform (Tier 5) functions incur a supplemental cost that scales with the input image resolution: `floor(width × height / 100,000)` additional QU per invocation. A 224×224 image adds 0 QU; a 1920×1080 image adds 20 QU; a 4K image adds 82 QU. The QU column in the tables below shows base cost only — actual cost equals base + supplemental.

## Numeric / Array

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `normalize` | `normalize(val, [min], [max])` | Normalize to 0–1 range. Byte/byte[]: default 0–255. Scalar/Vector: requires min/max. | 1 |
| `clamp` | `clamp(val, min, max)` | Clamp value to [min, max]. Works on Scalar, Vector, Matrix, Tensor. | 1 |
| `denormalize` | `denormalize(val, factor)` | Multiply by factor (reverse of normalize). | 1 |
| `reshape` | `reshape(tensor, dim1, dim2, ...)` | Reinterpret tensor shape without copying. Element count must match. | 1 |

## String

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `len` | `len(val)` | Length of string or collection. | 1 |
| `mid` | `mid(str, start, length)` | Extract substring by position and length (0-based). | 1 |
| `substring` | `substring(str, start, [length])` | Extract substring from start position (0-based). | 1 |
| `upper` | `upper(str)` | Convert to uppercase (invariant). | 1 |
| `lower` | `lower(str)` | Convert to lowercase (invariant). | 1 |
| `trim` | `trim(str)` | Remove whitespace from both sides. | 1 |
| `ltrim` | `ltrim(str)` | Remove leading whitespace. | 1 |
| `rtrim` | `rtrim(str)` | Remove trailing whitespace. | 1 |
| `contains` | `contains(str, sub)` | Returns Boolean — whether str contains sub (ordinal). | 1 |
| `starts_with` | `starts_with(str, prefix)` | Returns Boolean — whether str starts with prefix (ordinal). | 1 |
| `ends_with` | `ends_with(str, suffix)` | Returns Boolean — whether str ends with suffix (ordinal). | 1 |
| `position` | `position(str, sub)` | 0-based index of first occurrence, or -1. | 1 |
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

## JSON Column Functions

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `json_value` | `json_value(col, path)` | Extract scalar from JSON string at path. Returns String, Scalar, or null. | 5 |
| `json_query` | `json_query(col, path)` | Extract JSON fragment (array/object). Returns JsonValue or Vector if all-numeric. | 5 |
| `json_exists` | `json_exists(col, path)` | Returns 1.0 if path exists in JSON, 0.0 otherwise. | 5 |
| `json_array_length` | `json_array_length(col, [path])` | Count elements in JSON array at root or path. | 5 |

## Type Conversion

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `cast` | `cast(val, targetKind)` | Explicit type conversion. Date→Scalar yields epoch days; DateTime→Scalar yields epoch seconds. Supports "uuid" and "bool" target types. | 1 |
| `to_epoch` | `to_epoch(val)` | Convert Date to epoch days or DateTime to epoch seconds (since 1970-01-01) as Scalar. | 1 |

## UUID

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `uuid4` | `uuid4()` | Generate a random version 4 UUID. | 1 |
| `uuid7` | `uuid7()` | Generate a time-ordered version 7 UUID (monotonically increasing). | 1 |
| `is_uuid` | `is_uuid(str)` | Returns Boolean — whether the string is a valid UUID. | 1 |
| `uuid_str` | `uuid_str(uuid)` | Format UUID as lowercase hyphenated string. | 1 |
| `uuid_bytes` | `uuid_bytes(uuid)` | Extract UUID as 16-byte UInt8Array (big-endian). | 1 |
| `uuid_version` | `uuid_version(uuid)` | Extract version number as Scalar (4 for random, 7 for time-ordered). | 1 |
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
| `bytes` | `bytes(a, b, ...)` | Construct a byte array from Scalar values (each 0–255). | 1 |

## Hashing

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `md5` | `md5(val)` | MD5 hash as UInt8Array. Accepts String (UTF-8) or UInt8Array. Use `hex_encode()` for hex digest. | 2 |
| `sha256` | `sha256(val)` | SHA-256 hash as UInt8Array. Accepts String (UTF-8) or UInt8Array. Use `hex_encode()` for hex digest. | 2 |
| `sha512` | `sha512(val)` | SHA-512 hash as UInt8Array. Accepts String (UTF-8) or UInt8Array. Use `hex_encode()` for hex digest. | 2 |
| `crc32` | `crc32(val)` | CRC-32 checksum as Scalar. Accepts String (UTF-8) or UInt8Array. | 2 |

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
| `label_encode` | `label_encode(value, label1, label2, ...)` | Label-encode against an explicit domain. Returns the zero-based Scalar index; -1 for unknown values. | 1 |
| `label_encode_unk` | `label_encode_unk(value, label1, label2, ...)` | Label-encode with unknown bucket. Returns the zero-based Scalar index; K (domain size) for unknown values. | 1 |
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
| `date_part` | `date_part(part, val)` | Extract a named component from a Date or DateTime as Scalar. | 1 |
| `cyclical_encode` | `cyclical_encode(val, period)` | Encode a Scalar as a 2-element Vector `[sin(2π·val/period), cos(2π·val/period)]`. | 1 |

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
SELECT CAST(date_col AS Scalar) AS epoch_days FROM data

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

Shorthand functions for extracting individual components from Date, DateTime, or Time values. Each returns a Scalar.

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
| `make_date` | `make_date(year, month, day)` | Construct a Date from components (all Scalar). | 1 |
| `make_timestamp` | `make_timestamp(y, m, d, h, min, s)` | Construct a DateTime (UTC) from components (all Scalar). | 1 |
| `date_diff` | `date_diff(part, start, end)` | Count of part boundaries crossed between two dates. Returns Scalar. | 1 |
| `date_add` | `date_add(part, amount, date)` | Add amount of the specified part to a date. Preserves input kind. | 1 |
| `date_trunc` | `date_trunc(part, date)` | Truncate to the specified precision. Week uses ISO 8601 (Monday start). Preserves input kind. | 1 |
| `date_bucket` | `date_bucket(part, width, date [, origin])` | Bucket into fixed-width intervals. Default origin is 2000-01-01. Preserves input kind. | 1 |
| `make_time` | `make_time(hour, minute, second)` | Construct a Time from components (all Scalar). | 1 |
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
| `make_duration` | `make_duration(days, hours, minutes, seconds)` | Construct a Duration from components (all Scalar). | 1 |
| `duration_seconds` | `duration_seconds(dur)` | Total seconds in a Duration as Scalar. | 1 |
| `duration_minutes` | `duration_minutes(dur)` | Total minutes in a Duration as Scalar (fractional). | 1 |
| `duration_hours` | `duration_hours(dur)` | Total hours in a Duration as Scalar (fractional). | 1 |
| `duration_days` | `duration_days(dur)` | Total days in a Duration as Scalar (fractional). | 1 |

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

## Aggregate Functions

Aggregate functions reduce multiple rows into a single result per group. Used with `GROUP BY` or as global aggregations (see [SQL Reference — GROUP BY](sql.md#group-by--aggregation)).

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|  
| `COUNT` | `COUNT(*)` or `COUNT(expr)` | Count all rows (`*`) or non-null values (`expr`). | 1 |
| `SUM` | `SUM(expr)` | Sum of non-null `Scalar` values. Returns null if all inputs are null. | 1 |
| `AVG` | `AVG(expr)` | Arithmetic mean of non-null `Scalar` values. Nulls excluded from denominator. | 1 |
| `MIN` | `MIN(expr)` | Minimum value. Supports Scalar, UInt8, String, Date, DateTime, Time. | 1 |
| `MAX` | `MAX(expr)` | Maximum value. Supports Scalar, UInt8, String, Date, DateTime, Time. | 1 |

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
| `vec_sum` | `vec_sum(x)` | Sum of all elements → Scalar. | 2 |
| `vec_mean` | `vec_mean(x)` | Mean of all elements → Scalar. | 2 |
| `vec_min` | `vec_min(x)` | Minimum element → Scalar. | 2 |
| `vec_max` | `vec_max(x)` | Maximum element → Scalar. | 2 |
| `vec_std` | `vec_std(x)` | Population standard deviation → Scalar. | 2 |
| `vec_var` | `vec_var(x)` | Population variance → Scalar. | 2 |
| `vec_median` | `vec_median(x)` | Median → Scalar. | 2 |
| `vec_argmin` | `vec_argmin(x)` | Index of minimum element → Scalar. | 2 |
| `vec_argmax` | `vec_argmax(x)` | Index of maximum element → Scalar. | 2 |
| `vec_norm` | `vec_norm(x, [p])` | Lp norm (default p=2). p=∞ for max-norm. | 2 |
| `vec_count_nonzero` | `vec_count_nonzero(x)` | Count of non-zero elements → Scalar. | 2 |
| `vec_any` | `vec_any(x)` | 1 if any element is non-zero, else 0. | 2 |
| `vec_all` | `vec_all(x)` | 1 if all elements are non-zero, else 0. | 2 |
| `vec_product` | `vec_product(x)` | Product of all elements → Scalar. | 2 |

## Math — Tensor Introspection (3)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `rank` | `rank(x)` | Number of dimensions → Scalar. Vector=1, Matrix=2, Tensor=N. | 1 |
| `rdim` | `rdim(x, axis)` | Size of a specific dimension → Scalar. | 1 |
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

## Math — Utility & Conditional (8)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|| `coalesce` | `coalesce(a, b, ...)` | Returns first non-null argument. | 1 |
| `greatest` | `greatest(a, b, ...)` | Returns maximum of scalar arguments. | 1 |
| `least` | `least(a, b, ...)` | Returns minimum of scalar arguments. | 1 |
| `is_nan` | `is_nan(x)` | Returns 1 if NaN, 0 otherwise. | 1 |
| `is_finite` | `is_finite(x)` | Returns 1 if finite, 0 if NaN or infinite. | 1 |
| `if_null` | `if_null(x, default)` | Returns x if not null, otherwise default. | 1 |
| `iif` | `iif(cond, then, else)` | Returns then when cond is truthy (non-null, non-zero), else otherwise. | 1 |
| `random` | `random()` | Random float in [0, 1). | 1 |

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
| `image_brightness_mean` | `image_brightness_mean(img)` | Mean brightness (BT.601 luminance) across all pixels → Scalar 0–255. | 10 |
| `image_brightness_std` | `image_brightness_std(img)` | Standard deviation of brightness across all pixels → Scalar. | 10 |
| `image_brightness_histogram` | `image_brightness_histogram(img)` | 256-bin brightness histogram → Vector. Each element is the pixel count for that luminance bin. | 10 |
| `detect_blur` | `detect_blur(img)` | Laplacian variance blur detector → Scalar. Higher values = sharper image. | 10 |
| `compression_artifact_score` | `compression_artifact_score(img)` | JPEG blockiness score → Scalar 0–1. Measures 8×8 block boundary discontinuities. | 10 |

## Image — Pixel Statistics (2)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `image_pixel_mean` | `image_pixel_mean(img[, channels])` | Mean pixel value. Without channels: overall mean → Scalar. With channels vector (0=R,1=G,2=B,3=A): per-channel means → Vector. | 10 |
| `image_pixel_std` | `image_pixel_std(img[, channels])` | Standard deviation of pixel values. Same signature as `image_pixel_mean`. | 10 |

## Image — Loading & Decode (4)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `load_image` | `load_image(bytes)` | Load encoded bytes (UInt8Array from ZIP/binary column) as an Image for use with transform and analysis functions. No decode — wraps the bytes as an opaque Image value for the fused pipeline. | 1 |
| `image_to_bytes` | `image_to_bytes(img)` | Extract raw RGBA pixel bytes as UInt8Array (length H×W×4). | 50 |
| `image_to_tensor_hwc` | `image_to_tensor_hwc(img)` | Decode to [H, W, 3] RGB float tensor (values 0–255). TensorFlow/NumPy layout. | 50 |
| `image_to_tensor_chw` | `image_to_tensor_chw(img)` | Decode to [3, H, W] RGB float tensor (values 0–255). PyTorch layout. | 50 |

## Image — Transforms (13)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `resize` | `resize(img, w, h[, fmt])` | Resize image to target width/height. | 50 |
| `crop` | `crop(img, x, y, w, h[, fmt])` | Crop rectangular region. | 50 |
| `grayscale` | `grayscale(img[, fmt])` | Convert to grayscale (BT.601 luminance). | 50 |
| `rotate` | `rotate(img, degrees[, fmt])` | Rotate by arbitrary angle. Canvas expands for non-90° rotations. | 50 |
| `noise` | `noise(img, type, val[, fmt])` | Add noise. Type: `'gaussian'` (val=stddev) or `'salt_pepper'` (val=ratio). | 50 |
| `blur` | `blur(img, radius[, fmt])` | Gaussian blur with the given sigma radius. | 50 |
| `brighten` | `brighten(img, intensity[, fmt])` | Increase brightness by adding intensity to RGB channels. | 50 |
| `darken` | `darken(img, intensity[, fmt])` | Decrease brightness by subtracting intensity from RGB channels. | 50 |
| `sobel` | `sobel(img[, fmt])` | Sobel edge detection → grayscale edge magnitude image. | 50 |
| `resize_and_crop` | `resize_and_crop(img, w, h, gravity[, fmt])` | Resize to fill then crop to exact dimensions. Gravity: `'center'`, `'top'`, `'bottom'`, `'left'`, `'right'`. | 50 |
| `affine_transform` | `affine_transform(img, angle, sx, sy, shx, shy[, fmt])` | Affine transformation with rotation (degrees), scale, and shear parameters. | 50 |
| `elastic_deform` | `elastic_deform(img, alpha, sigma[, fmt])` | Elastic deformation (Simard et al.). Alpha = displacement intensity, sigma = smoothing. | 50 |
| `perspective_warp` | `perspective_warp(img, intensity[, fmt])` or `perspective_warp(img, tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y[, fmt])` | Perspective distortion. Intensity mode: random warp. Explicit mode: normalized corner coordinates. | 50 |

## Image — Hashing (1)

| Function | Signature | Description | QU |
|----------|-----------|-------------|----|
| `perceptual_hash` | `perceptual_hash(img)` | Difference hash (dHash) → 64-element Vector of 0/1 bits. Use with `hamming_distance()` for similarity. | 10 |

> All transform functions accept an optional trailing `fmt` argument (`'jpeg'`, `'png'`, `'webp'`) to control output encoding. Default preserves the original format.

## Fused image pipelines

When image transforms are nested — e.g. `resize(grayscale(crop(img, ...)), 224, 224)` — the engine automatically fuses the decode/encode cycle. Without fusion each function would decode the image from bytes, apply its transform, and re-encode to bytes, only for the next function to decode those bytes again. With fusion, only the first function in the chain decodes and only the final consumer encodes, eliminating N−1 redundant decode/encode cycles.

This is implemented via `ImageHandle`, a smart wrapper that carries either encoded bytes, a decoded `SKBitmap`, or both. Key properties:

- **Lazy decode** — `ImageHandle` created from encoded bytes defers decoding until a transform actually needs the bitmap.
- **Lazy encode** — `ImageHandle` created from a bitmap defers encoding until the bytes are needed (output writer, statistics, etc.).
- **Format propagation** — Each handle tracks the requested output format. Functions without a format argument inherit the input's format; functions with an explicit format argument override it. This matches the non-fused output byte-for-byte.
- **Deterministic disposal** — The expression evaluator disposes intermediate `ImageHandle` bitmaps as soon as they are consumed, releasing native SkiaSharp memory promptly rather than waiting for GC finalization.

Non-image consumers (`AsImage()` callers such as output writers, statistics accumulators, header-only metadata functions, and `CAST`) are transparent to the optimization — they receive encoded bytes on demand via lazy encoding.
