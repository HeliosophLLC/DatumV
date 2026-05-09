---
title: JOIN
---

## Why Use This

Most real datasets live in multiple tables — customers in one, their orders in another, product details in a third. JOINs bring related data together into a single result.

All five standard join types are supported:

```sql
-- INNER JOIN: only matching rows
SELECT * FROM customers INNER JOIN orders ON customers.customer_id = orders.customer_id

-- LEFT JOIN: all rows from left, matching from right
SELECT * FROM customers LEFT JOIN orders ON customers.customer_id = orders.customer_id

-- RIGHT JOIN: all rows from right, matching from left
SELECT * FROM orders RIGHT JOIN products ON orders.product_id = products.product_id

-- FULL OUTER JOIN: all rows from both sides
SELECT * FROM images FULL OUTER JOIN captions ON images.image_id = captions.image_id

-- CROSS JOIN: cartesian product
SELECT * FROM products CROSS JOIN departments

-- Comma-separated FROM list (SQL-89 / PostgreSQL): equivalent to CROSS JOIN
SELECT * FROM products, departments
```

NULL keys never match (SQL three-valued logic). Hash join is used for INNER/LEFT/RIGHT/FULL OUTER; nested loop for CROSS.

## Common Patterns

### Customer-order lookup (INNER JOIN)

Find every customer and their orders. Customers without orders and orders without customers are excluded:

```sql
SELECT c.name, c.email, o.order_id, o.total_amount, o.order_date
FROM customers c
INNER JOIN orders o ON c.customer_id = o.customer_id
```

### Keeping all customers even without orders (LEFT JOIN)

List every customer. Those who have not placed an order still appear, with NULL in the order columns:

```sql
SELECT c.name, c.email, o.order_id, o.total_amount
FROM customers c
LEFT JOIN orders o ON c.customer_id = o.customer_id
```

### Pairing images with captions for training (LEFT JOIN with null handling)

Build a training dataset where every image is included. Images without captions get a default placeholder:

```sql
SELECT
    i.image_id,
    i.file_path,
    COALESCE(cap.caption_text, 'no caption available') AS caption
FROM images i
LEFT JOIN captions cap ON i.image_id = cap.image_id
```

## Gotchas

- **NULL keys never match in any join type** — rows with NULL join keys are silently excluded from INNER JOIN results.
- **LEFT JOIN preserves all left rows but right columns become NULL for non-matches** — filter carefully. A WHERE clause on a right-side column (e.g., `WHERE o.status = 'shipped'`) converts the LEFT JOIN into an INNER JOIN because NULLs fail the filter.
- **CROSS JOIN produces N x M rows** — use only when you intentionally want every combination (e.g., generating all product-region pairs for a report template).

### Comma-separated FROM list

A comma-separated FROM list is equivalent to writing the same sources joined with `CROSS JOIN`. A WHERE clause then expresses the matching condition — the classic SQL-89 join style:

```sql
-- Equivalent to: SELECT ... FROM customers CROSS JOIN orders WHERE ...
SELECT c.name, o.total_amount
FROM customers c, orders o
WHERE c.customer_id = o.customer_id
```

Any of the comma-separated sources may be a table, a subquery, or a table-valued function. A table-valued function in this position is implicitly lateral — its arguments may reference columns from sources written earlier in the FROM list:

```sql
-- unnest(t.tags) sees each row of t (implicit LATERAL on function sources)
SELECT t.id, tag.value
FROM tasks t, unnest(t.tags) AS tag
```

A correlated subquery in the comma position still requires the explicit `JOIN LATERAL` form — only function sources gain LATERAL semantics implicitly.

### LATERAL JOIN / APPLY

Sometimes the right side of a join needs to reference each row from the left — like expanding an array column per row. That's what LATERAL does.

A **lateral join** re-executes the right-hand source for every row from the left side, allowing the right side to reference columns from the left. The explicit `LATERAL` keyword is required after `CROSS JOIN` or `LEFT [OUTER] JOIN`. The T-SQL `CROSS APPLY` and `OUTER APPLY` syntax is also supported.

```sql
-- CROSS JOIN LATERAL: expand array column per row (no match → row excluded)
SELECT t.name, s.value
FROM data AS t
CROSS JOIN LATERAL UNNEST(t.scores) AS s

-- LEFT JOIN LATERAL: preserve rows with empty arrays (NULL-padded)
SELECT t.name, s.value
FROM data AS t
LEFT JOIN LATERAL UNNEST(t.scores) AS s

-- Lateral subquery: correlated derived table referencing outer columns
SELECT o.customer, sub.product
FROM orders AS o
LEFT JOIN LATERAL (
    SELECT i.product FROM items AS i WHERE i.order_id = o.id
) AS sub ON 1 = 1

-- T-SQL CROSS APPLY (equivalent to CROSS JOIN LATERAL)
SELECT t.name, s.value
FROM data AS t
CROSS APPLY UNNEST(t.scores) AS s

-- T-SQL OUTER APPLY (equivalent to LEFT JOIN LATERAL)
SELECT t.name, s.value
FROM data AS t
OUTER APPLY UNNEST(t.scores) AS s
```

LATERAL is supported with `CROSS JOIN` and `LEFT [OUTER] JOIN` only. The right-hand source can be a table-valued function or a subquery.

> **Performance:** Lateral joins use O(N × M) nested-loop execution — the right side is re-executed for each left row. No hash acceleration is possible. For large left-side tables, consider filtering the left side before the lateral join.

## See Also

- [FROM](from.md)
- [SELECT](select.md)
- [WHERE](filtering.md)
