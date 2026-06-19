---
title: CASE Expressions
---

CASE expressions provide inline conditional logic, similar to if/else chains.

### Searched CASE

Each WHEN clause contains an independent boolean condition:

```sql
SELECT CASE
         WHEN score >= 90 THEN 'A'
         WHEN score >= 80 THEN 'B'
         WHEN score >= 70 THEN 'C'
         ELSE 'F'
       END AS grade
FROM students
```

### Simple CASE

Compares an operand against each WHEN value:

```sql
SELECT CASE status
         WHEN 1 THEN 'active'
         WHEN 2 THEN 'inactive'
         WHEN 3 THEN 'banned'
         ELSE 'unknown'
       END AS status_label
FROM users
```

### Behavior

- WHEN clauses are evaluated in order; the first match wins.
- If no WHEN clause matches and no ELSE is provided, the result is NULL.
- For simple CASE, a NULL operand never matches any WHEN value (SQL three-valued logic).
- CASE expressions can appear anywhere an expression is valid: SELECT, WHERE, ORDER BY, GROUP BY, HAVING, and JOIN ON.
- CASE expressions can be nested.
- For simple single-condition cases, the `iif()` function provides a more concise alternative.
- **Short-circuit evaluation:** THEN and ELSE branches are only evaluated for
  rows that actually reach them. A branch that would fail for other rows is
  safe as long as the WHEN condition prevents those rows from reaching it.
  This enables patterns like:
  ```sql
  CASE WHEN "col" = 'NULL' THEN 0.0 ELSE CAST("col" AS FLOAT64) END
  ```
  The CAST is never executed for rows where `col` is the string `'NULL'`.

### Branch Type Coercion

When THEN/ELSE branches return different data types, DatumV applies
implicit type coercion following SQL Server-style type precedence rules:

1. **Common type wins.** If all branches share a common type through the
   standard widening chain (e.g. `UInt8 → Float32`, `Boolean → Float32`), that
   common type is used.
2. **Non-String type wins over String.** When some branches return `String` and
   others return a numeric, boolean, or temporal type, the non-String type is
   chosen as the result type. String values are parsed to the target type at
   runtime.
3. **Unparseable strings become NULL.** If a String branch value cannot be
   parsed to the target type at runtime, the result is NULL rather than an
   error. This is an ETL-friendly behavior — data flows through without
   interruption, and downstream consumers can detect NULLs as conversion
   failures.

```sql
-- The result type is Float32, not String.
-- The string '0' is parsed to the number 0 at runtime.
SELECT CASE WHEN x > 0 THEN '0' ELSE 1 END AS value
FROM data

-- If 'not_a_number' cannot be parsed, the result is NULL.
SELECT CASE WHEN x > 0 THEN 'not_a_number' ELSE 1 END AS value
FROM data
```

Coercible String targets include: `Float32`, `UInt8`, `Boolean`, `Date`,
`Timestamp`, `TimestampTz`, `Time`, `Duration`, `Uuid`, and `JsonValue`. Types like `Vector`,
`Matrix`, `Tensor`, `Image`, and `UInt8Array` cannot be coerced from String.

## Common Patterns

**Binning continuous values:**

```sql
SELECT CASE WHEN score >= 90 THEN 'A' WHEN score >= 80 THEN 'B' ELSE 'C' END AS grade
FROM students
```

**Label mapping:**

```sql
SELECT CASE status WHEN 1 THEN 'active' WHEN 2 THEN 'churned' ELSE 'unknown' END AS label
FROM users
```

## See Also

- [SELECT](select.md)
- [Lambda Expressions](lambda-expressions.md)
- [QUALIFY](qualify.md)
