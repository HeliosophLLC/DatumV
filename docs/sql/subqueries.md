---
title: Subqueries
---

### Derived tables (FROM subqueries)

A subquery in the FROM clause produces an inline table. The subquery must be aliased.

```sql
SELECT id, name FROM (
  SELECT id, name, value FROM data WHERE value > 100
) AS filtered
WHERE name LIKE 'item_%'
```

### Scalar subqueries

A subquery that returns a single value can appear anywhere an expression is valid — in the SELECT list, WHERE clause, CASE expressions, and function arguments. The subquery must return exactly one column and at most one row.

```sql
-- Uncorrelated: constant-folded at plan time
SELECT name, score - (SELECT AVG(score) FROM data) AS deviation
FROM data

-- Correlated: re-evaluated per outer row
SELECT name, (SELECT MAX(value) FROM details WHERE details.id = data.id) AS max_val
FROM data
```

Uncorrelated scalar subqueries are executed once during planning and replaced with a literal value. Correlated scalar subqueries reference columns from the outer query and are executed once per outer row.

### IN / NOT IN subqueries

Filter rows based on whether a value appears in the result of a subquery. The subquery must return exactly one column.

```sql
-- Uncorrelated IN: constant-folded to a literal value list at plan time
SELECT name FROM employees
WHERE department_id IN (SELECT id FROM active_departments)

-- Correlated IN: decorrelated into a semi-join
SELECT name FROM employees
WHERE department_id IN (
  SELECT id FROM departments WHERE departments.region = employees.region
)

-- NOT IN: anti-semi-join with SQL-standard NULL semantics
SELECT name FROM employees
WHERE department_id NOT IN (SELECT id FROM excluded_departments)
```

`NOT IN` follows SQL three-valued logic: if the subquery result contains any NULL value, no rows pass the filter (because `x NOT IN (..., NULL, ...)` evaluates to UNKNOWN for every `x`).

### EXISTS / NOT EXISTS subqueries

Test whether a subquery produces any rows. The subquery's column list is irrelevant — only row existence matters. `SELECT 1` is conventional.

```sql
-- Uncorrelated EXISTS: boolean gate evaluated at plan time
SELECT name FROM data
WHERE EXISTS (SELECT 1 FROM feature_flags WHERE flag = 'enabled')

-- Correlated EXISTS: decorrelated into a semi-join
SELECT name FROM customers
WHERE EXISTS (
  SELECT 1 FROM orders WHERE orders.customer_id = customers.id
)

-- Correlated NOT EXISTS: decorrelated into an anti-semi-join
SELECT name FROM customers
WHERE NOT EXISTS (
  SELECT 1 FROM orders WHERE orders.customer_id = customers.id
)
```

### Execution strategy

| Form | Uncorrelated | Correlated |
|------|-------------|------------|
| Scalar `(SELECT ...)` | Constant-folded at plan time | `ScalarSubqueryOperator` per outer row |
| `IN (SELECT ...)` | Constant-folded to `IN (values)` | Left semi-join (hash join) |
| `NOT IN (SELECT ...)` | Constant-folded to `NOT IN (values)` | Left anti-semi-join (null-sensitive) |
| `EXISTS (SELECT ...)` | Boolean gate at plan time | Left semi-join (hash join) |
| `NOT EXISTS (SELECT ...)` | Boolean gate at plan time | Left anti-semi-join |

Correlated subqueries are decorrelated by the query planner: correlation predicates in the inner WHERE are extracted and become the join's ON condition, while non-correlated predicates remain as filters on the inner plan.

## See Also

- [Common Table Expressions (WITH)](cte.md)
- [SELECT](select.md)
- [Set Operations](set-operations.md)
