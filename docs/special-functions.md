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

---

## Struct Literal Syntax

Brace syntax `{ field: expr, ... }` constructs a typed struct value with named fields.

```sql
SELECT {name: 'alice', score: 9.5}                  -- two-field struct
SELECT {x: lng, y: lat} FROM waypoints              -- fields from columns
SELECT {}                                            -- empty struct
```

Field names are identifiers; values can be any scalar expression. Types are inferred from each field's expression at plan time. Struct literals can be nested:

```sql
SELECT {point: {x: 1.0, y: 2.0}, radius: 5.0}
```

---

## Index Access (Bracket Operator)

The postfix `[index]` operator accesses array elements by zero-based integer position, or struct fields by name string. Multiple subscripts chain left-to-right.

```sql
-- Array element access (0-based integer index)
SELECT scores[0]           -- first element
SELECT embeddings[127]     -- element at position 127

-- Struct field access (string key)
SELECT record['name']      -- field named 'name' (case-insensitive)
SELECT meta['created_at']  -- field named 'created_at'

-- Chained access
SELECT record['scores'][2]              -- element 2 of a nested array field
SELECT {x: 10, y: 20}['y']             -- inline struct: returns 20
```

Accessing an array index out of bounds or a struct field that does not exist returns null.

---

## LET Bindings

`LET` declares a named intermediate expression inside a SELECT list. The expression is evaluated once per row, cached, and can be referenced by subsequent LET bindings and output columns. LET bindings are not emitted as output columns unless given an `AS alias`.

### Syntax

```sql
SELECT LET name = expression [AS alias], ... columns ... FROM table
```

LET bindings appear before the first output column, separated from it and from each other by commas.

### Examples

```sql
-- Compute once, reference twice
SELECT LET total = price * qty, id, price, qty, total FROM line_items

-- Bind without emitting (no AS alias — not in output)
SELECT LET tax = amount * 0.1, id, amount FROM orders

-- Chain: later bindings may reference earlier ones
SELECT LET subtotal = price * qty,
       LET tax      = subtotal * 0.1,
       subtotal, tax
FROM line_items
```

### Evaluation order and scope

- LET bindings evaluate left-to-right; each may reference any binding declared before it.
- A binding is in scope for all subsequent LET expressions and for all output columns.
- LET is scoped to a single SELECT — bindings are not visible in subqueries or CTEs.
- LET expressions may reference window functions; the window result is computed before the binding is evaluated.

---

## ASSERT Clause

`ASSERT` validates a predicate against every projected (post-LET) row. It runs after SELECT projection, so it can reference both source columns and LET bindings. Multiple ASSERT clauses may appear after the column list; they are evaluated left-to-right.

### Syntax

```sql
SELECT columns FROM table
ASSERT predicate [MESSAGE expression] [ON FAIL ABORT | SKIP | WARN]
ASSERT ...
```

### Failure modes

| Mode | Behaviour |
|------|-----------|
| `ABORT` (default) | Throws immediately. No further rows are produced. |
| `SKIP` | Omits the failing row from the output silently. |
| `WARN` | Keeps the row and records a diagnostic (accessible via `AssertionDiagnostics`). |

### MESSAGE

`MESSAGE` accepts any scalar expression evaluated against the projected row and produces a human-readable failure description. It may reference LET bindings.

### Examples

```sql
-- Abort on any negative amount
SELECT id, amount FROM orders
ASSERT amount > 0

-- Skip bad rows and attach a dynamic message
SELECT id, amount FROM orders
ASSERT amount > 0
    MESSAGE CONCAT('order ', CAST(id AS VARCHAR), ' has non-positive amount')
    ON FAIL SKIP

-- Reference a LET binding in the predicate
SELECT LET total = price * qty, id, total FROM line_items
ASSERT total >= 0 MESSAGE 'negative total' ON FAIL WARN
```

### Pipeline position

ASSERT runs after projection and before DISTINCT/ORDER BY/LIMIT:

```
FROM → JOIN → WHERE → GROUP BY → HAVING → Window → QUALIFY → SELECT (LET) → ASSERT → DISTINCT → ORDER BY → LIMIT
```

---

## DEFINE Block

`DEFINE` groups LET bindings and ASSERT clauses inside a brace-delimited block placed immediately after `SELECT`. It is purely syntactic sugar — at parse time the block is flattened into the query's LET bindings and ASSERT list. All LET bindings evaluate before any ASSERT regardless of declaration order within the block.

### Syntax

```sql
SELECT DEFINE {
    LET name = expression [AS alias];
    ASSERT predicate [MESSAGE expression] [ON FAIL ABORT | SKIP | WARN];
} columns
FROM table
```

Declarations are separated by semicolons; a trailing semicolon before `}` is optional. LET and ASSERT may appear in any order inside the block.

### Equivalence

These two queries are identical:

```sql
-- DEFINE form
SELECT DEFINE {
    LET total = price * qty;
    ASSERT total >= 0 ON FAIL SKIP;
} total
FROM line_items

-- Inline equivalent
SELECT LET total = price * qty, total
FROM line_items
ASSERT total >= 0 ON FAIL SKIP
```

### Combining with trailing ASSERTs

ASSERT clauses from the DEFINE block and trailing ASSERT clauses after the column list are all collected and evaluated together, with block-sourced assertions applied first.

```sql
SELECT DEFINE {
    LET tax = amount * 0.1;
    ASSERT amount > 0 ON FAIL SKIP;
} id, amount, tax
FROM orders
ASSERT tax < 1000 ON FAIL WARN    -- evaluated after the DEFINE block's assertion
```

### Constraints

- At most one DEFINE block per SELECT.
- Cannot be combined with inline LET bindings in the same SELECT.
