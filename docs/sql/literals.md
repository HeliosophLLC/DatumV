---
title: Array, Struct & Index Literals
---

## Why Use This

Sometimes your data is nested -- a column contains an array of tags, or you need to construct a struct of coordinates on the fly. Array and struct literals let you build these directly in your query without a separate table or function.

## Common Patterns

### 1. Constructing an array of values inline

```sql
-- Build an array column directly in your output
SELECT product_id, [1, 2, 3] AS label_ids FROM products

-- Use an array with UNNEST to generate rows
SELECT * FROM UNNEST([10, 20, 30]) AS t(value)
```

### 2. Building a struct to group related values

```sql
-- Combine columns into a single structured value
SELECT {lat: 40.7, lng: -74.0, city: 'New York'} AS location

-- Build structs from existing columns
SELECT {name: customer_name, total: order_total, date: order_date} AS summary
FROM orders
```

### 3. Accessing nested data with index syntax

```sql
-- Get the first score from an array column
SELECT record['scores'][0] AS first_score FROM data

-- Access a named field in a struct column
SELECT address['city'] AS city FROM customers
```

## When to Use This vs JSON

If your data is already in JSON columns, use `json_value()` and `json_query()`. Struct literals are for constructing structured values inline. Arrays are for when you need a typed, ordered collection -- unlike JSON, array elements must all be the same type.

## Array Literals

Bracket syntax constructs arrays inline. `[a, b, c]` is syntactic sugar for `array(a, b, c)`.

```sql
SELECT [1, 2, 3]                          -- array of numbers
SELECT ['hello', 'world']                  -- array of strings
SELECT []                                  -- empty array
SELECT array_filter([10, 20, 30], x -> x > 15)  -- combined with lambdas
```

Nested array literals are supported:

```sql
SELECT [[1, 2], [3, 4]]
```

## Struct Literals

Brace syntax constructs struct values inline. Each field is a `name: expression` pair:

```sql
SELECT {name: 'alice', score: 9.5}                  -- two-field struct
SELECT {x: lng, y: lat, label: category} FROM data  -- fields from column references
SELECT {}                                            -- empty struct
```

Field names become the keys of the resulting struct value. Types are inferred from each field's expression at plan time. Struct literals can be nested:

```sql
SELECT {point: {x: 1.0, y: 2.0}, radius: 5.0}
```

## Index Access

The postfix `[index]` operator accesses array elements by position or struct fields by name. Multiple subscripts chain left-to-right.

### Array element access

```sql
SELECT scores[0]           -- first element (0-based)
SELECT matrix[1]           -- second element of a vector/array column
```

### Struct field access

```sql
SELECT row['name']         -- access field 'name' from a struct column
SELECT meta['created_at']  -- string key returns the named field
```

Field name lookup is case-insensitive. Accessing a field that does not exist returns null.

### Chained access

```sql
-- Access an element of an array stored inside a struct field
SELECT record['scores'][2] FROM data

-- Access a field of an inline struct literal
SELECT {x: 10, y: 20}['y']   -- returns 20
```

## See Also

- [Lambda Expressions](lambda-expressions.md)
- [SELECT](select.md)
- [CASE Expressions](case-expressions.md)
