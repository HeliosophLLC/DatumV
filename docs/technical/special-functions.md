# Special Functions

Reference for DatumIngest functions and syntax that go beyond standard SQL scalar and aggregate semantics.

## Lambda Expressions

Lambda expressions define inline anonymous functions, used as arguments to higher-order functions.

### Syntax

```
parameter -> body
(parameter) -> body
(param1, param2) -> body
```

The arrow operator is `->`. The body is any scalar expression. Parentheses are optional for a single parameter.

### Closure capture

Lambda bodies can reference columns from the enclosing row. Column values are captured at evaluation time.

```sql
-- 'discount' is a column on the table, not a lambda parameter
SELECT array_transform(prices, p -> p * discount) FROM products
```

Lambda parameter names shadow column names of the same name within the body.

---

## array_transform

Applies a lambda to each element of an array, returning a new array of the transformed values.

```sql
array_transform(array, element -> expression) → Array
```

### Examples

```sql
-- Double every price
SELECT array_transform(prices, p -> p * 2) FROM products

-- Uppercase every tag
SELECT array_transform(tags, t -> upper(t)) FROM articles
```

---

## array_filter

Filters an array, keeping only elements where the lambda predicate returns true.

```sql
array_filter(array, element -> Boolean) → Array
```

### Examples

```sql
-- Keep scores above 50
SELECT array_filter(scores, s -> s > 50) FROM students

-- Keep non-empty strings
SELECT array_filter(names, n -> length(n) > 0) FROM data
```

---

## Array Literal Syntax

Bracket syntax `[a, b, c]` is syntactic sugar for `array(a, b, c)`.

```sql
SELECT [1, 2, 3]                 -- array of numbers
SELECT ['a', 'b', 'c']          -- array of strings
SELECT []                        -- empty array
```

Array literals compose naturally with lambdas and other functions:

```sql
SELECT array_filter([10, 20, 30, 40], x -> x > 25)
-- result: [30, 40]

SELECT array_transform([1, 2, 3], x -> x * x)
-- result: [1, 4, 9]
```

Nested array literals are supported:

```sql
SELECT [[1, 2], [3, 4]]
```

---

## Struct Literal Syntax

Brace syntax `{ field: expr, ... }` constructs a typed struct value with named fields.

```sql
SELECT {name: 'alice', score: 9.5}                  -- two-field struct
SELECT {x: lng, y: lat} FROM waypoints              -- fields from columns
SELECT {}                                            -- empty struct
```

Field names are identifiers; values can be any scalar expression. Types are inferred from each field's expression at plan time. Struct literals can be nested:

```sql
SELECT {point: {x: 1.0, y: 2.0}, radius: 5.0}
```

---

## Index Access (Bracket Operator)

The postfix `[index]` operator accesses array elements by zero-based integer position, or struct fields by name string. Multiple subscripts chain left-to-right.

```sql
-- Array element access (0-based integer index)
SELECT scores[0]           -- first element
SELECT embeddings[127]     -- element at position 127

-- Struct field access (string key)
SELECT record['name']      -- field named 'name' (case-insensitive)
SELECT meta['created_at']  -- field named 'created_at'

-- Chained access
SELECT record['scores'][2]              -- element 2 of a nested array field
SELECT {x: 10, y: 20}['y']             -- inline struct: returns 20
```

Accessing an array index out of bounds or a struct field that does not exist returns null.

---

## LET Bindings

`LET` declares a named intermediate expression inside a SELECT list. The expression is evaluated once per row, cached, and can be referenced by subsequent LET bindings and output columns. LET bindings are not emitted as output columns unless given an `AS alias`.

### Syntax

```sql
SELECT LET name = expression [AS alias], ... columns ... FROM table
```

LET bindings appear before the first output column, separated from it and from each other by commas.

### Examples

```sql
-- Compute once, reference twice
SELECT LET total = price * qty, id, price, qty, total FROM line_items

-- Bind without emitting (no AS alias — not in output)
SELECT LET tax = amount * 0.1, id, amount FROM orders

-- Chain: later bindings may reference earlier ones
SELECT LET subtotal = price * qty,
       LET tax      = subtotal * 0.1,
       subtotal, tax
FROM line_items
```

### Tuple Destructuring

A LET binding can unpack a multi-valued result into several named variables in a single step. Two forms are supported:

#### Positional destructuring

```
LET (name1, name2, ...) = expression
```

Extracts values by **zero-based position**. Works on **Array**, and **Struct** sources.

```sql
-- Unpack a float array into named scalars
SELECT LET (r, g, b) = pixel_array, r, g, b FROM images

-- Unpack cyclical_encode output (returns a 2-element Vector)
SELECT LET (sin_month, cos_month) = cyclical_encode(month, 12),
       sin_month AS s, cos_month AS c
FROM events

-- Unpack a struct by field declaration order
SELECT LET (x, y) = {x: 1.0, y: 2.0}, x, y FROM data
```

#### Named destructuring

```
LET {field1, field2, ...} = expression
```

Extracts values by **field name**. Works on **Struct** sources only. Field order in the pattern does not need to match the struct's field declaration order.

```sql
-- Extract specific fields from a struct literal
SELECT LET {lo, hi} = {lo: 0.0, hi: 1.0}, lo, hi FROM data

-- Extract from a struct column via a scalar LET alias
SELECT LET s = build_struct(row), LET {score, label} = s, score, label FROM predictions

-- Field order is independent of struct declaration order
SELECT LET {beta, alpha} = {alpha: 7.0, beta: 8.0}, alpha, beta FROM data
```

#### Source semantics

| Source kind | Positional | Named |
|-------------|-----------|-------|
| Array       | ✓ zero-based index | ✗ |
| Vector      | ✓ zero-based index | ✗ |
| Struct      | ✓ declaration order | ✓ by field name |

Named destructuring on an Array or Vector is a runtime error — use positional destructuring instead.

#### Memoization

The source expression is evaluated **once per row** regardless of how many names are extracted. This matters when the source has side-effects or is expensive:

```sql
-- cyclical_encode is called once; sin_v and cos_v share the result
SELECT LET (sin_v, cos_v) = cyclical_encode(month, 12),
       sin_v * sin_v + cos_v * cos_v AS unit_check   -- always ≈ 1.0
FROM data
```

#### Chaining

Destructured names are plain LET bindings and can be used in subsequent LET expressions:

```sql
SELECT LET (dx, dy) = displacement,
       LET dist = sqrt(dx * dx + dy * dy),
       dist AS distance
FROM movements
```

### Evaluation order and scope

- LET bindings evaluate left-to-right; each may reference any binding declared before it.
- A binding is in scope for all subsequent LET expressions and for all output columns.
- LET is scoped to a single SELECT — bindings are not visible in subqueries or CTEs.
- LET expressions may reference window functions; the window result is computed before the binding is evaluated.

---

## SCAN Expression

`SCAN` computes a running fold (prefix scan) over ordered partitions. Each row's output feeds back as the accumulator for the next row: `output[i] = f(output[i-1], input[i])`. This enables computations that standard window functions cannot express — patterns where a row's result depends on its own previous output.

### Syntax

#### Scalar accumulator

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

#### Multi-valued accumulator (tuple form)

```sql
SELECT SCAN (name1, name2, ...) = (expr1, expr2, ...)
  INIT (value1, value2, ...)
  OVER (PARTITION BY ... ORDER BY ...)
  AS (alias1, alias2, ...)
FROM table
```

Each name in the left-hand tuple is accessible inside all right-hand expressions. The result tuple is updated atomically before the next row.

### PREV pseudo-function

Inside a SCAN expression body, `PREV(column)` returns the value of `column` from the source row immediately before the current one in partition order. On the first row of a partition, `PREV(col)` returns `NULL`. This is distinct from the accumulator: `PREV` sees the **input**, the accumulator name sees the **output from the previous row**.

`PREV` is only available inside SCAN body expressions. Using it elsewhere produces an "Unknown function" error.

### Examples

#### Exponential Moving Average

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

#### Sessionization

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

#### Streak Detection

The current winning streak resets to zero on a loss.

```sql
SELECT player_id, game_date,
  SCAN streak = CASE WHEN won = 1 THEN streak + 1 ELSE 0 END
    INIT 0
    OVER (PARTITION BY player_id ORDER BY game_date)
    AS current_streak
FROM games
```

#### Episode Numbering (tuple form)

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

### Interaction with LET

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

### Evaluation semantics

- **First row**: The body expression is evaluated on every row, including the first. On the first row the accumulator holds the INIT value.
- **NULL propagation**: If the body evaluates to NULL, the accumulator becomes NULL for subsequent rows unless the body handles it (e.g. via `COALESCE`).
- **Ordering**: Two SCAN expressions in the same SELECT are independent — they cannot reference each other's accumulators. Each is evaluated left-to-right.
- **Sort sharing**: When a SCAN and a window function share the same OVER specification, the sort and partition pass is shared.

### Pipeline position

SCAN runs after window functions and before QUALIFY:

```
Window → SCAN → QUALIFY → SELECT (LET)
```

SCAN output columns are available to QUALIFY, LET bindings, ASSERT, and subsequent ORDER BY.

### When to use SCAN vs. window functions

For simple running aggregates (SUM, COUNT, MAX) already expressible as `OVER` window functions, use the window form. SCAN is specifically for patterns where the output of row N is a direct input to row N+1.

| Pattern | SCAN | Window function |
|---|---|---|
| Running sum | Use window SUM instead | SUM(x) OVER (...) |
| EMA | SCAN | Not expressible |
| Sessionization | SCAN | Not expressible |
| Streak detection | SCAN | Not expressible |
| State machines | SCAN | Not expressible |
| Online Welford | SCAN (tuple) | Not expressible |

---

## ASSERT Clause

`ASSERT` validates a predicate against every projected (post-LET) row. It runs after SELECT projection, so it can reference both source columns and LET bindings. Multiple ASSERT clauses may appear after the column list; they are evaluated left-to-right.

### Syntax

```sql
SELECT columns FROM table
ASSERT predicate [MESSAGE expression] [ON FAIL ABORT | SKIP | WARN]
ASSERT ...
```

### Failure modes

| Mode | Behaviour |
|------|-----------|
| `ABORT` (default) | Throws immediately. No further rows are produced. |
| `SKIP` | Omits the failing row from the output silently. |
| `WARN` | Keeps the row and records a diagnostic (accessible via `AssertionDiagnostics`). |

### MESSAGE

`MESSAGE` accepts any scalar expression evaluated against the projected row and produces a human-readable failure description. It may reference LET bindings.

### Examples

```sql
-- Abort on any negative amount
SELECT id, amount FROM orders
ASSERT amount > 0

-- Skip bad rows and attach a dynamic message
SELECT id, amount FROM orders
ASSERT amount > 0
    MESSAGE CONCAT('order ', CAST(id AS VARCHAR), ' has non-positive amount')
    ON FAIL SKIP

-- Reference a LET binding in the predicate
SELECT LET total = price * qty, id, total FROM line_items
ASSERT total >= 0 MESSAGE 'negative total' ON FAIL WARN
```

### Pipeline position

ASSERT runs after projection and before DISTINCT/ORDER BY/LIMIT:

```
FROM → JOIN → WHERE → GROUP BY → HAVING → Window → SCAN → QUALIFY → SELECT (LET) → ASSERT → DISTINCT → ORDER BY → LIMIT
```

---

## DEFINE Block

`DEFINE` groups LET bindings and ASSERT clauses inside a brace-delimited block placed immediately after `SELECT`. It is purely syntactic sugar — at parse time the block is flattened into the query's LET bindings and ASSERT list. All LET bindings evaluate before any ASSERT regardless of declaration order within the block.

### Syntax

```sql
SELECT DEFINE {
    LET name = expression [AS alias];
    ASSERT predicate [MESSAGE expression] [ON FAIL ABORT | SKIP | WARN];
} columns
FROM table
```

Declarations are separated by semicolons; a trailing semicolon before `}` is optional. LET and ASSERT may appear in any order inside the block.

### Equivalence

These two queries are identical:

```sql
-- DEFINE form
SELECT DEFINE {
    LET total = price * qty;
    ASSERT total >= 0 ON FAIL SKIP;
} total
FROM line_items

-- Inline equivalent
SELECT LET total = price * qty, total
FROM line_items
ASSERT total >= 0 ON FAIL SKIP
```

### Combining with trailing ASSERTs

ASSERT clauses from the DEFINE block and trailing ASSERT clauses after the column list are all collected and evaluated together, with block-sourced assertions applied first.

```sql
SELECT DEFINE {
    LET tax = amount * 0.1;
    ASSERT amount > 0 ON FAIL SKIP;
} id, amount, tax
FROM orders
ASSERT tax < 1000 ON FAIL WARN    -- evaluated after the DEFINE block's assertion
```

### Destructuring inside DEFINE

Tuple destructuring bindings work inside DEFINE blocks alongside ASSERT clauses:

```sql
-- Unpack and validate a Vector result in one block
SELECT DEFINE {
    LET (sin_m, cos_m) = cyclical_encode(month, 12);
    ASSERT sin_m BETWEEN -1.0 AND 1.0 ON FAIL WARN;
} sin_m AS s, cos_m AS c
FROM events

-- Named destructuring with guard
SELECT DEFINE {
    LET {lo, hi} = bounds;
    ASSERT hi > lo MESSAGE 'inverted range' ON FAIL SKIP;
} lo, hi
FROM ranges
```

### Constraints

- At most one DEFINE block per SELECT.
- Cannot be combined with inline LET bindings in the same SELECT.

---

## TABLESAMPLE — Class-Balanced Sampling

TABLESAMPLE limits the rows returned from a table source to an approximate sample. Beyond standard BERNOULLI and SYSTEM methods, DatumIngest adds STRATIFIED and BALANCED for ML dataset preparation.

### Syntax

```sql
FROM table TABLESAMPLE method(argument) [ON column | ON (col1, col2)] [REPEATABLE(seed)]
```

### Methods

#### BERNOULLI / SYSTEM (standard)

Row-level probabilistic sampling. Each row is independently included with probability `percentage / 100`:

```sql
SELECT * FROM data TABLESAMPLE BERNOULLI(10)              -- ~10% of rows
SELECT * FROM data TABLESAMPLE BERNOULLI(10) REPEATABLE(42) -- deterministic
```

#### STRATIFIED (proportional per-class sampling)

Samples each class at the same rate, preserving the original class distribution. Requires an `ON` clause specifying the stratification column.

```sql
-- 10% of each class: if 5000 cats and 1000 dogs, returns ~500 cats and ~100 dogs
SELECT * FROM training_data
TABLESAMPLE STRATIFIED(10) ON label REPEATABLE(42)
```

**Algorithm:** Uniform Bernoulli at the given rate — each row is independently included with probability `percentage / 100`, regardless of its class. Because the rate is the same for all classes, proportions are maintained in expectation. Single-pass streaming with no buffering.

**Output:** Approximate row count. Class proportions preserved in expectation.

#### BALANCED (fixed-count per-class sampling)

Returns exactly `count` rows from each distinct class via reservoir sampling (Algorithm R). Equalizes class representation regardless of the original distribution.

```sql
-- Exactly 500 rows per class: returns 500 cats and 500 dogs
SELECT * FROM training_data
TABLESAMPLE BALANCED(500) ON label REPEATABLE(42)

-- Composite stratification key
SELECT * FROM training_data
TABLESAMPLE BALANCED(500) ON (label, split)
```

**Algorithm:** Single-pass per-class reservoir sampling. Maintains one reservoir of capacity `count` per distinct class. Each row is evaluated against its class's reservoir using Algorithm R. After all input is consumed, reservoirs are emitted in class-first order (all rows from the first-seen class, then the second, etc.).

**Output:** Exact row count per class (or fewer if a class has fewer rows than the target — no oversampling). Rows within each class are in reservoir-random order.

**Memory:** Buffers up to `count × C` rows, where `C` is the number of distinct classes. A configurable maximum class count (default 10,000) prevents unbounded memory growth.

### REPEATABLE (deterministic seeding)

`REPEATABLE(seed)` makes any sampling method deterministic. The same seed on the same data always produces the same sample:

```sql
SELECT * FROM data TABLESAMPLE BALANCED(100) ON label REPEATABLE(42)
```

### Interaction with WHERE

TABLESAMPLE applies at the scan level. Pushed-down WHERE predicates filter rows **before** sampling:

```sql
-- Sample from the filtered population (only valid rows)
SELECT * FROM data TABLESAMPLE STRATIFIED(10) ON label
WHERE is_valid = 1
```

### Combining with other clauses

TABLESAMPLE composes naturally with all query clauses:

```sql
-- Balanced sample → sort → limit → output
SELECT * FROM training_data
TABLESAMPLE BALANCED(1000) ON label REPEATABLE(42)
ORDER BY label, id
LIMIT 5000
INTO 'balanced_train.parquet'
```

### When to use STRATIFIED vs. BALANCED

| Goal | Method | Example |
|------|--------|---------|
| Reduce dataset size while preserving class ratios | STRATIFIED | `STRATIFIED(10) ON label` — 10% of each class |
| Equalize underrepresented classes for training | BALANCED | `BALANCED(1000) ON label` — exactly 1000 per class |
| Quick exploratory sample | BERNOULLI | `BERNOULLI(1)` — ~1% random sample |

---

## CROSS VALIDATE — k-Fold Cross-Validation

CROSS VALIDATE assigns each row a deterministic fold index in `[0, k)` for k-fold cross-validation. It desugars to a synthetic LET binding at plan time — no new operator, no buffering, zero query units.

### Syntax

```sql
SELECT *, fold
FROM table
CROSS VALIDATE(k = N [, seed = S]) ON key_column [STRATIFY BY col] [GROUP BY col] AS alias
```

- `k` — number of folds (integer >= 2, required)
- `seed` — deterministic seed (default 0)
- `ON key` — hash key column(s); composite: `ON (col1, col2)`
- `STRATIFY BY col` — balanced class distribution per fold
- `GROUP BY col` — all rows with same group key get the same fold
- `AS alias` — output column name (required)

### Fold assignment algorithm

```
fold = CAST(FLOOR(hash_split(key, seed) * k) AS Int32)
```

Uses XxHash64 via `hash_split` for deterministic, uniform distribution. Same key + seed + k = same fold, always, across runs and machines.

### Examples

```sql
-- 5-fold CV
SELECT *, fold FROM data CROSS VALIDATE(k = 5, seed = 42) ON id AS fold

-- Stratified: balanced class distribution per fold
SELECT *, fold FROM data CROSS VALIDATE(k = 5, seed = 42) ON id STRATIFY BY label AS fold

-- Group: prevent data leakage (all patient visits in same fold)
SELECT *, fold FROM visits CROSS VALIDATE(k = 5) ON patient_id GROUP BY patient_id AS fold

-- Nested CV: outer for evaluation, inner for tuning
SELECT *, outer_fold, inner_fold FROM data
CROSS VALIDATE(k = 5, seed = 1) ON id AS outer_fold
CROSS VALIDATE(k = 3, seed = 2) ON id AS inner_fold
```

### Composition with TABLESAMPLE BALANCED

Balance classes first, then assign folds — the common ML training pipeline:

```sql
SELECT fold, label, COUNT(*) AS cnt
FROM training_data
TABLESAMPLE BALANCED(500) ON label REPEATABLE(42)
CROSS VALIDATE(k = 5, seed = 7) ON id AS fold
GROUP BY fold, label
ORDER BY fold, label
```

### When to use CROSS VALIDATE vs. manual hash_split

| Approach | Declarative | Deterministic | Stratified | Group-aware |
|----------|:-----------:|:-------------:|:----------:|:-----------:|
| `CROSS VALIDATE` | Yes | Yes | Yes | Yes |
| Manual `FLOOR(hash_split(...) * k)` | No (boilerplate) | Yes | Manual | Manual |

CROSS VALIDATE is syntactic sugar over `hash_split` — use it for the common case. Use `hash_split` directly for custom splitting logic (time-based splits, non-uniform fractions, etc.).
