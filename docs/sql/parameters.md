---
title: Parameterized Queries
---

## Why Use This

Parameterized queries let you write a query once and run it with different values — change the threshold, switch the category, adjust the date range — without editing the SQL. This is essential for automation, scripts, and API integration.

Named parameters use PostgreSQL-style `$name` syntax. Parameters can appear anywhere an expression is valid — WHERE, SELECT, JOIN ON, ORDER BY, HAVING, CASE, and function arguments.

```sql
-- Filter with a parameter
SELECT * FROM data WHERE score > $threshold

-- Multiple parameters
SELECT * FROM data WHERE category = $category AND score > $min_score

-- In expressions
SELECT name, score * $weight AS weighted FROM data

-- In function arguments
SELECT min_max_normalize(score, $min, $max) AS norm FROM data
```

### CLI usage

Pass values with repeatable `--param key=value` flags:

```bash
datum-ingest query "SELECT * FROM data WHERE score > $threshold" \
  --source "data=./data.csv" \
  --param threshold=0.5

datum-ingest explore "SELECT * FROM data WHERE category = $cat" \
  --source "data=./data.csv" \
  --param cat=electronics
```

### Value type inference

Parameter values are parsed from strings with automatic type inference:

| Value | Inferred type |
|-------|---------------|
| `42`, `3.14`, `-1.5` | Float32 |
| `true`, `false` | Boolean |
| `null` | Null |
| Everything else | String |

### Binding model

Parameters use **early binding** — `$name` placeholders are parsed into `ParameterExpression` AST nodes, then substituted with `LiteralExpression` values before the query planner runs. This preserves all existing optimizations (predicate pushdown, statistics pruning, bloom filter acceleration, index seek) without modification.

If a query references a parameter that was not supplied, parsing succeeds but binding fails with a diagnostic listing the missing parameter names. Supplying parameters that are not referenced in the query also produces an error.

### gRPC usage

The `QueryRequest` message accepts a `parameters` map:

```protobuf
map<string, DataValueMessage> parameters = 3;
```

See [Compute Backend — Query](compute.md#query-server-streaming) for details.

## See Also

- [SELECT](select.md)
- [EXPLAIN](explain.md)
- [Type System](type-system.md)
