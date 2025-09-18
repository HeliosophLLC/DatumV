# Special Functions

Reference for DatumIngest functions and syntax that go beyond standard SQL scalar and aggregate semantics.

## Lambda Expressions

Lambda expressions define inline anonymous functions, used as arguments to higher-order functions.

### Syntax

```
parameter -> body
(parameter) -> body
(param1, param2) -> body
```

The arrow operator is `->`. The body is any scalar expression. Parentheses are optional for a single parameter.

### Closure capture

Lambda bodies can reference columns from the enclosing row. Column values are captured at evaluation time.

```sql
-- 'discount' is a column on the table, not a lambda parameter
SELECT array_transform(prices, p -> p * discount) FROM products
```

Lambda parameter names shadow column names of the same name within the body.

---

## array_transform

Applies a lambda to each element of an array, returning a new array of the transformed values.

```sql
array_transform(array, element -> expression) → Array
```

### Examples

```sql
-- Double every price
SELECT array_transform(prices, p -> p * 2) FROM products

-- Uppercase every tag
SELECT array_transform(tags, t -> upper(t)) FROM articles
```

---

## array_filter

Filters an array, keeping only elements where the lambda predicate returns true.

```sql
array_filter(array, element -> Boolean) → Array
```

### Examples

```sql
-- Keep scores above 50
SELECT array_filter(scores, s -> s > 50) FROM students

-- Keep non-empty strings
SELECT array_filter(names, n -> len(n) > 0) FROM data
```

---

## Array Literal Syntax

Bracket syntax `[a, b, c]` is syntactic sugar for `array(a, b, c)`.

```sql
SELECT [1, 2, 3]                 -- array of numbers
SELECT ['a', 'b', 'c']          -- array of strings
SELECT []                        -- empty array
```

Array literals compose naturally with lambdas and other functions:

```sql
SELECT array_filter([10, 20, 30, 40], x -> x > 25)
-- result: [30, 40]

SELECT array_transform([1, 2, 3], x -> x * x)
-- result: [1, 4, 9]
```

Nested array literals are supported:

```sql
SELECT [[1, 2], [3, 4]]
```
