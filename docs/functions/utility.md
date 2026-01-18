---
title: Utility & Type Conversion Functions
category: utility
---

# Utility & Type Conversion Functions

Type conversion, type introspection, and general-purpose conditional and null-handling functions.

## Type Conversion

### cast

`cast(val, TargetType)` → TargetType | QU: 1

Explicit type conversion. Accepts a type literal (`cast(x, Int32)`) or the `CAST(x AS Int32)` SQL syntax. Date to Float32 yields epoch days; DateTime to Float32 yields epoch seconds.

```sql
SELECT cast(score, UInt8) AS byte_score FROM data
SELECT CAST(score AS UInt8) AS byte_score FROM data  -- equivalent
```

### to_epoch

`to_epoch(val)` → Float32 | QU: 1

Convert Date to epoch days or DateTime to epoch seconds (since 1970-01-01) as Float32.

```sql
SELECT to_epoch(date_col) AS epoch_days FROM data
```

### typeof

`typeof(val)` → Type | QU: 1

Returns the runtime DataKind of a value as a Type tag. Use with type literals for type-oriented comparisons: `typeof(x) = Int32`.

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

`can_cast(val, TargetType)` → Boolean | QU: 1

Returns whether CAST would succeed for this value. Allows truncation (matches CAST semantics); returns false only on overflow, parse failure, or unsupported conversion pair.

```sql
SELECT * FROM t WHERE can_cast(x, Int32) AND CAST(x AS Int32) > 0
SELECT * FROM t WHERE can_cast(x, UInt8)   -- false for 5000, true for 3.14
```

### try_cast

`try_cast(val, TargetType)` → TargetType / null | QU: 1

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

`coalesce(a, b, ...)` → first non-null type | QU: 1

Returns first non-null argument.

```sql
SELECT coalesce(primary_score, fallback_score) AS score FROM results
SELECT coalesce(name, 'unknown') AS safe_name FROM users
```

### greatest

`greatest(a, b, ...)` → scalar / String | QU: 1

Returns maximum of scalar or string arguments.

```sql
SELECT greatest(a, b, c) AS max_val FROM data
```

### least

`least(a, b, ...)` → scalar / String | QU: 1

Returns minimum of scalar or string arguments.

```sql
SELECT least(a, b, c) AS min_val FROM data
```

### choose

`choose(index, v1, v2, ...)` → value type | QU: 1

Returns the value at 1-based index. NULL if out of range.

```sql
SELECT choose(2, 'a', 'b', 'c') -- 'b'
```

### is_nan

`is_nan(x)` → Float32 | QU: 1

Returns 1 if NaN, 0 otherwise.

```sql
SELECT * FROM data WHERE is_nan(score) = 0
```

### is_finite

`is_finite(x)` → Float32 | QU: 1

Returns 1 if finite, 0 if NaN or infinite.

```sql
SELECT * FROM data WHERE is_finite(value) = 1
```

### is_even

`is_even(x)` → Float32 | QU: 1

Returns 1 if x is an even integer, 0 otherwise.

```sql
SELECT * FROM data WHERE is_even(id) = 1
```

### is_odd

`is_odd(x)` → Float32 | QU: 1

Returns 1 if x is an odd integer, 0 otherwise.

```sql
SELECT * FROM data WHERE is_odd(id) = 1
```

### if_null

`if_null(x, default)` → value type | QU: 1

Returns x if not null, otherwise default.

```sql
SELECT if_null(score, 0) AS safe_score FROM data
```

### iif

`iif(cond, then, else)` → value type | QU: 1

Returns then when cond is truthy (non-null, non-zero), else otherwise. For multi-branch conditionals, see CASE expressions.

```sql
SELECT iif(age > 18, 'adult', 'minor') AS age_group FROM users
SELECT iif(score > 0.5, 'positive', 'negative') AS label FROM predictions
```

### random

`random()` → Float32 | QU: 1

Random float in [0, 1).

```sql
SELECT random() AS rand_val FROM data
SELECT * FROM data WHERE random() < 0.1  -- ~10% sample
```

## Catalog Introspection

### ident_current

`ident_current(table_name)` → Int64 | QU: 1

Returns the most recently reserved `IDENTITY` value for the named table — the value the previous `INSERT` produced. Mirrors SQL Server's `IDENT_CURRENT`.

Returns `NULL` when:
- The named table is not registered in the catalog
- The table has no `IDENTITY` column
- No values have been reserved yet (the counter still sits at the seed)

```sql
-- Insert a parent row, then reference its IDENTITY in a child INSERT.
INSERT INTO conversations (workspace, title) VALUES ('default', 'Chat');
INSERT INTO messages (conversation_id, body)
    VALUES (ident_current('conversations'), 'Hello');
```

Reads the persisted counter; values reserved by an in-flight `INSERT` session that hasn't committed yet are not visible. Marked impure (its result depends on external state), so multiple references within a row evaluate independently.

## See Also

- [String Functions](string.md) -- text manipulation and formatting
- [Array Functions](array.md) -- array construction and manipulation
- [JSON Functions](json.md) -- JSON path access and type extraction
