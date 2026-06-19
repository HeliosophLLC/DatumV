---
title: Comments
---

## Realistic Example

```sql
-- Pipeline: build a clean training dataset from raw e-commerce data
WITH
  -- Step 1: filter to only completed orders from the last 90 days
  recent_orders AS (
    SELECT order_id, customer_id, order_date, total_amount
    FROM orders
    WHERE status = 'completed'
      AND order_date >= '2026-01-15'  /* 90-day lookback window */
  ),
  -- Step 2: join customer demographics
  enriched AS (
    SELECT
      o.order_id,
      o.total_amount,
      c.region,
      c.signup_date,
      /* days between signup and first order — a key retention feature */
      DATEDIFF('day', c.signup_date, o.order_date) AS days_to_order
    FROM recent_orders o
    INNER JOIN customers c ON o.customer_id = c.customer_id
  ),
  -- Step 3: aggregate per region for the summary report
  summary AS (
    SELECT
      region,
      COUNT(*) AS order_count,
      AVG(total_amount) AS avg_order_value,
      AVG(days_to_order) AS avg_days_to_order
    FROM enriched
    GROUP BY region
  )
-- Step 4: export the final result sorted by volume
SELECT * FROM summary ORDER BY order_count DESC
```

## Syntax

Line comments start with `--` and continue to the end of the line. Block comments are enclosed in `/* ... */`. Both styles are stripped during tokenization and may appear anywhere whitespace is allowed.

```sql
-- This is a line comment
SELECT
    col1,           -- inline comment
    /* col2, */     -- block comment can disable code
    col3
FROM my_table
```

Block comments do not nest.

## See Also

- [SELECT](select.md)
- [FROM](from.md)
- [WHERE](filtering.md)
