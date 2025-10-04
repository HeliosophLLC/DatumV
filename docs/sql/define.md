---
title: DEFINE
---

## Why Use This

When you have several LET bindings and data quality checks (ASSERTs) in one query, they can clutter the SELECT list. DEFINE groups them in a block at the top so you can see all your setup in one place -- like a variable declaration section at the top of a function.

## Before and After

The same query written both ways:

**Without DEFINE** -- inline LET bindings and a trailing ASSERT:

```sql
SELECT
  LET subtotal = unit_price * quantity,
  LET tax = subtotal * 0.08,
  order_id,
  subtotal AS line_total,
  tax AS tax_amount,
  subtotal + tax AS grand_total
FROM orders
ASSERT subtotal > 0 MESSAGE 'subtotal must be positive' ON FAIL SKIP
ASSERT tax >= 0 MESSAGE 'tax cannot be negative' ON FAIL WARN
```

**With DEFINE** -- all setup grouped at the top:

```sql
SELECT DEFINE {
    LET subtotal = unit_price * quantity;
    LET tax = subtotal * 0.08;
    ASSERT subtotal > 0 MESSAGE 'subtotal must be positive' ON FAIL SKIP;
    ASSERT tax >= 0 MESSAGE 'tax cannot be negative' ON FAIL WARN;
} order_id,
  subtotal AS line_total,
  tax AS tax_amount,
  subtotal + tax AS grand_total
FROM orders
```

Both produce identical results. DEFINE just moves the setup out of the column list.

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

## Gotchas

- **A SELECT can have at most one DEFINE block** -- you cannot split definitions across multiple blocks.
- **You cannot mix DEFINE with inline LET in the same SELECT** -- pick one style. If you use a DEFINE block, all LET bindings must go inside it.
- **Inside the block, all LET bindings are evaluated before any ASSERT, regardless of declaration order** -- even if you write an ASSERT above a LET, the LET runs first. This means ASSERTs can safely reference any LET binding in the block.

## See Also

- [ASSERT](assert.md)
- [SELECT](select.md)
- [Lambda Expressions](lambda-expressions.md)
