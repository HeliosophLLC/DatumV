---
title: Array, Struct & Index Literals
---

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
