---
title: Utility & Type Conversion Functions
category: utility
---

# Utility & Type Conversion Functions

Type conversion, type introspection, conditional and null-handling, and floating-point predicate functions.

## Type Conversion

### cast

`cast(val, TargetType)` → TargetType

Explicit type conversion. Accepts a type literal (`cast(x, Int32)`) or the `CAST(x AS Int32)` SQL syntax. Date to Float32 yields epoch days; DateTime to Float32 yields epoch seconds.

```sql
SELECT cast(score, UInt8) AS byte_score FROM data
SELECT CAST(score AS UInt8) AS byte_score FROM data  -- equivalent
```

### typeof

`typeof(val)` → Type

Returns the runtime DataKind of a value as a Type tag. Use with type literals for type-oriented comparisons: `typeof(x) = Int32`. The PostgreSQL alias `pg_typeof(val)` resolves to the same function.

```sql
-- Project the runtime type of each value
SELECT name, typeof(value) AS value_type FROM data
-- Output: "Int32", "Float64", "String", etc.

-- Filter rows by type
SELECT * FROM mixed_data WHERE typeof(value) = Int32

-- IS shorthand (equivalent to the above)
SELECT * FROM mixed_data WHERE value IS Int32
SELECT * FROM mixed_data WHERE value IS NOT String

-- Filter to numeric types with IN
SELECT * FROM t WHERE typeof(col) IN (Int32, Int64, Float32, Float64)

-- Type-driven branching with CASE
SELECT CASE typeof(x)
    WHEN Int32 THEN 'integer'
    WHEN Float64 THEN 'float'
    WHEN String THEN 'text'
    ELSE 'other'
END AS type_label
FROM t

-- Conditional formatting based on runtime type
SELECT CASE typeof(value)
    WHEN Float64 THEN round(value, 2)
    WHEN DateTime THEN strftime(value, '%Y-%m-%d')
    ELSE CAST(value AS String)
END AS formatted
FROM mixed_data

-- Data quality: find rows where a column has an unexpected type
SELECT * FROM raw_data WHERE value IS NOT Float64
```

### can_cast

`can_cast(val, TargetType)` → Boolean

Returns whether CAST would succeed for this value. Allows truncation (matches CAST semantics); returns false only on overflow, parse failure, or unsupported conversion pair.

```sql
SELECT * FROM t WHERE can_cast(x, Int32) AND CAST(x AS Int32) > 0
SELECT * FROM t WHERE can_cast(x, UInt8)   -- false for 5000, true for 3.14
```

### try_cast

`try_cast(val, TargetType)` → TargetType / null

Attempts to cast a value to the target type. Returns NULL on failure instead of throwing. Follows CAST semantics (including truncation) on success.

```sql
SELECT try_cast(x, Int32) FROM t           -- NULL if x can't be parsed/converted
SELECT COALESCE(try_cast(x, Float64), 0.0) FROM t  -- default on failure

-- Type-narrowing bind: combines can_cast + CAST in one expression
SELECT * FROM t WHERE x AS Int32 y AND y > 0
-- Desugars to: WHERE can_cast(x, Int32) AND CAST(x AS Int32) > 0

-- Avoids repeating complex expressions
SELECT * FROM t WHERE json_value(data, '$.score') AS Float64 score AND score > 0.5
```

Type literals (`Int32`, `Float64`, `String`, etc.) are reserved keywords in expression position. They produce a `Type` value that can be compared with `typeof()` results using `=`, `!=`, `IN`, `CASE`, and `IS`. See [Type Literals and typeof()](../sql/type-system.md#type-literals-and-typeof) in the SQL Reference for the full list of type names, escaping rules, and the type-narrowing bind syntax.

## Conditional & Null Handling

### coalesce

`coalesce(a, b, ...)` → first non-null type

Returns the first non-null argument. Numeric arguments may be of mixed kinds; the result is the widest numeric kind among them. For non-numeric kinds every argument must share the same kind.

```sql
SELECT coalesce(primary_score, fallback_score) AS score FROM results
SELECT coalesce(name, 'unknown') AS safe_name FROM users
```

### nullif

`nullif(a, b)` → same type as `a` (or promoted numeric kind)

Returns NULL when `a` equals `b`, otherwise returns `a`. The inverse of `coalesce`: useful for turning a sentinel value into a true NULL so it composes with COALESCE / aggregates that skip nulls. Numeric arguments may be of mixed kinds; the result is the widest numeric kind. For non-numeric kinds both arguments must share the same kind.

```sql
-- Turn empty strings into NULL
SELECT nullif(trim(comment), '') AS comment FROM feedback

-- Avoid divide-by-zero: the row produces NULL instead of an error
SELECT total / nullif(count, 0) AS average FROM stats

-- Erase a sentinel before aggregation
SELECT avg(nullif(score, -1)) FROM measurements
```

### greatest

`greatest(a, b, ...)` → scalar / String

Returns the largest non-null argument. Null arguments are skipped; the result is null only when every argument is null. Mixed numeric kinds promote to the widest.

```sql
SELECT greatest(a, b, c) AS max_val FROM data
```

### least

`least(a, b, ...)` → scalar / String

Returns the smallest non-null argument. Null arguments are skipped; the result is null only when every argument is null. Mixed numeric kinds promote to the widest.

```sql
SELECT least(a, b, c) AS min_val FROM data
```

## Floating-Point Predicates

PostgreSQL-conformant predicates that classify floating-point values. All three accept `Float16`, `Float32`, or `Float64`, return `Boolean`, and propagate NULL inputs to NULL outputs. The undash aliases (`isnan`, `isfinite`) match the PG built-in spellings.

### is_nan

`is_nan(x)` → Boolean (alias: `isnan`)

Returns true when `x` is NaN, false for any finite or infinite value.

```sql
SELECT * FROM data WHERE NOT is_nan(score)
```

### is_finite

`is_finite(x)` → Boolean (alias: `isfinite`)

Returns true when `x` is a finite number (not NaN, not ±infinity).

```sql
SELECT * FROM data WHERE is_finite(value)
SELECT count(*) FILTER (WHERE NOT is_finite(weight)) AS bad_rows FROM model_outputs
```

### is_infinite

`is_infinite(x)` → Boolean

Returns true when `x` is positive or negative infinity. Note that `is_finite` and `is_infinite` are not strict complements: `is_infinite(NaN)` and `is_finite(NaN)` both return false.

```sql
SELECT count(*) FILTER (WHERE is_infinite(rate)) AS overflow_rows FROM metrics
```

## See Also

- [Numeric Functions](numeric.md) -- arithmetic, rounding, `sign`, normalization
- [Random & Sampling](random.md) -- `random`, `hash_split`, distribution samplers
- [UUID Functions](uuid.md) -- UUID generation and inspection
- [String Functions](string.md) -- text manipulation and formatting
- [Array Functions](array.md) -- array construction and manipulation
- [JSON Functions](json.md) -- JSON path access and type extraction
