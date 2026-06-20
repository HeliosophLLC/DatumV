---
title: ASSERT
---

ASSERT validates a predicate against every projected row. Unlike WHERE (which filters silently before projection), ASSERT runs after projection and can abort the query, skip failing rows, or emit diagnostic warnings depending on the configured failure mode.

### Syntax

```sql
SELECT columns
FROM table
ASSERT predicate [MESSAGE expression] [ON FAIL ABORT | SKIP | WARN]
```

Multiple ASSERT clauses may follow a single SELECT; they are evaluated left-to-right and all must pass (or be configured to skip/warn):

```sql
SELECT id, amount, name FROM orders
ASSERT amount > 0     MESSAGE 'amount must be positive'   ON FAIL SKIP
ASSERT name IS NOT NULL                                   ON FAIL WARN
```

### Failure modes

| Mode | Behavior |
|------|----------|
| `ABORT` (default) | Throws immediately. No further rows are produced. |
| `SKIP` | Omits the failing row from the output silently. |
| `WARN` | Keeps the row in the output and records a diagnostic. |

### MESSAGE

The optional `MESSAGE` expression provides a human-readable failure description. It may reference any column in the projected row, including LET bindings:

```sql
SELECT id, amount FROM orders
ASSERT amount > 0 MESSAGE CONCAT('bad amount on order ', CAST(id AS VARCHAR))
```

### Interaction with LET and QUALIFY

ASSERT runs after SELECT projection (including LET evaluation). This means ASSERT predicates may reference computed LET bindings directly:

```sql
SELECT LET total = price * qty, id, price, qty, total FROM line_items
ASSERT total >= 0 MESSAGE 'negative total'
```

QUALIFY is a pre-projection window filter; ASSERT is a post-projection row validator. The pipeline order is:

```
FROM → JOIN → WHERE → GROUP BY → HAVING → Window → SCAN → QUALIFY → SELECT (LET) → ASSERT → DISTINCT → ORDER BY → LIMIT
```

### Execution model

ASSERT is a streaming pass (0 query units). Each row is checked individually; no buffering occurs. ABORT mode short-circuits the entire pipeline on the first failure.

## See Also

- [DEFINE](define.md)
- [QUALIFY](qualify.md)
- [SELECT](select.md)
