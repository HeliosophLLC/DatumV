---
title: Temporal Functions
category: temporal
---

# Temporal Functions

[< Back to Functions Reference](string.md) | [SQL Reference](../sql/select.md)

Functions for date, time, duration, and timestamp construction, extraction, formatting, and arithmetic.

## Feature Extraction

### date_part

`date_part(part, val)` -> Float64 | QU: 1

Extract a named component from a Date, Timestamp, TimestampTz, Time, Duration, or Interval. Matches Postgres' `Float64` return type so sub-second precision (`microsecond`, `epoch`) survives without truncation.

```sql
SELECT date_part('year', date_col) AS year,
       date_part('month', date_col) AS month,
       date_part('dow', date_col) AS dow
FROM data

-- Works against Interval too
SELECT date_part('epoch', INTERVAL '1 hour 30 minutes')  -- 5400.0
```

### EXTRACT

`EXTRACT(field FROM source)` -> Float64 | QU: 1

PostgreSQL-standard syntax; desugars to `date_part('field', source)` at parse time.

```sql
SELECT EXTRACT(YEAR FROM date_col) AS year,
       EXTRACT(MONTH FROM date_col) AS month,
       EXTRACT(DOW FROM date_col) AS dow
FROM data

-- Extract from Time values
SELECT EXTRACT(HOUR FROM time_col) AS h,
       EXTRACT(MINUTE FROM time_col) AS m,
       EXTRACT(SECOND FROM time_col) AS s
FROM data
```

### cyclical_encode

`cyclical_encode(val, period)` -> Vector | QU: 1

Encode a Float32 as a 2-element Vector `[sin(2*pi*val/period), cos(2*pi*val/period)]`.

```sql
-- Cyclical encoding for periodic features (preserves month 12 -> 1 proximity)
SELECT cyclical_encode(date_part('month', date_col), 12) AS month_encoded,
       cyclical_encode(date_part('hour', datetime_col), 24) AS hour_encoded
FROM data

-- Full temporal feature vector via concatenation
SELECT vec_concat(
    cyclical_encode(EXTRACT(MONTH FROM d), 12),
    cyclical_encode(EXTRACT(ISODOW FROM d), 7),
    cyclical_encode(EXTRACT(HOUR FROM d), 24)
) AS temporal_features
FROM data
```

### Supported date parts

PostgreSQL-compatible fields for `date_part` / `EXTRACT`:

| Part Name | Returns | Example | Time input? |
|-----------|---------|---------|:-----------:|
| `year` | Year number | 2026 | |
| `month` | 1-12 | 3 | |
| `day` | 1-31 | 16 | |
| `hour` | 0-23 (Date returns 0) | 14 | Yes |
| `minute` | 0-59 (Date returns 0) | 30 | Yes |
| `second` | Fractional seconds (includes ms) | 45.5 | Yes |
| `quarter` | 1-4 | 1 | |
| `dow` | Day of week: 0 (Sunday) - 6 (Saturday) | 0 | |
| `doy` | Day of year: 1-366 | 75 | |
| `week` | ISO 8601 week number: 1-53 | 12 | |
| `isodow` | ISO day of week: 1 (Monday) - 7 (Sunday) | 7 | |
| `isoyear` | ISO 8601 week-numbering year | 2026 | |
| `epoch` | Seconds since 1970-01-01 UTC (fractional) | 1577836800 | Yes |
| `century` | Century (1-based): year 2001 -> 21 | 21 | |
| `decade` | Year / 10 (truncated) | 202 | |
| `millennium` | Millennium (1-based): year 2001 -> 3 | 3 | |
| `julian` | Julian day number | 2451545 | |
| `millisecond` | Seconds x 1000 (includes whole seconds) | 45500 | Yes |
| `microsecond` | Seconds x 1000000 (includes whole seconds) | 45500000 | Yes |
| `timezone` | UTC offset in seconds | -18000 | |
| `timezone_hour` | Signed hour component of UTC offset | -5 | |
| `timezone_minute` | Minute component of UTC offset | 30 | |

Backward-compatible aliases (DatumV extensions):

| Alias | Equivalent to |
|-------|---------------|
| `day_of_week` | `dow` |
| `day_of_year` | `doy` |
| `week_of_year` | `week` |
| `is_weekend` | Returns 0 or 1 (no PostgreSQL equivalent) |

> **Note:** `second` returns fractional seconds (e.g. 45.5 for 45s 500ms), matching PostgreSQL behavior. Use `millisecond` or `microsecond` for integer-scaled sub-second precision.

> **Note:** `timezone`, `timezone_hour`, and `timezone_minute` reflect the UTC offset stored in the value. Use `AT TIME ZONE` first to convert a UTC timestamp to a specific zone before extracting these parts.

> **Note:** `dow` uses the PostgreSQL convention (0=Sunday, 6=Saturday). The standalone `dayofweek()` function uses ISO 8601 (1=Monday, 7=Sunday). Use `isodow` for ISO convention via `date_part`/`EXTRACT`.

## Date/Time Extraction

Shorthand functions for extracting individual components from Date, Timestamp, TimestampTz, or Time values. Each returns a Float32.

### year

`year(date)` -> Float32 | QU: 1

Extract year.

### month

`month(date)` -> Float32 | QU: 1

Extract month (1-12).

### day

`day(date)` -> Float32 | QU: 1

Extract day of month (1-31).

### hour

`hour(date)` -> Float32 | QU: 1

Extract hour (0-23). Accepts Date, Timestamp, TimestampTz, or Time. Returns 0 for Date inputs.

### minute

`minute(date)` -> Float32 | QU: 1

Extract minute (0-59). Accepts Date, Timestamp, TimestampTz, or Time. Returns 0 for Date inputs.

### second

`second(date)` -> Float32 | QU: 1

Extract second (0-59). Accepts Date, Timestamp, TimestampTz, or Time. Returns 0 for Date inputs.

### quarter

`quarter(date)` -> Float32 | QU: 1

Extract quarter (1-4).

### dayofweek

`dayofweek(date)` -> Float32 | QU: 1

ISO 8601 day of week: 1 (Monday) through 7 (Sunday).

> **Note:** `dayofweek()` uses ISO 8601 convention (1=Monday, 7=Sunday). `date_part('dow', ...)` / `EXTRACT(DOW FROM ...)` uses PostgreSQL convention (0=Sunday, 6=Saturday). Use `EXTRACT(ISODOW FROM ...)` for ISO convention via `date_part`/`EXTRACT`.

### dayofyear

`dayofyear(date)` -> Float32 | QU: 1

Day of year (1-366).

```sql
-- Shorthand extraction
SELECT year(order_date) AS y, month(order_date) AS m, day(order_date) AS d FROM orders

-- ISO day of week (1=Monday)
SELECT dayofweek(event_date) AS dow FROM events
```

## Date/Time Constants

PostgreSQL-compatible temporal constants that return the same value for all references within a statement batch (matching PostgreSQL's "transaction start time" semantics). These are **keywords**, not function calls -- no parentheses for the base forms.

### CURRENT_DATE

`CURRENT_DATE` -> Date | QU: 1

Current UTC date.

### CURRENT_TIME

`CURRENT_TIME` or `CURRENT_TIME(p)` -> Time | QU: 1

Current UTC time-of-day. Optional precision `p` truncates to that many fractional-second digits (0-6).

### CURRENT_TIMESTAMP

`CURRENT_TIMESTAMP` or `CURRENT_TIMESTAMP(p)` -> TimestampTz | QU: 1

Current UTC timestamp. Optional precision `p` truncates to that many fractional-second digits.

### LOCALTIME

`LOCALTIME` or `LOCALTIME(p)` -> Time | QU: 1

Same as `CURRENT_TIME` (no session timezone).

### LOCALTIMESTAMP

`LOCALTIMESTAMP` or `LOCALTIMESTAMP(p)` -> TimestampTz | QU: 1

Same as `CURRENT_TIMESTAMP`.

> **Note:** `now()` and `current_time()` are also transaction-stable -- they return the same constant value as `CURRENT_TIMESTAMP` and `CURRENT_TIME` respectively. All temporal constants within a batch are resolved to the batch start time before execution.

```sql
-- All return the same timestamp within a single batch
SELECT CURRENT_TIMESTAMP, now(), CURRENT_TIMESTAMP(3)

-- Use in WHERE clauses
SELECT * FROM events WHERE event_date = CURRENT_DATE

-- Truncate to whole seconds
SELECT CURRENT_TIMESTAMP(0) AS ts_no_fractional
```

## Construction & Arithmetic

### now

`now()` -> TimestampTz | QU: 1

Current UTC timestamp as TimestampTz. Transaction-stable (= `CURRENT_TIMESTAMP`).

### transaction_timestamp

`transaction_timestamp()` -> TimestampTz | QU: 1

Same as `now()`. Named to clearly reflect what it returns.

### statement_timestamp

`statement_timestamp()` -> TimestampTz | QU: 1

Start time of the current statement. Same as `transaction_timestamp()` for the first statement in a batch; may differ for subsequent statements.

### clock_timestamp

`clock_timestamp()` -> TimestampTz | QU: 1

Actual wall-clock time. Changes even within a single statement -- NOT batch-stable.

### timeofday

`timeofday()` -> String | QU: 1

Actual wall-clock time as a formatted String (ISO 8601). NOT batch-stable.

### make_date

`make_date(year, month, day)` -> Date | QU: 1

Construct a Date from integer components (year, month, day).

```sql
SELECT make_date(year_col, month_col, 1) AS first_of_month FROM data
```

### make_timestamp

`make_timestamp(y, m, d, h, min, s)` -> TimestampTz | QU: 1

Construct a TimestampTz (UTC) from components. Year, month, day, hour, minute are integers; second is numeric (supports fractional seconds).

### date_diff

`date_diff(part, start, end)` -> Float32 | QU: 1

Count of part boundaries crossed between two dates. Returns Float32.

```sql
SELECT date_diff('day', hire_date, now()) AS tenure_days FROM employees
```

### date_add

`date_add(part, amount, date)` -> Date/Timestamp/TimestampTz | QU: 1

Add amount of the specified part to a date. Preserves input kind.

```sql
SELECT date_add('month', 3, start_date) AS extended FROM contracts
```

### date_trunc

`date_trunc(part, source)` -> Timestamp/TimestampTz | QU: 1

Truncate a temporal value to the start of the named field. `Timestamp` and
`TimestampTz` preserve their kind; `Date` inputs promote to `Timestamp`.
Week uses ISO 8601 (Monday start).

Supported parts: `microsecond`, `millisecond`, `second`, `minute`, `hour`,
`day`, `week`, `month`, `quarter`, `year`, `decade`, `century`, `millennium`.

```sql
-- Truncation for grouping
SELECT date_trunc('month', sale_date) AS period, SUM(amount) FROM sales GROUP BY period

-- Snap to the Monday of the ISO week
SELECT date_trunc('week', event_ts) AS week_start FROM events
```

### date_bucket

`date_bucket(part, width, date [, origin])` -> Date/Timestamp/TimestampTz | QU: 1

Bucket into fixed-width intervals. Default origin is 2000-01-01. Preserves input kind.

```sql
-- 15-minute bucketing (DatumV style)
SELECT date_bucket('minute', 15, event_time) AS bucket, COUNT(*) FROM logs GROUP BY bucket
```

### date_bin

`date_bin(stride Interval, source, origin)` -> Timestamp/TimestampTz | QU: 1

PostgreSQL-15-compatible binning: rounds `source` down to the nearest
`stride` boundary aligned to `origin`. Preserves the source's timestamp
kind (both `source` and `origin` must be the same kind).

The stride must be a positive `Interval` with no month component — months
are variable-length and aren't a fixed bin width. Use `date_trunc('month',
...)` for month buckets.

```sql
-- 15-minute bucketing
SELECT date_bin(INTERVAL '15 minutes', event_time, TIMESTAMP '2000-01-01') AS bucket,
       COUNT(*)
FROM logs GROUP BY bucket

-- 5-second polling buckets pinned to a known origin
SELECT date_bin(INTERVAL '5 seconds', poll_at, TIMESTAMP '2026-01-01') AS bucket
FROM heartbeats
```

### make_time

`make_time(hour, minute, second)` -> Time | QU: 1

Construct a Time from components (all Float32).

```sql
SELECT make_time(14, 30, 0) AS meeting_time FROM data
```

### current_time

`current_time()` -> Time | QU: 1

Current UTC time of day as Time. Transaction-stable.

```sql
SELECT current_time() AS now_time
```

### date_span

`date_span(start, end)` -> Duration | QU: 1

Elapsed Duration between two Date, Timestamp, or TimestampTz values.

```sql
SELECT date_span(start_date, end_date) AS elapsed FROM projects
SELECT duration_days(date_span(hire_date, now())) AS tenure FROM employees
```

### date_offset

`date_offset(date, duration)` -> TimestampTz/Time | QU: 1

Add a Duration to a Date, Timestamp, TimestampTz, or Time. Returns TimestampTz for Date/TimestampTz, Timestamp for Timestamp, Time for Time.

```sql
SELECT date_offset(ship_date, make_duration(3, 0, 0, 0)) AS delivery_date FROM orders

-- Time + Duration
SELECT date_offset(shift_start, make_duration(0, 8, 0, 0)) AS shift_end FROM schedule
```

### time_diff

`time_diff(start, end)` -> Duration | QU: 1

Duration between two Time values (wraps forward through midnight).

```sql
SELECT time_diff(shift_start, shift_end) AS shift_length FROM shifts
```

### Supported date parts

All date arithmetic and truncation functions (`date_add`, `date_diff`, `date_trunc`, `date_bucket`) accept these names (case-insensitive) with aliases:

| Part | Aliases | `date_trunc` |
|------|---------|:------------:|
| `year` | `years`, `y` | Yes |
| `quarter` | `quarters`, `q` | Yes |
| `month` | `months`, `m` | Yes |
| `week` | `weeks`, `w` | Yes (ISO Monday) |
| `day` | `days`, `d` | Yes |
| `hour` | `hours`, `h` | Yes |
| `minute` | `minutes`, `min` | Yes |
| `second` | `seconds`, `s` | Yes |
| `millisecond` | `milliseconds`, `ms` | Yes |
| `microsecond` | `microseconds`, `us` | Yes |
| `decade` | `decades` | Yes (e.g. 2026 -> 2020-01-01) |
| `century` | `centuries` | Yes (1-based: 2026 -> 2001-01-01) |
| `millennium` | `millennia` | Yes (1-based: 2026 -> 2001-01-01) |

## Interval

Postgres-compatible calendar-aware spans. The `Interval` kind carries
months / days / microseconds independently (see
[Type System](../sql/type-system.md#interval-literals) for the literal
syntax). All functions in this section take and return `Interval`
unless otherwise noted.

### make_interval

`make_interval([years [, months [, weeks [, days [, hours [, mins [, secs]]]]]]])` -> Interval | QU: 1

Construct an `Interval` from up to seven numeric components. All
parameters are optional — omitted trailing arguments default to 0, so
`make_interval()` returns the zero interval and `make_interval(0, 0, 0, 0, 1, 30)`
yields `01:30:00`. `secs` accepts a fractional value down to microsecond precision.

```sql
SELECT make_interval(1, 2, 0, 3, 4, 5, 6)            -- 1 year 2 mons 3 days 04:05:06
SELECT make_interval(0, 0, 0, 0, 0, 90)              -- 01:30:00
SELECT make_interval()                                -- 00:00:00
```

### interval_qualified

`interval_qualified(literal, qualifier)` -> Interval | QU: 1

Parse an interval literal with an explicit PG-style qualifier. The
parser desugars `INTERVAL '...' YEAR TO MONTH` and similar forms to this
function; direct calls are useful for programmatic construction from
runtime strings.

Recognised qualifier names (case-insensitive): `YEAR`, `MONTH`, `DAY`,
`HOUR`, `MINUTE`, `SECOND`, `YEAR TO MONTH`, `DAY TO HOUR`,
`DAY TO MINUTE`, `DAY TO SECOND`, `HOUR TO MINUTE`, `HOUR TO SECOND`,
`MINUTE TO SECOND`.

```sql
-- Disambiguate a bare-number literal
SELECT interval_qualified('1', 'HOUR')                -- 01:00:00
SELECT interval_qualified('90', 'MINUTE')             -- 01:30:00

-- Truncate finer-grained fields from a verbose literal
SELECT interval_qualified('1 year 2 months 3 days', 'YEAR')  -- 1 year
```

### justify_hours / justify_days / justify_interval

`justify_hours(interval)` -> Interval | QU: 1
`justify_days(interval)` -> Interval | QU: 1
`justify_interval(interval)` -> Interval | QU: 1

Normalisation helpers mirroring PG:

- `justify_hours` pushes microseconds spanning multiples of 24 hours into the day component.
- `justify_days` pushes days spanning multiples of 30 into the month component.
- `justify_interval` applies both, then aligns each component's sign with the interval's net direction.

```sql
SELECT justify_hours(INTERVAL '36 hours')             -- 1 day 12:00:00
SELECT justify_days(INTERVAL '35 days')               -- 1 mon 5 days
SELECT justify_interval(INTERVAL '35 days 25 hours')  -- 1 mon 6 days 01:00:00
```

### age

`age(later, earlier)` -> Interval | QU: 1

Calendar-aware difference between two timestamps. Unlike `later - earlier`
(which returns `Duration` with raw elapsed time), `age` walks the calendar
and returns years, months, days, and sub-day components separately.

The result, when applied back to `earlier`, reproduces `later` exactly.

```sql
-- "How old is this record?"
SELECT age(now(), created_at) AS age_interval FROM records

-- Compare against the raw-elapsed form
SELECT age(later, earlier) AS calendar_age,
       later - earlier      AS elapsed_duration
FROM events
```

Accepts `(Timestamp, Timestamp)` or `(TimestampTz, TimestampTz)`. Cross-kind pairs require an explicit `CAST`.

## Time-Series Generation

### generate_series

`generate_series(start, stop[, step])` -> table(value Int32|Int64|Float64) | QU: TVF
`generate_series(start, stop, stride Interval)` -> table(value Timestamp|TimestampTz) | QU: TVF

Emits one row per step (numeric) or stride (temporal) boundary from
`start` through `stop`, **inclusive** of both ends. The headline
gap-filler: pair with a `LEFT JOIN` against a sparse fact table to
surface empty time buckets. For an upper-bound-exclusive sequence, see
[`range`](table-valued.md#range).

```sql
-- Hourly buckets, even ones with no events
SELECT g.value AS bucket,
       COALESCE(SUM(e.amount), 0) AS total
FROM generate_series(
       TIMESTAMP '2026-06-11 00:00:00',
       TIMESTAMP '2026-06-11 23:00:00',
       INTERVAL '1 hour') AS g
LEFT JOIN events e
  ON e.ts >= g.value AND e.ts < g.value + INTERVAL '1 hour'
GROUP BY g.value
ORDER BY g.value

-- Numeric form (PG-compatible integer sequence)
SELECT value FROM generate_series(1, 10)        -- 1, 2, …, 10
SELECT value FROM generate_series(0, 1, 0.25)   -- 0.0, 0.25, …, 1.0
```

Numeric form accepts `Int32`, `Int64`, or `Float64` arguments; mixed
kinds widen to the widest input. The default step is `1` and must be
non-zero.

Temporal form: `start` and `stop` must be the same temporal kind. The
stride may be negative to walk backwards. Calendar strides
(`INTERVAL '1 month'`) walk calendar months with PG end-of-month
clamp-then-stay semantics. The stride must be non-zero; an interval that
fails to advance the running value short-circuits to avoid infinite
loops.

A step or stride that points away from `stop` (e.g. positive step with
`start > stop`) yields zero rows — not an error. Any `NULL` argument
also yields zero rows.

## Duration

### make_duration

`make_duration(days, hours, minutes, seconds)` -> Duration | QU: 1

Construct a Duration from components (all Float32).

```sql
-- Duration arithmetic (preserves Duration type)
SELECT date_span(start_date, end_date) + make_duration(1, 0, 0, 0) AS extended FROM projects
```

### duration_seconds

`duration_seconds(dur)` -> Float32 | QU: 1

Total seconds in a Duration as Float32.

### duration_minutes

`duration_minutes(dur)` -> Float32 | QU: 1

Total minutes in a Duration as Float32 (fractional).

### duration_hours

`duration_hours(dur)` -> Float32 | QU: 1

Total hours in a Duration as Float32 (fractional).

### duration_days

`duration_days(dur)` -> Float32 | QU: 1

Total days in a Duration as Float32 (fractional).

```sql
SELECT duration_days(date_span(hire_date, now())) AS tenure FROM employees
```

## Formatting & Probing

### strftime

`strftime(date, format)` -> String | QU: 1

Format a Date, Timestamp, or TimestampTz as a String using .NET format strings (e.g., `"yyyy-MM-dd"`).

```sql
SELECT strftime(created_at, 'yyyy-MM-dd HH:mm') AS formatted FROM records
```

### is_date

`is_date(expr)` -> Float32 | QU: 1

Returns 1 if the value is or can be parsed as a date, 0 otherwise. Accepts String, Date, Timestamp, TimestampTz.

```sql
-- Data quality checks
SELECT * FROM raw_data WHERE is_date(date_column) = 0
```

## See Also

- [UUID Functions](uuid.md) -- UUID generation, formatting, and timestamp extraction
- [Cryptographic Hash Functions](crypto.md) -- md5, sha1, sha2 family, and digest dispatcher
- [Aggregate Functions](aggregate.md) -- grouping and reduction functions
- [SQL Reference](../sql/select.md) -- full SQL dialect documentation
