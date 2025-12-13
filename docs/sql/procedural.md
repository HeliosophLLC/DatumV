---
title: Procedural Statements
---

## Why Use This

DatumIngest treats a script as a *batch* — a sequence of statements executed
in order against a shared procedural scope. Inside a batch you can declare
variables, branch, loop over either a counter or a query result, and pin
intermediate values with `SELECT @var = ...`. The result is that
analytics-style ad-hoc SQL and procedural orchestration share the same
language: instead of dropping out to a host language to glue queries
together, you compose them inside the engine.

```sql
DECLARE @threshold FLOAT64 = 0.85;
DECLARE @kept INT64 = 0;
DECLARE @sum_score FLOAT64 = 0.0;

FOR @row IN (SELECT id, score FROM model_outputs ORDER BY id) BEGIN
  IF @row['score'] > @threshold BEGIN
    SET @kept = @kept + 1
    SET @sum_score = @sum_score + @row['score']
  END
END

SELECT @kept AS rows_kept, @sum_score / @kept AS mean_score
```

A procedural batch is just a list of statements separated by optional
semicolons. The statements covered on this page — `DECLARE`, `SET`,
`BEGIN/END`, `IF`/`ELSE`, `WHILE`, `FOR`, and the assignment form of
`SELECT` — are the procedural primitives.

## Variables

Variables are referenced with a leading `@` and live for the lifetime of
the enclosing batch (or the enclosing block, if declared inside `BEGIN/END`
— see [Block Scoping](#block-scoping)). Names are case-insensitive: `@X`
and `@x` resolve to the same binding.

### DECLARE

Introduces a new variable. The declared type is required; an initializer
is optional.

```sql
DECLARE @count INT32                    -- typed NULL
DECLARE @name STRING = 'alice'          -- bound from literal
DECLARE @sum INT64 = 0                  -- bound and coerced to INT64
DECLARE @ratio FLOAT64 = price / cost   -- bound from expression
```

When both a type and an initializer are present, the initializer is
implicitly cast to the declared type. This means `DECLARE @sum INT64 = 0`
binds `@sum` as `INT64` even though the literal `0` parses as the narrowest
integer kind that fits — the cast prevents arithmetic from accidentally
running in a smaller type.

Declaring a name that's already bound in the same block is an error;
declaring a name that's bound in an *enclosing* block shadows the outer
binding for the lifetime of the inner block.

### SET

Reassigns an existing variable. The variable must be declared in the
current scope or an enclosing one; reassigning an undeclared name is an
error.

```sql
SET @count = @count + 1
SET @name = upper(@name)
```

`SET` walks the scope chain outward and updates the first frame holding
the name. The reassigned value is materialized into the procedure's
variable store, so the binding remains valid even after the producing
expression's per-query arena recycles.

### Multi-variable SELECT assignment

A SELECT whose every column is `@var = expression` runs as a *silent
assignment* — no rows are returned, and each row updates the listed
variables in iteration order.

```sql
DECLARE @a INT64 = 0
DECLARE @b INT64 = 0
DECLARE @max_score FLOAT64 = 0.0

-- Bind multiple variables from the same row.
SELECT @a = 1, @b = 2

-- Pull the last (highest) score from a query into a variable.
SELECT @max_score = score FROM results ORDER BY score
```

The semantics match T-SQL:

- **Zero rows** → all variables remain at their pre-SELECT values.
- **One row** → variables get that row's values.
- **Multiple rows** → variables iterate through every row; the *last*
  row's values are what stick. Use `ORDER BY` to make the outcome
  deterministic.

Mixing assignment columns with regular projection columns in the same
SELECT is rejected:

```sql
SELECT @x = 1, 'hello'   -- error: must be all-or-nothing
```

The disambiguation between assignment and comparison happens at parse
time. `@var = rhs` at the top level of a SELECT column with no alias is
treated as assignment. Anything that breaks that shape — adding an
alias, wrapping in extra structure — falls back to a regular comparison
expression:

| Form | Treated as |
| --- | --- |
| `SELECT @a = 5` | Assignment — `@a` ← 5 |
| `SELECT @a = 5 AS isFive` | Comparison — projects boolean column |
| `SELECT (@a = 5) AS isFive` | Comparison (alias is required) |
| `SELECT @a = a, @b = b FROM t` | Both assignments |

## Block Scoping

`BEGIN` opens a new lexical scope; `END` closes it. Variables declared
inside the block are visible only until the matching `END`; variables
declared in an outer scope remain visible inside the block.

```sql
DECLARE @outer INT32 = 1
BEGIN
  DECLARE @inner INT32 = 2     -- visible only inside this block
  SET @outer = @inner + 10     -- mutates the outer binding
END
-- @inner is gone; @outer is now 11
```

`SET` walks the scope chain outward, so blocks can mutate outer
variables. `DECLARE` always binds in the innermost frame, so an inner
`DECLARE @x` shadows an outer `@x` for the block's lifetime.

Empty blocks (`BEGIN END`) are not supported — at least one statement
is required, matching T-SQL.

## Conditional Branch (IF / ELSE)

Conditional branch on a boolean predicate. The single-statement form
attaches one statement to each branch; pair with `BEGIN/END` to run a
block.

```sql
IF @x > 0
  SET @sign = 1
ELSE IF @x < 0
  SET @sign = -1
ELSE
  SET @sign = 0
```

`ELSE IF` is not a separate keyword — it falls out naturally because the
`ELSE` body can itself be an `IF`. The chain matches the first branch
whose predicate evaluates to true, then runs only that branch's body.

NULL predicates are treated as false (the branch is not taken), matching
T-SQL's three-valued logic.

```sql
IF @row['score'] > 0.5 BEGIN
  SET @count = @count + 1
  SET @sum_kept = @sum_kept + @row['score']
END
```

## WHILE

Repeats the body while the predicate is true. The predicate is
re-evaluated before every iteration; NULL terminates the loop.

```sql
DECLARE @i INT32 = 0
DECLARE @sum INT32 = 0

WHILE @i < 10 BEGIN
  SET @sum = @sum + @i
  SET @i = @i + 1
END
```

Loops have a runtime cap of one million iterations; exceeding it throws
to surface accidentally infinite predicates. There is no `BREAK` /
`CONTINUE` — set the predicate variable to terminate.

## FOR

Two forms: a counter loop and a cursor loop.

### FOR @i = start TO end

Counter loop with inclusive bounds. The loop variable is auto-declared
in a fresh frame for the loop's lifetime, initialised to `start`, and
incremented by 1 on each iteration. When `start > end`, the body never
runs.

```sql
DECLARE @sum INT32 = 0

FOR @i = 1 TO 5 SET @sum = @sum + @i        -- 1+2+3+4+5 = 15
```

Bounds expressions can reference enclosing variables:

```sql
DECLARE @lo INT32 = 2
DECLARE @hi INT32 = @lo * 5

FOR @i = @lo TO @hi BEGIN
  -- ... 2..10 inclusive
END
```

The loop variable is bound as `INT64`; numeric kinds at the bounds are
coerced. Modifying `@i` inside the body has no defined effect — the
counter is internally driven and reset every iteration.

### FOR @row IN (SELECT ...)

Cursor loop: drives the body once per row of a parenthesised query. The
loop variable holds each row as a `STRUCT` whose ordered fields match
the source columns; access fields positionally with `@row[0]` or by
name with `@row['column']`.

```sql
DECLARE @count INT64 = 0
DECLARE @max_score FLOAT64 = 0.0

FOR @row IN (SELECT id, score FROM predictions WHERE score > 0.5) BEGIN
  SET @count = @count + 1
  IF @row['score'] > @max_score
    SET @max_score = @row['score']
END
```

A fresh frame is pushed for each iteration, so any inner `DECLARE` is
scoped to that single pass. Field name lookup uses the source query's
column names; positional access is always available regardless.

The source query runs to completion (or until a body statement throws)
and never streams; for large source queries, the loop iterates rows
batch-by-batch but always finishes the inner work before requesting the
next batch.

## EXEC inside a batch

`EXEC` runs a function call as a top-level statement. Inside a
procedural batch it can reference declared variables in the argument
list:

```sql
DECLARE @prompt STRING = 'summarise the last quarter'

EXEC models.llama_3_8b(@prompt)
```

For LLM models, an `EXEC` cell forwards token chunks live to the host
(terminal or DevWeb pane) as they arrive. See [Models](../models.md) for
streaming details.

## Statement Separators

Statements within a batch are typically separated by `;`, but the
separator is optional — block-terminated statements (anything ending
with `END`) are common boundaries where forcing a trailing `;` reads as
awkward.

```sql
-- Both styles are valid.
DECLARE @x INT32 = 1; SET @x = @x + 1;
DECLARE @x INT32 = 1
SET @x = @x + 1

FOR @row IN (SELECT * FROM t) BEGIN
  SET @count = @count + 1
END
SELECT @count       -- no `;` after END required
```

Each statement parser is keyword-anchored (`DECLARE`, `SET`, `IF`,
`WHILE`, `FOR`, `BEGIN`, `SELECT`, ...), so consecutive statements
without a separator disambiguate cleanly. Empty statements (extra
semicolons) are silently ignored.

## Variable Lifetimes and Storage

Variable payloads (strings, structs, arrays, byte buffers) live in a
*procedure-lifetime arena* that survives every child query's per-call
arena recycle. Reads of `@var` in the middle of a query stabilise the
value into the active query's target arena, so downstream consumers see
the variable the same way they see any other column value. The
procedure-lifetime arena is released when the batch finishes — at which
point all variable bindings become invalid for any host code still
holding references.

For host code that introspects bindings post-run (tests, REPL output),
the engine snapshots variable values into managed types before the
arena releases, so post-batch inspection is safe.

## See Also

- [SELECT](select.md)
- [User-Defined Functions](udf.md)
- [Type System](type-system.md)
- [Models](../models.md)
