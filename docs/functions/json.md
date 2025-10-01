---
title: JSON Functions
category: json
---

# JSON Functions

JSON path access, existence testing, and array inspection. All JSON functions parse the input string on each call (QU 5).

### json_value

`json_value(col, path)` → String / Float32 / null | QU: 5

Extract a scalar value from a JSON string at the given path. Returns String, Float32, or null depending on the JSON element type.

```sql
SELECT json_value(metadata, '$.category') AS cat FROM records
SELECT json_value(data, '$.score') AS score FROM events
```

### json_query

`json_query(col, path)` → JsonValue / Vector | QU: 5

Extract a JSON fragment (array or object) at the given path. Returns JsonValue, or Vector if all elements are numeric.

```sql
SELECT json_query(metadata, '$.tags') AS tags FROM records
SELECT json_query(data, '$.coordinates') AS coords FROM locations
```

### json_exists

`json_exists(col, path)` → Float32 | QU: 5

Returns 1.0 if the path exists in the JSON document, 0.0 otherwise.

```sql
SELECT * FROM records WHERE json_exists(metadata, '$.category') = 1
```

### json_array_length

`json_array_length(col, [path])` → Float32 | QU: 5

Count the number of elements in a JSON array at root or at the specified path.

```sql
SELECT json_array_length(metadata) AS root_len FROM records
SELECT json_array_length(data, '$.items') AS item_count FROM orders
```

## See Also

- [String Functions](string.md) -- text manipulation, search, and regular expressions
- [Array Functions](array.md) -- typed array construction and manipulation
- [Utility & Type Conversion Functions](utility.md) -- type checks and casting
