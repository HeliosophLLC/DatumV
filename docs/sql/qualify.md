---
title: QUALIFY
---

## Why Use This

The most common use of QUALIFY is "give me the top N rows per group" — the top 3 products per category, the latest order per customer, the best score per student. Without QUALIFY, you'd need to wrap your query in a subquery just to filter on a window function result.

## How It Works

QUALIFY is a filter that runs after window functions have been computed. Window functions like ROW_NUMBER() assign a ranking to each row within a group, but there's no standard SQL way to filter on that ranking without wrapping the whole thing in a subquery. QUALIFY lets you write the filter directly: "keep only the rows where the ranking meets my condition." The window function runs first, QUALIFY filters second — one query, no nesting.

Filter rows based on the result of a window function, without needing a subquery wrapper. QUALIFY is evaluated after window functions but before SELECT projection.

```sql
-- Syntax
SELECT ...
FROM table
QUALIFY <predicate>
```

### Referencing window aliases

QUALIFY can reference aliases defined in the SELECT list:

```sql
SELECT
    department,
    employee,
    salary,
    ROW_NUMBER() OVER (PARTITION BY department ORDER BY salary DESC) AS rn
FROM employees
QUALIFY rn <= 3
```

### Inline window functions

QUALIFY also supports inline window function calls that do not appear in the SELECT list. The window function is computed internally and the synthetic column is stripped from the output:

```sql
SELECT department, employee, salary
FROM employees
QUALIFY ROW_NUMBER() OVER (PARTITION BY department ORDER BY salary DESC) <= 3
```

### Combining with other clauses

QUALIFY slots into the pipeline between HAVING and ORDER BY:

```
FROM → JOIN → WHERE → GROUP BY → HAVING → Window → SCAN → QUALIFY → SELECT → DISTINCT → ORDER BY → LIMIT
```

All clauses can coexist:

```sql
SELECT department, SUM(salary) AS total,
       ROW_NUMBER() OVER (ORDER BY department) AS rn
FROM employees
WHERE status = 'active'
GROUP BY department
HAVING COUNT(*) > 1
QUALIFY rn = 1
ORDER BY total DESC
LIMIT 10
```

### QUALIFY vs. subquery

QUALIFY eliminates the common subquery pattern for window-function filtering:

```sql
-- Without QUALIFY (subquery wrapper)
SELECT * FROM (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY category ORDER BY score DESC) AS rn
    FROM data
) sub
WHERE rn <= 5

-- With QUALIFY (equivalent, no subquery)
SELECT *, ROW_NUMBER() OVER (PARTITION BY category ORDER BY score DESC) AS rn
FROM data
QUALIFY rn <= 5
```

### Execution model

QUALIFY is a streaming filter (0 query units) — it applies a FilterOperator to each row after window function computation. The window function itself remains a blocking operator, but the QUALIFY predicate adds zero additional memory or cost.

## See Also

- [Window Functions](window-functions.md)
- [SELECT](select.md)
- [ASSERT](assert.md)
