---
title: Array Functions
category: array
---

# Array Functions

Typed array construction, inspection, search, manipulation, and string conversion.

> **Tip:** `len(arr)` also works as an alias for `array_length(arr)` since `len()` supports Array inputs.

> **Tip:** Arrays can be constructed with bracket syntax: `[1, 2, 3]` is equivalent to `array(1, 2, 3)`.

### array

`array(a, b, c, ...)` → Array | QU: 1

Construct a typed Array from one or more values. All arguments must share the same data kind.

```sql
SELECT array(1, 2, 3) AS nums
SELECT [1, 2, 3] AS nums  -- bracket syntax equivalent
```

### array_length

`array_length(arr)` → Float32 | QU: 1

Number of elements in the array.

```sql
SELECT array_length(array(1, 2, 3)) -- 3
```

### array_join

`array_join(arr, separator)` → String | QU: 1

Join elements into a String with separator. Null elements are skipped. String elements used directly; others converted via ToString.

```sql
SELECT array_join(array('a', 'b', 'c'), ', ') -- 'a, b, c'
```

### array_contains

`array_contains(arr, value)` → Boolean | QU: 1

Returns whether the array contains the value (by equality).

```sql
SELECT array_contains(array(1, 2, 3), 2) -- true
```

### array_position

`array_position(arr, value)` → Float32 | QU: 1

1-based index of the first matching element, or null if not found.

```sql
SELECT array_position(array('a', 'b', 'c'), 'b') -- 2
```

### array_sort

`array_sort(arr)` → Array | QU: 1

Sorted copy (ascending). Uses ORDER BY comparison semantics -- nulls sort last. Supports Float32, UInt8, String, Date, DateTime elements.

```sql
SELECT array_sort(array(3, 1, 2)) -- [1, 2, 3]
```

### array_reverse

`array_reverse(arr)` → Array | QU: 1

Reversed copy of the array.

```sql
SELECT array_reverse(array(1, 2, 3)) -- [3, 2, 1]
```

### array_distinct

`array_distinct(arr)` → Array | QU: 1

Remove duplicates, preserving first-occurrence order. Uses DataValue equality.

```sql
SELECT array_distinct(array(1, 2, 2, 3, 1)) -- [1, 2, 3]
```

### array_slice

`array_slice(arr, start, length)` → Array | QU: 1

Sub-array extraction. 1-based start, clamped to bounds. Returns empty array if out of range.

```sql
SELECT array_slice(array(10, 20, 30, 40), 2, 2) -- [20, 30]
```

### array_concat

`array_concat(arr1, arr2)` → Array | QU: 1

Concatenate two arrays. Both must share the same element kind.

```sql
SELECT array_concat(array(1, 2), array(3, 4)) -- [1, 2, 3, 4]
```

### array_get

`array_get(arr, index)` → element type | QU: 1

Element at a 1-based index. Returns null if index is out of bounds or either argument is null. Return type matches the array's element kind.

```sql
SELECT array_get(array('a', 'b', 'c'), 2) -- 'b'
```

### array_min

`array_min(arr)` → element type | QU: 1

Minimum element, skipping nulls. Returns null for an empty or all-null array. Return type matches the array's element kind.

```sql
SELECT array_min(array(3, 1, 2)) -- 1
```

### array_max

`array_max(arr)` → element type | QU: 1

Maximum element, skipping nulls. Returns null for an empty or all-null array. Return type matches the array's element kind.

```sql
SELECT array_max(array(3, 1, 2)) -- 3
```

### array_sum

`array_sum(arr)` → Float32 | QU: 1

Sum of numeric (Float32 or UInt8) elements, skipping nulls. Returns null for an empty or all-null array. Always returns Float32.

```sql
SELECT array_sum(array(1, 2, 3)) -- 6
```

### array_avg

`array_avg(arr)` → Float32 | QU: 1

Average (mean) of numeric elements, skipping nulls. Returns null for an empty or all-null array. Always returns Float32.

```sql
SELECT array_avg(array(2, 4, 6)) -- 4
```

### array_transform

`array_transform(arr, element -> expr)` → Array | QU: 1

Applies a lambda to each element, returning a new array of transformed values.

```sql
SELECT array_transform(array(1, 2, 3), x -> x * 2) -- [2, 4, 6]
```

### array_filter

`array_filter(arr, element -> Boolean)` → Array | QU: 1

Filters an array, keeping only elements where the lambda predicate returns true.

```sql
SELECT array_filter(array(1, 2, 3, 4, 5), x -> x > 2) -- [3, 4, 5]
```

## See Also

- [String Functions](string.md) -- string_to_array, regexp_split_to_array, and array_join
- [JSON Functions](json.md) -- json_array_length and JSON array extraction
- [Utility & Type Conversion Functions](utility.md) -- type checks and casting
