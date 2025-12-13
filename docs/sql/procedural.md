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

The initializer can also be a parenthesised scalar subquery — useful for
preamble values like row counts and aggregates. The same form works in
`SET`:

```sql
DECLARE @count INT64 = (SELECT count(*) FROM orders)
DECLARE @threshold INT64 = (SELECT max(score) FROM events) + 100

SET @recent = (SELECT count(*) FROM orders WHERE ts > @cutoff)
```

The subquery must produce exactly one row; zero rows yield `NULL`,
multiple rows raise an error. References to enclosing `@vars` resolve
inside the subquery exactly as they do at top level.

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

## PRINT

Emits a diagnostic string to the batch's event stream — distinct from
`SELECT` so callers can route procedural tracing (progress markers,
intermediate values, "what's happening" chatter) to a separate channel
without confusing it with user-facing query rows.

```sql
PRINT 'starting batch'

DECLARE @cohort_size INT64 = (SELECT count(*) FROM cohort)
PRINT @cohort_size

FOR @i = 1 TO 5 BEGIN
  PRINT 'iteration ' || cast(@i AS STRING)
  -- ... work ...
END
```

The expression is evaluated against the current variable scope and
rendered to a string: numbers use invariant culture, booleans render as
lowercase `true` / `false` (matching SQL convention), strings pass
through unchanged. `NULL` produces a null event payload so consumers can
distinguish a missing value from the literal text `"null"`.

Print events surface as `CellPrintBatchEvent` on the `RunWithEventsAsync`
stream — a debug pane, stderr, or a log can route them anywhere; the
non-streaming `ExecuteAsync` path silently discards them.

## Error Handling (TRY / CATCH / FINALLY)

Procedural exception handling, IF-flavored: each body is a single
statement; pair with `BEGIN ... END` for multi-statement bodies. The
catch's `@err` variable is auto-declared in a fresh frame and bound to
the exception's message — visible only inside the catch body.

```sql
TRY
  EXEC models.flaky_llm(@prompt)
CATCH @err
  PRINT 'model call failed: ' || @err
  SET @result = 'fallback'
FINALLY
  SET @attempt_count = @attempt_count + 1
```

`FINALLY` is optional. When present, it runs unconditionally after
TRY/CATCH — on success, on caught error, on `BREAK` / `CONTINUE` exiting
the try, and on cancellation. A throw from `FINALLY` supersedes any
pending exception (matches C# / Java semantics).

`BREAK` and `CONTINUE` are not caught by `CATCH` — they pass through to
their enclosing loop, running `FINALLY` on the way. Cancellation behaves
the same way. Recursion-depth and other procedural runtime errors *are*
catchable; downstream code can treat them as fallback paths.

```sql
FOR @row IN (SELECT prompt FROM queue) BEGIN
  TRY BEGIN
    DECLARE @reply STRING = (SELECT models.gpt_4(@row['prompt']))
    INSERT INTO replies (prompt, reply) VALUES (@row['prompt'], @reply)
  END
  CATCH @err
    PRINT 'skipping prompt: ' || @err
  FINALLY
    SET @processed = @processed + 1
END
```

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
to surface accidentally infinite predicates.

### BREAK / CONTINUE

`BREAK` exits the innermost enclosing `WHILE` / `FOR @i = ... TO ...` /
`FOR @row IN (...)` loop immediately. `CONTINUE` skips the rest of the
current iteration; the predicate (or counter / row source) advances
normally.

```sql
DECLARE @i INT32 = 0
DECLARE @sum INT32 = 0

WHILE @i < 10 BEGIN
  SET @i = @i + 1
  IF @i % 2 = 0 CONTINUE     -- skip even values
  IF @i > 7 BREAK            -- stop once @i passes 7
  SET @sum = @sum + @i        -- accumulates 1 + 3 + 5 + 7 = 16
END
```

Both keywords break only the innermost loop; an outer loop continues
unchanged. Using `BREAK` or `CONTINUE` outside any loop (at batch top
level or directly inside an `IF` not nested within a loop) raises an
error.

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

## CREATE PROCEDURE

A *procedure* is a named, parameterised procedural batch — the multi-
statement equivalent of a UDF. Bodies live in the catalog and survive
process restarts when the catalog is opened with a path (same persistence
contract as UDFs). Invocation is `EXEC proc.<name>(args)`.

```sql
CREATE PROCEDURE compute_cohort(@threshold FLOAT64) AS BEGIN
    DECLARE @kept INT64 = 0
    DECLARE @sum_score FLOAT64 = 0.0

    FOR @row IN (SELECT id, score FROM model_outputs ORDER BY id) BEGIN
      IF @row['score'] > @threshold BEGIN
        SET @kept = @kept + 1
        SET @sum_score = @sum_score + @row['score']
      END
    END

    SELECT @kept AS rows_kept, @sum_score / @kept AS mean_score
END

EXEC proc.compute_cohort(0.5)
```

### Syntax

```sql
CREATE [OR REPLACE | OR ALTER] PROCEDURE [IF NOT EXISTS] name(
    @param1 TYPE [IS NOT NULL] [, @param2 TYPE [IS NOT NULL] ...]
) AS BEGIN
    ...statements...
END;

DROP PROCEDURE [IF EXISTS] name;

EXEC proc.name(arg1, arg2);
```

`OR REPLACE` (PostgreSQL convention) and `OR ALTER` (T-SQL convention)
are accepted as synonyms — both overwrite an existing procedure with the
same name.

The body is required to be a `BEGIN ... END` block — a procedure that
collapses to a single statement is a UDF, not a procedure.

### How procedures differ from UDFs

| | UDF | Procedure |
| --- | --- | --- |
| Body shape | Scalar expression | `BEGIN ... END` block |
| Plan-time treatment | Inlined into call site | **Not inlined.** Invocation resolves the descriptor at runtime, runs the body in a fresh batch context. |
| Returns | A single scalar value | Whatever rows the body's `SELECT`s produce |
| Call site | `udf.X(...)` (anywhere a scalar is valid) | `EXEC proc.X(...)` (statement only) |
| Persistence | Body formatted from AST (whitespace canonicalised) | **Original source text** stored verbatim |
| Variable scope | Sees call-site columns as bare identifiers | Isolated — parameters are the only bridge from the caller |

### Parameter binding and scoping

Parameters use the same `@`-prefix declaration as UDFs and procedural
variables, and `IS NOT NULL` works the same way: the call site evaluates
the argument expression, the runtime checks for null when declared, then
declares `@param` in the procedure's root frame.

```sql
CREATE PROCEDURE need_name(@name STRING IS NOT NULL) AS BEGIN
    SELECT upper(@name)
END

EXEC proc.need_name('alice')   -- yields 'ALICE'
EXEC proc.need_name(NULL)
-- error: Procedure 'proc.need_name' parameter '@name' must not be null.
```

**Each invocation gets its own scope.** Procedures don't share variable
state with the caller — parameters carry values across the boundary,
and that's the only path. A procedure's `SET @v = ...` doesn't leak
into the caller's `@v` even if they have the same name:

```sql
CREATE PROCEDURE shadow(@v INT64) AS BEGIN
    SET @v = 999            -- mutates the procedure's local @v only
END

DECLARE @counter INT64 = 5
EXEC proc.shadow(@counter)
SELECT @counter             -- still 5; the procedure can't see or
                            -- modify the caller's @counter
```

Argument expressions evaluate in the *caller's* scope, so they can
reference the caller's `@vars` and the call-site columns. The result
is then stabilised across the boundary into the procedure's variable
store before the parameter is declared.

### Output rows

A procedure body's `SELECT` statements produce rows that flow up to the
caller's event stream — the cell-numbering machinery treats a
procedure's statements as if they were inlined at the call site. So a
procedure that does:

```sql
CREATE PROCEDURE summarize() AS BEGIN
    SELECT 'rows so far'
    SELECT count(*) FROM data
END

EXEC proc.summarize()
```

produces two cells in the host (terminal table or DevWeb pane), just as
if the user had written the two `SELECT`s inline.

### Introspection — `system_procedures`

The `system_procedures` virtual table surfaces every registered
procedure as queryable rows:

```sql
SELECT name, parameter_count, parameters, source_text
FROM system_procedures
ORDER BY name
```

Schema:

| Column            | Type   | Nullable | Description                                                              |
|-------------------|--------|----------|--------------------------------------------------------------------------|
| `name`            | String | no       | Unqualified procedure name. Call sites use the `proc.` prefix.           |
| `parameter_count` | Int32  | no       | Number of declared parameters. `0` for nullary procedures.               |
| `parameters`      | String | no       | Comma-separated `"@name TYPE [IS NOT NULL], @name TYPE"` rendition.      |
| `source_text`     | String | no       | Original CREATE PROCEDURE source as registered. Whitespace preserved.    |

### Persistence

Procedures persist exactly like UDFs: when the `TableCatalog` is opened
with a catalog file path, registered procedures are written to the same
JSON file atomically and rehydrated on the next session. The persisted
form stores the original `CREATE PROCEDURE` source text verbatim, so
formatting and comments survive a round-trip. See
[User-Defined Functions — Persistence](udf.md#persistence) for the file
layout.

### Limitations

- **Bodies cannot reference parameters across subqueries.** A `SELECT
  ... WHERE col = @param` inside the body's `FOR @row IN (...)` source
  query has its `@param` reference resolved against the procedure's
  variable scope at runtime — same as elsewhere. (This is the same
  rule that applies to `@vars` in regular procedural batches.)
- **Recursion is capped at 32 nested calls.** A procedure that `EXEC`s
  itself (directly or transitively) raises a clear error once the call
  depth exceeds the limit, instead of silently overflowing the .NET
  call stack. Procedural recursion is intentionally not a supported
  pattern — rewrite as a recursive CTE or an iterative loop.
- **Nested routine DDL is rejected at registration time.** A procedure
  body cannot contain `CREATE FUNCTION`, `CREATE PROCEDURE`,
  `DROP FUNCTION`, or `DROP PROCEDURE`. DML and table DDL (`CREATE
  TEMP TABLE`, `INSERT`, `UPDATE`, `DELETE`) are allowed.

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
