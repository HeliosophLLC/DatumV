---
title: User-Defined Functions
---

User-defined functions (UDFs) allow you to name a reusable computation and
call it through the `udf.` namespace. DatumIngest supports two UDF body
shapes with different trade-offs:

- **Macro UDFs** (`AS expression`) — the body is a scalar expression inlined
  at plan time. The call site sees the substituted body; no UDF boundary
  remains at execution. Nondeterministic primitives (`random`,
  `models.*`) re-evaluate per reference, giving each call site its own
  independent roll.
- **Procedural UDFs** (`BEGIN … END`) — the body is a statement sequence
  executed at runtime by a per-call adapter. `DECLARE` variables are
  evaluated once per invocation and reused wherever they appear in the body.
  Use this shape when a body must compute an intermediate value once and
  reference it multiple times — for example, a random draw that seeds an
  expression and must be the same everywhere it appears in that expression.

The two shapes coexist in the same catalog and are called identically from SQL.
`CREATE OR REPLACE` can swap a name between the two shapes.

## Syntax

```sql
-- Macro UDF
CREATE [OR REPLACE] [PURE] FUNCTION [IF NOT EXISTS] name(
    @param TYPE [IS NOT NULL] [= default] [, ...]
) [RETURNS TYPE [IS NOT NULL]] AS expression;

-- Procedural UDF
CREATE [OR REPLACE] [PURE] FUNCTION [IF NOT EXISTS] name(
    @param TYPE [IS NOT NULL] [= default] [, ...]
) RETURNS TYPE [IS NOT NULL]
BEGIN
    [DECLARE @var TYPE [= expr] ;]
    [SET @var = expr ;]
    [IF predicate statement [ELSE statement]]
    [WHILE predicate statement]
    RETURN expr
END

DROP FUNCTION [IF EXISTS] name;

SELECT udf.name(arg1, arg2) FROM ...;

EXEC udf.name(arg1, arg2);
```

## Macro UDFs

A macro UDF's body is a scalar expression that is spliced in at every call
site by the planner. By the time the operator tree is built, no `udf.`
references remain.

```sql
CREATE FUNCTION shout(@name STRING) AS upper(@name);

SELECT udf.shout(first_name) FROM users;
-- equivalent to: SELECT upper(first_name) FROM users
```

Parameters use the `@`-prefix at the declaration site and are referenced the
same way inside the body. The inliner substitutes each `@param` with the
corresponding call-site argument expression at plan time:

```sql
CREATE FUNCTION add(@a INT32, @b INT32) AS @a + @b;

SELECT udf.add(price, tax) FROM orders;
-- equivalent to: SELECT price + tax FROM orders
```

A bare identifier in the body (no `@`-prefix, not a built-in function)
resolves against the columns available at the call site — see
[Scoping Rules](#scoping-rules).

### Execution Model

Macro UDFs incur no runtime overhead. Inlining happens once per `Plan(...)`
call, before operator construction. The planner sees the substituted body
and applies all its usual optimizations (model hoisting,
common-subexpression elimination, predicate pushdown).

A macro UDF that appears N times in a query produces N independent inlined
copies of the body. CSE may consolidate textually identical sub-expressions
that share the same arguments, but nondeterministic primitives inside the
body remain independent — each call site gets its own random draw and its
own model dispatch.

## Procedural UDFs

A procedural UDF's body is a statement sequence. The body executes at
runtime per row, with parameters and `DECLARE` variables bound into a fresh
scope each time.

```sql
CREATE FUNCTION twin() RETURNS STRING
BEGIN
    DECLARE @x FLOAT32 = random(0.0, 1.0);
    RETURN concat(CAST(@x AS STRING), '/', CAST(@x AS STRING))
END
```

`@x` is evaluated once when the DECLARE runs and reused both times it
appears in the RETURN. A macro UDF would re-evaluate `random(0.0, 1.0)`
at every reference, producing two different numbers.

### Body Statements

Inside a `BEGIN … END` block:

| Statement | Description |
|---|---|
| `DECLARE @var TYPE [= expr]` | Declares a local variable, optionally initialised. Without `= expr`, the variable is a typed NULL. |
| `SET @var = expr` | Reassigns an existing variable. |
| `IF predicate stmt [ELSE stmt]` | Conditional branch. Either arm can be a single statement or a `BEGIN … END` block. |
| `WHILE predicate stmt` | Loops while the predicate holds. |
| `BEGIN … END` | Block, primarily useful as the arm of `IF`/`WHILE`. |
| `RETURN expr` | Terminates the function and returns the expression's value. |

Every control-flow path through the body must end in `RETURN`. The parser
rejects bodies where a path can fall off the end without a `RETURN`.

### RETURNS is Required

Procedural UDFs must declare `RETURNS TYPE`. The parser enforces this. At
execution time, if the `RETURN`-ed value's kind differs from the declared
type, an implicit `CAST` is applied — the same coercion macro UDFs apply at
inline time.

### Execution Model

Procedural UDFs execute per row via a runtime adapter. Each call:

1. Binds the call-site arguments to parameter names in a fresh `VariableScope`.
2. Walks the statement body in order, evaluating expressions against the same
   `FunctionRegistry` the outer query uses. Nested `udf.X(...)` calls
   dispatch through the registry — macros were already inlined into the body
   at registration time; procedural-to-procedural calls dispatch at runtime.
3. Returns the value produced by the first `RETURN` reached.

Because the body runs at runtime, procedural UDFs carry per-row execution
overhead. For a body whose cost is dominated by a `models.*` dispatch, the
overhead is negligible. For a body that is purely arithmetic, a well-crafted
macro UDF may be cheaper.

### Composition

A procedural body can call macro UDFs and other procedural UDFs:

```sql
CREATE FUNCTION dbl(@x INT32) AS @x * 2;   -- macro

CREATE FUNCTION quad(@x INT32) RETURNS INT32
BEGIN
    RETURN udf.dbl(udf.dbl(@x))
END
```

The reference to `udf.dbl` is inlined into the body at registration time,
so the runtime adapter sees `@x * 2` substituted in. Procedural-to-procedural
calls are resolved through the registry at evaluation time.

### Forward References

Two procedural UDFs can reference each other. Registering `a` before `b` is
valid even when `a`'s body calls `udf.b`:

```sql
CREATE FUNCTION a(@x INT32) RETURNS INT32 BEGIN RETURN udf.b(@x) END
CREATE FUNCTION b(@x INT32) RETURNS INT32 BEGIN RETURN @x + 1 END
```

Both can be defined in either order. Actual mutual recursion (`a → b → a`)
is caught at runtime — see [Cycle Detection](#cycle-detection).

## PURE modifier

`CREATE [PURE] FUNCTION` marks the UDF as pure: the same arguments always
yield the same result with no side effects.

```sql
CREATE PURE FUNCTION square(@x INT32) RETURNS INT32 BEGIN RETURN @x * @x END
```

The `PURE` modifier enables CSE to treat the UDF as a hoistable leaf.
Without it, a procedural UDF at multiple call sites in the same query
evaluates once per site. With it, the CSE pass can consolidate identical
call sites into a single evaluation:

```sql
SELECT udf.square(v), udf.square(v) + 1
FROM data
```

With `PURE`, a single call to `udf.square(v)` is hoisted; both references
read the cached result. Without `PURE`, `udf.square(v)` evaluates twice per
row.

For macro UDFs, `PURE` is stored in the catalog and surfaces in
`system_udfs.is_pure`, but it has no CSE effect — the macro is already
inlined at plan time, so CSE sees the expanded expression rather than a
`udf.` call. Mark macro UDFs `PURE` only if you also want downstream tools
to know the UDF is deterministic.

**Correctness note:** Declaring a UDF `PURE` when its body calls
`random`, `now()`, `models.*`, or any other impure function silently
makes CSE consolidate calls that should be independent. The engine trusts
the declaration — the burden is on the author.

## Shared Syntax Details

These rules apply to both macro and procedural UDFs.

### OR REPLACE / OR ALTER

`CREATE OR REPLACE FUNCTION` overwrites an existing UDF. Without it,
redefinition is rejected. `CREATE OR ALTER FUNCTION` is accepted as a
synonym for users coming from T-SQL.

```sql
CREATE OR REPLACE FUNCTION shout(@name STRING) AS lower(@name);
CREATE OR ALTER  FUNCTION shout(@name STRING) AS lower(@name);  -- same
```

`OR REPLACE` can swap a name between the two body shapes. A procedural UDF
replaced by a macro has its runtime adapter removed; a macro replaced by a
procedural gains one.

### IF NOT EXISTS

`CREATE FUNCTION IF NOT EXISTS` is a no-op when a UDF with that name already
exists. The original definition wins; no error is raised.

```sql
CREATE FUNCTION IF NOT EXISTS shout(@name STRING) AS upper(@name);
```

### IS NOT NULL on parameters

Append `IS NOT NULL` to a parameter type to require a non-null argument. A
NULL at the call site throws an error naming the parameter.

```sql
CREATE FUNCTION shout(@name STRING IS NOT NULL) AS upper(@name);

SELECT udf.shout(first_name) FROM users WHERE first_name IS NOT NULL;
-- works fine

SELECT udf.shout(NULL) FROM dual;
-- error: UDF 'udf.shout' parameter '@name' must not be null.
```

For procedural UDFs the null check fires before any body statement runs.

### Default parameter values

A parameter declared with `= expr` becomes optional at the call site.
Omitted trailing arguments fall back to the default. Defaults must sit at
the tail of the parameter list.

```sql
CREATE FUNCTION add(@a INT32, @b INT32 = 5) AS @a + @b;

SELECT udf.add(2);     -- 7
SELECT udf.add(2, 10); -- 12
```

`IS NOT NULL` precedes `=`:

```sql
CREATE FUNCTION shout(@name STRING IS NOT NULL = 'world') AS upper(@name);
```

### RETURNS

`RETURNS TYPE` declares the result kind. For macro UDFs it is optional; the
inliner wraps the body with an implicit `CAST`. For procedural UDFs it is
required — the parser rejects a `BEGIN … END` body without it.

```sql
CREATE FUNCTION truncated(@x FLOAT64) RETURNS INT32 AS @x;

SELECT udf.truncated(3.7) FROM dual;  -- yields 3
```

Add `IS NOT NULL` to the return type to assert the body never returns NULL:

```sql
CREATE FUNCTION parsed(@s STRING) RETURNS INT32 IS NOT NULL
AS try_cast(@s, INT32);
```

### DROP FUNCTION

Removes a UDF. Subsequent calls fail at plan time. `DROP FUNCTION IF EXISTS`
is a no-op when the name doesn't exist.

```sql
DROP FUNCTION shout;
DROP FUNCTION IF EXISTS shout;
```

### Calling a UDF

UDFs are called through the `udf.` namespace. The argument count must match
the declared arity (or fall within the `min–max` range when defaults are
present).

```sql
SELECT udf.add(1, 2) FROM dual;
EXEC udf.shout('hello');
```

UDF names are case-insensitive.

### Direct execution with EXEC

`EXEC` executes a callable expression as a standalone statement and returns
its result as a single-row, single-column result set.

```sql
EXEC udf.shout('hello');               -- yields 'HELLO'
EXEC udf.dnd_rewrite_caption(caption); -- yields the LLM-rewritten string
```

When the call resolves to a streaming LLM, `EXEC` forwards tokens to the
terminal as the model produces them. Non-streaming calls behave identically
to `SELECT udf.name(arg)`.

## Composing UDFs

A UDF body can call any other registered UDF. For macros the inliner expands
nested calls depth-first; for procedurals the runtime adapter dispatches
through the function registry.

```sql
CREATE FUNCTION first_token(@s STRING)  AS split(@s, ' ')[0];
CREATE FUNCTION shout_first(@s STRING)  AS upper(udf.first_token(@s));

SELECT udf.shout_first(headline) FROM articles;
-- expanded plan: upper(split(headline, ' ')[0])
```

UDFs may wrap model invocations:

```sql
CREATE FUNCTION dnd_rewrite_caption(@caption STRING) AS
    models.llama31_8b(
        udf.dnd_rewrite_prompt(
            @caption,
            random_choice(array('gothic', 'folk horror', 'cosmic horror')),
            random_choice(array('possession', 'curse', 'time loop'))
        ),
        random(0.7, 1.0));
```

## Cycle Detection

A UDF must not call itself directly or transitively. Detection differs by
body shape:

- **Macro UDFs** — direct self-reference (`udf.A` in `A`'s body) is caught
  at registration time. Indirect cycles surface at the first call site that
  closes the loop, because `B` may not exist when `A` is created.
- **Procedural UDFs** — caught at runtime via a per-async-context invocation
  stack. Re-entering a procedural UDF whose name is already on the stack
  throws immediately.

```sql
-- Macro cycle (indirect, caught at call site)
CREATE FUNCTION a(@x INT32) AS udf.b(@x);
CREATE FUNCTION b(@x INT32) AS udf.a(@x);
SELECT udf.a(1) FROM dual;
-- error: Cyclic UDF reference detected: a → b → a.

-- Procedural cycle (direct, caught at runtime)
CREATE FUNCTION recurse(@x INT32) RETURNS INT32 BEGIN RETURN udf.recurse(@x) END
SELECT udf.recurse(1) FROM dual;
-- error: Cyclic procedural UDF call detected: recurse.
```

## Scoping Rules

UDF parameters and column references live in different namespaces:

- **`@param`** — substituted with the call-site argument at inline time
  (macros) or bound into a fresh scope at call time (procedurals).
- **Bare identifiers** in a macro body — column references resolved against
  the columns available at the call site.

```sql
CREATE FUNCTION boost(@score FLOAT32) AS @score * weight;
--                                                 ^ resolves at call site

SELECT udf.boost(raw_score) FROM models WHERE weight IS NOT NULL;
```

Procedural bodies don't support bare column references — parameters and
`DECLARE` variables are the only names the body can reference. Pass columns
through parameters.

### Lambda and SCAN Bindings

Lambda parameters bind bare identifiers and live in a separate namespace
from UDF parameters:

```sql
CREATE FUNCTION sum_doubled(@arr ARRAY) AS
    array_reduce(@arr, (a, b) -> a + b, 0);
-- @arr is substituted at inline time; 'a' and 'b' are lambda-scoped.
```

## Introspection

The `system_udfs` virtual table surfaces every registered UDF:

```sql
SELECT name, body_kind, is_pure, parameter_count, parameters, return_type, body
FROM system_udfs
ORDER BY name;
```

Schema:

| Column            | Type    | Nullable | Description |
|-------------------|---------|----------|-------------|
| `name`            | String  | no       | Unqualified UDF name. Call sites use the `udf.` prefix. |
| `parameter_count` | Int32   | no       | Number of declared parameters. |
| `parameters`      | String  | no       | Comma-separated `"@name TYPE [IS NOT NULL]"` rendition. |
| `return_type`     | String  | yes      | The `RETURNS` annotation (with any `IS NOT NULL` suffix), or NULL when omitted. |
| `body_kind`       | String  | no       | `"macro"` or `"procedural"`. |
| `is_pure`         | Boolean | no       | Whether the UDF was declared `PURE`. |
| `body`            | String  | no       | For macros, the body expression formatted from the AST. For procedurals, the original source text of the `BEGIN … END` block. |

## Persistence

A `TableCatalog` constructed with a catalog-file path persists its UDF
registry across process restarts:

```csharp
TableCatalog catalog = new(pool, catalogPath: "/data/.datum-catalog.json");
```

When the path is supplied:

- Existing UDFs are loaded from the file at construction.
- `CREATE FUNCTION` and `DROP FUNCTION` write through atomically (write to
  `.tmp`, rename into place) so a crash mid-save never leaves a partial file.
- Per-entry failures are skipped with a warning collected on
  `TableCatalog.CatalogLoadReport.Warnings`. A single corrupt entry doesn't
  take down the session.
- A catalog file written by a newer binary (higher `version`) is treated as
  opaque; the registry stays empty.

The on-disk format stores the body shape in `body_kind`:

```json
{
  "version": 1,
  "udfs": [
    {
      "name": "shout",
      "parameters": [{"name": "name", "type": "STRING", "isNotNull": false}],
      "returnType": null,
      "returnIsNotNull": false,
      "body_kind": "macro",
      "body": "upper(@name)"
    },
    {
      "name": "twin",
      "parameters": [],
      "returnType": "STRING",
      "returnIsNotNull": false,
      "body_kind": "procedural",
      "is_pure": false,
      "source_text": "BEGIN\n    DECLARE @x FLOAT32 = random(0.0, 1.0);\n    RETURN concat(CAST(@x AS STRING), '/', CAST(@x AS STRING))\nEND"
    }
  ]
}
```

Procedural entries store the verbatim source text. The catalog re-runs macro
inlining when loading them, so the stored form is always the as-written body.

## Limitations

- **No recursion.** A UDF may not call itself directly or transitively.
  Recursive logic belongs in a recursive CTE.
- **Scalar only.** UDFs return a single value per call. Table-valued
  functions are not supported.
- **One signature per name.** Overloading by parameter count or types is not
  supported.
- **No compiled code.** UDFs are SQL macros or procedural statement bodies.
  Authoring UDFs in C# is not supported through this surface.
- **Procedural bodies cannot reference outer columns.** Bare column names in
  a `BEGIN … END` body are not resolved against the calling query's row.
  Pass columns through parameters.
- **No scalar subqueries in procedural bodies.** `RETURN (SELECT ...)` parses
  but fails at execution because the body evaluator doesn't drive a query
  plan — only direct expressions, function calls (including `udf.X` and
  `models.X`), and variable references are resolvable inside the body.
- **No BREAK/CONTINUE in procedural bodies.** These are parsed but rejected
  at runtime; use `IF`/`RETURN` to short-circuit.
- **Subquery bodies don't see macro parameters.** A `@param` inside
  `(SELECT ... WHERE col = @param)` in a macro body survives inlining as a
  `VariableExpression` and is resolved at evaluation time against the
  procedural variable scope (if any). Pass arguments through the outer
  expression instead.

## See Also

- [Functions](../functions/utility.md) — built-in scalar functions usable in UDF bodies
- [Models](../models.md) — `models.X(...)` invocations that UDFs commonly wrap
- [Lambda Expressions](lambda-expressions.md) — how scope shadowing interacts with UDF parameters
- [Common Table Expressions](cte.md) — for recursive logic that UDFs cannot express
- [Common Subexpression Elimination](../common-subexpression-elimination.md) — how `PURE` interacts with the CSE pass
- [system_udfs](#introspection) — querying registered UDFs from SQL
- [CREATE PROCEDURE](procedural.md#create-procedure) — the multi-statement equivalent for orchestration that returns rows rather than a scalar
