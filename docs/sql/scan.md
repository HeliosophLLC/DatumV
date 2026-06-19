---
title: SCAN
---

# SCAN Expression

`SCAN` computes a running fold (prefix scan) over ordered partitions. Each row's output feeds back as the accumulator for the next row: `output[i] = f(output[i-1], input[i])`. This enables computations that standard window functions cannot express — patterns where a row's result depends on its own previous output.

## Syntax

### Scalar accumulator

```sql
SELECT SCAN accumulator = expression
  INIT seed
  OVER (PARTITION BY ... ORDER BY ...)
  AS alias
FROM table
```

- `accumulator` — the feedback variable available inside `expression`. On the first row of each partition it holds the `INIT` value; on subsequent rows it holds the output of `expression` for the previous row.
- `expression` — any scalar SQL expression that may reference `accumulator`, the current row's columns, LET bindings, and `PREV(col)`.
- `INIT seed` — the initial accumulator value at the start of each partition. Evaluated against the first row, so it may reference columns (e.g. `INIT price`).
- `OVER (...)` — reuses the standard window clause syntax. `PARTITION BY` is optional; `ORDER BY` is required.
- `AS alias` — the output column name. The accumulator itself is not visible outside the SCAN expression.

### Multi-valued accumulator (tuple form)

```sql
SELECT SCAN (name1, name2, ...) = (expr1, expr2, ...)
  INIT (value1, value2, ...)
  OVER (PARTITION BY ... ORDER BY ...)
  AS (alias1, alias2, ...)
FROM table
```

Each name in the left-hand tuple is accessible inside all right-hand expressions. The result tuple is updated atomically before the next row.

## PREV pseudo-function

Inside a SCAN expression body, `PREV(column)` returns the value of `column` from the source row immediately before the current one in partition order. On the first row of a partition, `PREV(col)` returns `NULL`. This is distinct from the accumulator: `PREV` sees the **input**, the accumulator name sees the **output from the previous row**.

`PREV` is only available inside SCAN body expressions. Using it elsewhere produces an "Unknown function" error.

## Examples

### Exponential Moving Average

Every past value influences the current one with exponential decay — no finite window frame can express this.

```sql
SELECT date, price,
  SCAN ema = 0.1 * price + 0.9 * ema
    INIT price
    OVER (ORDER BY date)
    AS ema_10
FROM stock_prices
```

`INIT price` seeds the accumulator with the first observed price so the EMA starts in range.

### Sessionization

A new session starts when the gap since the last event exceeds 30 minutes. The session ID must reference itself.

```sql
SELECT user_id, timestamp,
  SCAN s = CASE
      WHEN PREV(timestamp) IS NULL THEN s
      WHEN date_diff('minute', PREV(timestamp), timestamp) > 30 THEN s + 1
      ELSE s
    END
    INIT 0
    OVER (PARTITION BY user_id ORDER BY timestamp)
    AS session_id
FROM clicks
```

### Streak Detection

The current winning streak resets to zero on a loss.

```sql
SELECT player_id, game_date,
  SCAN streak = CASE WHEN won = 1 THEN streak + 1 ELSE 0 END
    INIT 0
    OVER (PARTITION BY player_id ORDER BY game_date)
    AS current_streak
FROM games
```

### Episode Numbering (tuple form)

Segment a time series into episodes based on inactivity gaps, then number each step within the episode.

```sql
SELECT device_id, timestamp,
  SCAN (episode, step) = (
      CASE WHEN gap_minutes > 60 THEN episode + 1 ELSE episode END,
      CASE WHEN gap_minutes > 60 THEN 0 ELSE step + 1 END
    )
    INIT (0, 0)
    OVER (PARTITION BY device_id ORDER BY timestamp)
    AS (episode_id, step_index)
FROM sensor_readings
```

## Interaction with LET

SCAN expressions may appear as the right-hand side of a LET binding. This allows the output to be referenced multiple times without recomputation:

```sql
SELECT LET ema = SCAN e = 0.1 * price + 0.9 * e
                   INIT price
                   OVER (ORDER BY date)
                   AS _ema,
  date, price,
  ema AS ema_10,
  price - ema AS deviation
FROM stock_prices
```

## Evaluation semantics

- **First row**: The body expression is evaluated on every row, including the first. On the first row the accumulator holds the INIT value.
- **NULL propagation**: If the body evaluates to NULL, the accumulator becomes NULL for subsequent rows unless the body handles it (e.g. via `COALESCE`).
- **Ordering**: Two SCAN expressions in the same SELECT are independent — they cannot reference each other's accumulators. Each is evaluated left-to-right.
- **Sort sharing**: When a SCAN and a window function share the same OVER specification, the sort and partition pass is shared.

## Pipeline position

SCAN runs after window functions and before QUALIFY:

```
Window → SCAN → QUALIFY → SELECT (LET)
```

SCAN output columns are available to QUALIFY, LET bindings, ASSERT, and subsequent ORDER BY.

## When to use SCAN vs. window functions

For simple running aggregates (SUM, COUNT, MAX) already expressible as `OVER` window functions, use the window form. SCAN is specifically for patterns where the output of row N is a direct input to row N+1.

| Pattern | SCAN | Window function |
|---|---|---|
| Running sum | Use window SUM instead | SUM(x) OVER (...) |
| EMA | SCAN | Not expressible |
| Sessionization | SCAN | Not expressible |
| Streak detection | SCAN | Not expressible |
| State machines | SCAN | Not expressible |
| Online Welford | SCAN (tuple) | Not expressible |

## Gotchas

- If the body evaluates to NULL, the accumulator stays NULL for all subsequent rows unless you handle it with COALESCE.
- Two SCAN expressions in the same SELECT are independent — they can't reference each other's accumulators.
- PREV() is only available inside SCAN body expressions — using it elsewhere is an error.
- SCAN requires ORDER BY in the OVER clause — without it, "previous row" is undefined.

## See Also

- [Window Functions](window-functions.md)
- [LET Bindings](let-bindings.md)
- [QUALIFY](qualify.md)
