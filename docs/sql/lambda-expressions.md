---
title: Lambda Expressions
---

Lambda expressions define inline anonymous functions for use with higher-order functions such as `array_transform` and `array_filter`. They are not first-class values — they can only appear as arguments to functions that expect them.

### Syntax

A single parameter needs no parentheses:

```sql
SELECT array_transform(prices, p -> p * 1.1) FROM products
```

Parentheses are optional for a single parameter and required for multiple parameters:

```sql
SELECT array_filter(scores, (s) -> s > 0.5) FROM students
```

The arrow operator is `->` (thin arrow). The body is any scalar expression.

### Closure capture

Lambda bodies can reference columns from the enclosing row:

```sql
SELECT array_transform(prices, p -> p * discount) FROM products
```

Here `discount` is a column on the `products` table, captured by the lambda at evaluation time.

### Restrictions

- Lambdas cannot appear outside a higher-order function argument list.
- Lambdas cannot be aliased, stored, or passed between queries.
- Lambda parameter names shadow column names of the same name within the body.

## See Also

- [Array, Struct & Index Literals](literals.md)
- [CASE Expressions](case-expressions.md)
- [SELECT](select.md)
