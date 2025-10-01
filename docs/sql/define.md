---
title: DEFINE
---

DEFINE is syntactic sugar that groups LET bindings and ASSERT clauses inside a brace-delimited block placed directly after SELECT. It is purely a readability aid — at parse time the block is flattened into the query's LET bindings and ASSERT clauses.

### Syntax

```sql
SELECT DEFINE {
    LET name = expression [AS alias];
    ASSERT predicate [MESSAGE expression] [ON FAIL ABORT | SKIP | WARN];
} columns
FROM table
```

Declarations inside the block are separated by semicolons (trailing semicolon before `}` is optional). LET and ASSERT declarations may appear in any order inside the block; all LET bindings are evaluated before any ASSERT is checked, regardless of declaration order.

### Examples

```sql
-- All definitions grouped at the top for readability
SELECT DEFINE {
    LET tax      = amount * 0.1;
    LET subtotal = amount - discount;
    ASSERT amount > 0    MESSAGE 'amount must be positive'  ON FAIL SKIP;
    ASSERT discount >= 0 MESSAGE 'discount cannot be negative';
} id, amount, discount, subtotal, tax
FROM orders
```

Destructuring bindings work inside DEFINE blocks too:

```sql
-- Unpack a Vector result and validate components in the same block
SELECT DEFINE {
    LET (sin_m, cos_m) = cyclical_encode(month, 12);
    ASSERT sin_m BETWEEN -1.0 AND 1.0 ON FAIL WARN;
} sin_m AS s, cos_m AS c
FROM events

-- Named destructuring with an ASSERT guard
SELECT DEFINE {
    LET {lo, hi} = bounds_column;
    ASSERT hi > lo MESSAGE 'inverted bounds' ON FAIL SKIP;
} lo, hi
FROM ranges
```

### Equivalence to inline LET + trailing ASSERT

These two queries are identical:

```sql
-- DEFINE block form
SELECT DEFINE {
    LET total = price * qty;
    ASSERT total > 0 ON FAIL SKIP;
} total
FROM line_items

-- Equivalent inline form
SELECT LET total = price * qty, total
FROM line_items
ASSERT total > 0 ON FAIL SKIP
```

DEFINE assertions from the block and any trailing ASSERT clauses written after the column list are all collected into the same assertion list, with block-sourced assertions applied first.

### Constraints

- A SELECT may have at most one DEFINE block.
- DEFINE cannot be combined with inline LET bindings in the same SELECT.

## See Also

- [ASSERT](assert.md)
- [SELECT](select.md)
- [Lambda Expressions](lambda-expressions.md)
