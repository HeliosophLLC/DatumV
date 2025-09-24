# SCAN Expression — Design Plan

> **Status**: Under consideration. Prerequisite: tuple destructuring in LET (unlocks multi-valued accumulator state).

---

## Motivation

Window functions see **source data** from other rows but never their own output from the previous row. That single constraint rules out an enormous class of computations:

- Sessionization: a session ID depends on whether it should increment, which depends on its own current value.
- Exponential moving averages: every past value influences the current one with exponential decay.
- State machines: the next state depends on the current state, not the raw input.
- Online normalization: population statistics change with every new row.

The workaround is recursive CTEs, but recursive CTEs require O(N) iterations each joined back to the base table — they are semantically correct and practically unusable on large datasets. Python `.expanding().apply()` with mutable accumulator dictionaries works but exits the SQL pipeline entirely.

SCAN adds a controlled feedback loop to the existing window expression machinery:

```
output[i] = f(output[i-1], input[i])
```

This is a **fold** (also called a prefix scan/running reduce) over an ordered partition, where the fold function is an arbitrary SQL expression that may reference the accumulator by name.

---

## Syntax

### Scalar accumulator

```sql
SCAN accumulator_name = expression
  INIT initial_value
  OVER (PARTITION BY ... ORDER BY ...)
  AS alias
```

- `accumulator_name` — the name of the feedback variable available inside `expression`. On the first row of each partition it equals `initial_value`; on every subsequent row it equals the output of `expression` for the previous row.
- `expression` — any scalar SQL expression that may reference `accumulator_name`, the current row's columns, LET bindings, and `PREV(col)` for the previous row's source column.
- `INIT initial_value` — the seed value for the accumulator at partition start.
- `OVER (...)` — reuses the existing window clause parser. `PARTITION BY` is optional (defaults to the full ordered dataset). `ORDER BY` is required.
- `AS alias` — the output column name.

### Multi-valued accumulator (requires tuple destructuring)

```sql
SCAN (name1, name2, ...) = (expr1, expr2, ...)
  INIT (value1, value2, ...)
  OVER (PARTITION BY ... ORDER BY ...)
  AS (alias1, alias2, ...)
```

Each name in the left-hand tuple is accessible inside the right-hand expressions. The result tuple is bound atomically before the next row — there is no interleaving between components.

### PREV pseudo-function

Inside a SCAN expression (and only there), `PREV(column)` returns the value of `column` from the source row immediately before the current one in partition order. On the first row of a partition, `PREV(col)` returns `NULL`. This is distinct from the accumulator: `PREV` sees the *input*, the accumulator name sees the *output from the previous row*.

---

## Execution Model

SCAN is a physical operator (`ScanOperator`) that sits between the existing `WindowOperator` and `ProjectOperator` in the plan.

1. The planner detects SCAN expressions in the SELECT list.
2. For each SCAN clause, it extracts the OVER specification and schedules the necessary `ORDER BY`/`PARTITION BY` sort (sharing with any window operators that use an identical spec).
3. The `ScanOperator` buffers the sorted rows per partition, iterates them in order, maintains the accumulator values in an `ExpressionEvaluatorContext` slot, evaluates the fold expression on each row, and emits the row augmented with the SCAN output column.
4. The accumulator is **not visible outside the SCAN expression** — it does not appear in the schema, cannot be referenced by other SELECT columns directly. Only the aliased output column is visible. Use LET to cache and reuse the output: `LET ema = SCAN ema = 0.1 * price + 0.9 * ema INIT price OVER (ORDER BY date) AS _ema`.

### Parallelism constraint

SCAN is inherently sequential within a partition. Rows must be processed one at a time in order, because each row's output is an input to the next. This precludes intra-partition parallelism, same as `WITH RECURSIVE`. Inter-partition parallelism is unaffected — each partition can run on a separate thread.

### Interaction with LET

SCAN expressions may appear as the right-hand side of a LET binding. This allows the output to be referenced multiple times without recomputation:

```sql
SELECT
  LET session = SCAN s = CASE WHEN gap > 30 THEN s + 1 ELSE s END
                  INIT 0
                  OVER (PARTITION BY user_id ORDER BY timestamp)
                  AS session_id,
  user_id,
  timestamp,
  session AS session_id,
  session * 1000 + ROW_NUMBER() OVER (PARTITION BY user_id, session ORDER BY timestamp) AS global_event_id
FROM clicks
```

---

## Implementation Sketch

### Parser (`SqlParser.cs`)

New production rule in the SELECT expression grammar:

```
ScanExpression ::=
  'SCAN' Identifier '=' Expression
  'INIT' Expression
  'OVER' '(' WindowSpec ')'
  'AS' Identifier

  | 'SCAN' '(' Identifier (',' Identifier)+ ')' '=' '(' Expression (',' Expression)+ ')'
    'INIT' '(' Expression (',' Expression)+ ')'
    'OVER' '(' WindowSpec ')'
    'AS' '(' Identifier (',' Identifier)+ ')'
```

New AST node: `ScanExpression` with fields `AccumulatorNames`, `BodyExpressions`, `InitExpressions`, `WindowSpec`, `OutputAliases`.

### Planner (`QueryPlanner.cs`)

Detect `ScanExpression` nodes in the SELECT list. For each unique OVER spec, emit a sort node if one is not already present from a co-located window function. Emit a `ScanOperator` after sorting, wrapping the upstream plan.

### Evaluator

Register accumulator names in a new `ScanAccumulatorContext` in `ExpressionEvaluator`. On each row evaluation, bind accumulator names to their current values before evaluating body expressions, then update the context with the new values. `PREV(col)` resolves to a `PrevRowContext` slot populated by the operator.

### New operator: `ScanOperator`

```
ScanOperator(source, partitionByExpressions, orderByExpressions, scanClauses)
  For each partition (grouped by partitionByExpressions, sorted by orderByExpressions):
    Initialize accumulator values from INIT expressions
    For each row in partition order:
      Bind accumulators to ExpressionEvaluator context
      Bind PREV() slots to previous source row
      Evaluate body expressions → new accumulator values
      Emit row with output columns appended
```

---

## Use Cases

### 1. Sessionization (clickstream)

A new session starts when the gap since the last event exceeds 30 minutes. The session ID must reference itself — it increments when the condition is met.

```sql
SELECT
  user_id,
  timestamp,
  SCAN s = CASE
      WHEN date_diff('minute', PREV(timestamp), timestamp) > 30 THEN s + 1
      ELSE s
    END
    INIT 0
    OVER (PARTITION BY user_id ORDER BY timestamp)
    AS session_id
FROM clicks
```

**Without SCAN**: Recursive CTE joins back to the base table per row — O(N) iterations, O(N²) work.

---

### 2. Exponential Moving Average (EMA)

Infinite memory — every past value influences the current one with exponential decay. No finite window frame can express this.

```sql
SELECT
  date,
  price,
  SCAN ema = 0.1 * price + 0.9 * ema
    INIT price
    OVER (ORDER BY date)
    AS ema_10
FROM stock_prices
```

The `INIT price` seeds the accumulator with the first observed price so the EMA starts in range rather than at zero.

---

### 3. Streak Detection

The current winning streak resets to zero on a loss. The streak counter must reference its previous value.

```sql
SELECT
  player_id,
  game_date,
  SCAN streak = CASE WHEN won = 1 THEN streak + 1 ELSE 0 END
    INIT 0
    OVER (PARTITION BY player_id ORDER BY game_date)
    AS current_streak
FROM games
```

To get the *longest* streak, wrap in a window MAX:

```sql
SELECT
  player_id,
  MAX(current_streak) OVER (PARTITION BY player_id) AS longest_streak
FROM (
  SELECT player_id, game_date,
    SCAN streak = CASE WHEN won = 1 THEN streak + 1 ELSE 0 END
      INIT 0
      OVER (PARTITION BY player_id ORDER BY game_date)
      AS current_streak
  FROM games
)
```

**Without SCAN**: Three nested CTEs using gap-and-island grouping.

---

### 4. Time-Decayed Feature Accumulation

Weight recent transactions more than old ones. `SUM() OVER` treats all rows equally regardless of time distance. This produces a proper recency-weighted feature for tabular ML.

```sql
SELECT
  user_id,
  timestamp,
  SCAN decayed = decayed * 0.95 + purchase_amount
    INIT 0
    OVER (PARTITION BY user_id ORDER BY timestamp)
    AS recency_weighted_spend
FROM transactions
```

Each transaction contributes its full amount and then decays at 5% per step. The decay rate can be parameterized: replace `0.95` with `$decay_rate`.

---

### 5. State Machine for Label Generation (Churn Modeling)

A user "churns" after 14 days of inactivity and "reactivates" on their next event. The label depends on the previous label — a textbook state machine over a sorted event log.

```sql
SELECT
  user_id,
  event_date,
  SCAN state = CASE
      WHEN date_diff('day', PREV(event_date), event_date) > 14 THEN 'reactivated'
      WHEN state = 'reactivated' THEN 'active'
      ELSE 'active'
    END
    INIT 'active'
    OVER (PARTITION BY user_id ORDER BY event_date)
    AS user_state
FROM events
```

This generates training labels directly from raw event logs — an entire labeling pipeline in a single query. The two-step `'reactivated' → 'active'` transition captures the one-event window where a user is known to have returned.

---

### 6. Sequence Numbering Within Episodes (for LSTMs / Transformers)

Segment a time series into episodes based on inactivity gaps, then number each step within the episode. Requires **tuple destructuring** to maintain two accumulators in lock-step.

```sql
SELECT
  device_id,
  timestamp,
  SCAN (episode, step) = CASE
      WHEN gap_minutes > 60 THEN (episode + 1, 0)
      ELSE (episode, step + 1)
    END
    INIT (0, 0)
    OVER (PARTITION BY device_id ORDER BY timestamp)
    AS (episode_id, step_index)
FROM (
  SELECT *,
    date_diff('minute', PREV(timestamp), timestamp) AS gap_minutes
  FROM sensor_readings
)
```

`episode_id` becomes a partition key for sequence model training; `step_index` is the position fed into positional encodings. Together they replace a Python loop that assigns sequence IDs.

---

### 7. Running Normalization with Warm-Up (Welford's Algorithm)

Online z-score computation where the mean and variance update with every row. Critically, only past data is used — no future leakage. Uses **Welford's numerically stable online algorithm**.

```sql
SELECT
  timestamp,
  value,
  SCAN (n, mean, m2) = (
      n + 1,
      mean + (value - mean) / (n + 1),
      m2 + (value - mean) * (value - (mean + (value - mean) / (n + 1)))
    )
    INIT (0, 0.0, 0.0)
    OVER (PARTITION BY sensor_id ORDER BY timestamp)
    AS (count, running_mean, running_m2),
  CASE WHEN count > 1
    THEN (value - running_mean) / sqrt(running_m2 / (count - 1))
    ELSE 0
  END AS online_zscore
FROM readings
```

The derived `online_zscore` column references the SCAN output aliases (`count`, `running_mean`, `running_m2`) directly. This is one pass, numerically stable, and produces the same result as StandardScaler fitted only on past data — a strict prerequisite for non-leaking feature pipelines.

---

### 8. Carry-Forward Imputation with Expiry

Forward-fill a missing sensor reading, but only up to 5 minutes after the last real observation. After that, emit NULL — stale data is worse than missing data in most ML contexts.

```sql
SELECT
  timestamp,
  raw_temperature,
  SCAN (filled, last_real_ts) = CASE
      WHEN raw_temperature IS NOT NULL
        THEN (raw_temperature, timestamp)
      WHEN date_diff('minute', last_real_ts, timestamp) <= 5
        THEN (filled, last_real_ts)
      ELSE (NULL, last_real_ts)
    END
    INIT (NULL, NULL)
    OVER (PARTITION BY sensor_id ORDER BY timestamp)
    AS (temperature, _last_ts)
FROM sensor_data
```

`pandas.ffill(limit=N)` limits by row count, not wall-clock time. This variant limits by elapsed time — the semantically correct threshold for sensor data with irregular sampling frequencies.

---

### 9. Click-Through Attribution (Last-Touch, 7-Day Lookback)

Credit the most recent ad impression before a conversion, but only if it falls within the last 7 days. Carries forward a **struct** as accumulator state.

```sql
SELECT
  user_id,
  event_type,
  timestamp,
  campaign_id,
  SCAN last_impression = CASE
      WHEN event_type = 'impression'
        THEN {campaign: campaign_id, ts: timestamp}
      ELSE last_impression
    END
    INIT NULL
    OVER (PARTITION BY user_id ORDER BY timestamp)
    AS last_ad,
  CASE
    WHEN event_type = 'conversion'
      AND last_ad IS NOT NULL
      AND date_diff('day', last_ad['ts'], timestamp) <= 7
    THEN last_ad['campaign']
    ELSE NULL
  END AS attributed_campaign
FROM events
```

The struct accumulator (`{campaign, ts}`) carries both fields forward together, avoiding a second SCAN or a self-join. Currently this pattern requires custom attribution code or a specialized tool. With SCAN + struct literals, it is a readable single-pass query.

---

## Comparison with Alternatives

| Pattern | SCAN | Recursive CTE | Python `.expanding().apply()` | Window function |
|---|---|---|---|---|
| EMA | ✅ One expression | ❌ O(N) iterations | ✅ But exits SQL | ❌ Infinite history |
| Sessionization | ✅ | ❌ Unusable at scale | ✅ But exits SQL | ❌ Can't self-reference |
| Online Welford | ✅ | ❌ | ✅ But exits SQL | ❌ |
| Streak | ✅ | ⚠️ Complex 3-CTE | ✅ | ❌ |
| State machines | ✅ | ❌ | ✅ But exits SQL | ❌ |
| Attribution | ✅ | ⚠️ Self-join | ✅ But exits SQL | ❌ |
| Simple running sum | ✅ | ✅ | ✅ | ✅ Use window instead |

For simple running aggregates (SUM, COUNT, MAX) already expressible as `OVER` window functions, SCAN offers no advantage — use the window form. SCAN's domain is specifically patterns where the output of row N is a direct input to row N+1.

---

## Synergies

- **Tuple destructuring in LET** — prerequisite for multi-valued accumulators (use cases 6, 7, 8). Implement tuple destructuring first.
- **Struct type** — already implemented; enables carrying complex state in a single accumulator slot (use case 9).
- **Lambda expressions** — future: user-defined fold functions via `array_reduce`-style higher-order form.
- **ASSERT** — validate sequential invariants inline: `ASSERT timestamp > PREV(timestamp) MESSAGE 'non-monotonic: ' || timestamp ON FAIL SKIP`.
- **LET** — cache SCAN output and reference it in multiple downstream columns and further SCAN expressions without recomputation.
- **SPLIT INTO** — feed SCAN-derived features directly into train/validation splits in one pipeline.

---

## Design Decisions and Open Questions

**Should SCAN appear inside a subquery or only at the top-level SELECT?**
Recommend: allow SCAN anywhere a window function is allowed. Both are partition-ordered expressions that require the same OVER clause infrastructure.

**What happens when SCAN and a window function share the same OVER spec?**
The sort and partition pass can be shared. The `ScanOperator` should accept a pre-sorted input stream and not re-sort. Planner responsibility: detect compatible OVER specs and schedule a single sort node.

**Can two SCAN expressions reference each other's output?**
No — same restriction as LET bindings. Left-to-right sequential evaluation. SCAN A cannot reference SCAN B defined after it in the SELECT list.

**Should PREV(col) be available outside SCAN?**
Potentially useful alongside `LAG(col, 1) OVER (...)`. One difference: `PREV` inside SCAN is always the directly preceding row in the current partition pass, which is cheaper than computing a separate LAG window. Exposing PREV outside SCAN as a shorthand for `LAG(col, 1)` with implicit OVER could be a follow-on.

**Empty partitions / single-row partitions**
A single-row partition emits the INIT value directly (the body expression is never evaluated, since there is no previous row to fold against). This matches the semantics of `INIT` as the zero element.

**NULL handling in the accumulator**
If `expression` evaluates to NULL, the accumulator becomes NULL for all subsequent rows in the partition. Implementations should document this and encourage `COALESCE(expr, accumulator)` patterns to preserve state on NULL inputs (see use case 8's `ELSE (filled, last_real_ts)` branch).
