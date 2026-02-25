---
title: User-Defined Functions
---

User-defined functions (UDFs) allow you to name a reusable computation and
call it like any other function. UDFs live in [schemas](schema-introspection.md) — by
default `public` — and are callable by either their bare name (when the
schema is on the session `search_path`) or a schema-qualified name
(`analytics.shout(...)`). DatumIngest supports two UDF body shapes with
different trade-offs:

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
CREATE [OR REPLACE] [PURE] FUNCTION [IF NOT EXISTS] [schema.]name(
    param TYPE [IS NOT NULL] [= default] [, ...]
) [RETURNS TYPE [IS NOT NULL]] AS expression;

-- Procedural UDF
CREATE [OR REPLACE] [PURE] FUNCTION [IF NOT EXISTS] [schema.]name(
    param TYPE [IS NOT NULL] [= default] [, ...]
) RETURNS TYPE [IS NOT NULL]
BEGIN
    [DECLARE var TYPE [= expr] ;]
    [SET var = expr ;]
    [IF predicate statement [ELSE statement]]
    [WHILE predicate statement]
    RETURN expr
END

DROP FUNCTION [IF EXISTS] [schema.]name;

SELECT name(arg1, arg2) FROM ...;
SELECT schema.name(arg1, arg2) FROM ...;

CALL name(arg1, arg2);
```

An unqualified `CREATE FUNCTION name(...)` lands in the first writable
schema on the session `search_path` — `public` in a default session.
Qualifying the name (`CREATE FUNCTION analytics.shout(...)`) targets that
schema directly. Call sites follow the same rule: bare names resolve
against `search_path`; qualifying picks an exact schema.

## Macro UDFs

A macro UDF's body is a scalar expression that is spliced in at every call
site by the planner. By the time the operator tree is built, no UDF call
nodes remain — the substituted body is what the planner sees.

```sql
CREATE FUNCTION shout(name STRING) AS upper(name);

SELECT shout(first_name) FROM users;
-- equivalent to: SELECT upper(first_name) FROM users
```

Parameters are bare identifiers — no sigil — at both the declaration site
and inside the body. The inliner substitutes each `param` with the
corresponding call-site argument expression at plan time:

```sql
CREATE FUNCTION add(a INT32, b INT32) AS a + b;

SELECT add(price, tax) FROM orders;
-- equivalent to: SELECT price + tax FROM orders
```

A bare identifier in the body that doesn't match a parameter resolves
against the columns available at the call site — see [Scoping
Rules](#scoping-rules).

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
    DECLARE x FLOAT32 = random(0.0, 1.0);
    RETURN concat(CAST(x AS STRING), '/', CAST(x AS STRING))
END
```

`x` is evaluated once when the DECLARE runs and reused both times it
appears in the RETURN. A macro UDF would re-evaluate `random(0.0, 1.0)`
at every reference, producing two different numbers.

### Body Statements

Inside a `BEGIN … END` block:

| Statement | Description |
|---|---|
| `DECLARE var TYPE [= expr]` | Declares a local variable, optionally initialised. Without `= expr`, the variable is a typed NULL. |
| `SET var = expr` | Reassigns an existing variable. |
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
   `FunctionRegistry` the outer query uses. Nested UDF calls dispatch
   through the registry — macros were already inlined into the body at
   registration time; procedural-to-procedural calls dispatch at runtime.
3. Returns the value produced by the first `RETURN` reached.

Because the body runs at runtime, procedural UDFs carry per-row execution
overhead. For a body whose cost is dominated by a `models.*` dispatch, the
overhead is negligible. For a body that is purely arithmetic, a well-crafted
macro UDF may be cheaper.

### Composition

A procedural body can call macro UDFs and other procedural UDFs:

```sql
CREATE FUNCTION dbl(x INT32) AS x * 2;   -- macro

CREATE FUNCTION quad(x INT32) RETURNS INT32
BEGIN
    RETURN dbl(dbl(x))
END
```

The reference to `dbl` is inlined into the body at registration time,
so the runtime adapter sees `x * 2` substituted in. Procedural-to-procedural
calls are resolved through the registry at evaluation time.

### Forward References

Two procedural UDFs can reference each other. Registering `a` before `b` is
valid even when `a`'s body calls `b`:

```sql
CREATE FUNCTION a(x INT32) RETURNS INT32 BEGIN RETURN b(x) END
CREATE FUNCTION b(x INT32) RETURNS INT32 BEGIN RETURN x + 1 END
```

Both can be defined in either order. Actual mutual recursion (`a → b → a`)
is caught at runtime — see [Cycle Detection](#cycle-detection).

## PURE modifier

`CREATE [PURE] FUNCTION` marks the UDF as pure: the same arguments always
yield the same result with no side effects.

```sql
CREATE PURE FUNCTION square(x INT32) RETURNS INT32 BEGIN RETURN x * x END
```

The `PURE` modifier enables CSE to treat the UDF as a hoistable leaf.
Without it, a procedural UDF at multiple call sites in the same query
evaluates once per site. With it, the CSE pass can consolidate identical
call sites into a single evaluation:

```sql
SELECT square(v), square(v) + 1
FROM data
```

With `PURE`, a single call to `square(v)` is hoisted; both references
read the cached result. Without `PURE`, `square(v)` evaluates twice per
row.

For macro UDFs, `PURE` is stored in the catalog and surfaces in
`system.udfs.is_pure`, but it has no CSE effect — the macro is already
inlined at plan time, so CSE sees the expanded expression rather than a
function-call node. Mark macro UDFs `PURE` only if you also want downstream
tools to know the UDF is deterministic.

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
CREATE OR REPLACE FUNCTION shout(name STRING) AS lower(name);
CREATE OR ALTER  FUNCTION shout(name STRING) AS lower(name);  -- same
```

`OR REPLACE` can swap a name between the two body shapes. A procedural UDF
replaced by a macro has its runtime adapter removed; a macro replaced by a
procedural gains one.

### IF NOT EXISTS

`CREATE FUNCTION IF NOT EXISTS` is a no-op when a UDF with that name already
exists. The original definition wins; no error is raised.

```sql
CREATE FUNCTION IF NOT EXISTS shout(name STRING) AS upper(name);
```

### IS NOT NULL on parameters

Append `IS NOT NULL` to a parameter type to require a non-null argument. A
NULL at the call site throws an error naming the parameter.

```sql
CREATE FUNCTION shout(name STRING IS NOT NULL) AS upper(name);

SELECT shout(first_name) FROM users WHERE first_name IS NOT NULL;
-- works fine

SELECT shout(NULL) FROM dual;
-- error: UDF 'public.shout' parameter 'name' must not be null.
```

For procedural UDFs the null check fires before any body statement runs.

### Default parameter values

A parameter declared with `= expr` becomes optional at the call site.
Omitted trailing arguments fall back to the default. Defaults must sit at
the tail of the parameter list.

```sql
CREATE FUNCTION add(a INT32, b INT32 = 5) AS a + b;

SELECT add(2);     -- 7
SELECT add(2, 10); -- 12
```

`IS NOT NULL` precedes `=`:

```sql
CREATE FUNCTION shout(name STRING IS NOT NULL = 'world') AS upper(name);
```

### RETURNS

`RETURNS TYPE` declares the result kind. For macro UDFs it is optional; the
inliner wraps the body with an implicit `CAST`. For procedural UDFs it is
required — the parser rejects a `BEGIN … END` body without it.

```sql
CREATE FUNCTION truncated(x FLOAT64) RETURNS INT32 AS x;

SELECT truncated(3.7) FROM dual;  -- yields 3
```

Add `IS NOT NULL` to the return type to assert the body never returns NULL:

```sql
CREATE FUNCTION parsed(s STRING) RETURNS INT32 IS NOT NULL
AS try_cast(s, INT32);
```

### DROP FUNCTION

Removes a UDF. Subsequent calls fail at plan time. `DROP FUNCTION IF EXISTS`
is a no-op when the name doesn't exist.

```sql
DROP FUNCTION shout;
DROP FUNCTION IF EXISTS shout;
```

### Calling a UDF

UDFs are called like any other function — bare names resolve against the
session `search_path`, schema-qualified names target an exact schema. The
argument count must match the declared arity (or fall within the `min–max`
range when defaults are present).

```sql
SELECT add(1, 2) FROM dual;
SELECT analytics.add(1, 2) FROM dual;   -- explicit schema
CALL shout('hello');
```

UDF names — and schema names — are case-insensitive.

### Direct invocation with CALL

`CALL` invokes a callable expression as a standalone statement and returns
its result as a single-row, single-column result set.

```sql
CALL shout('hello');               -- yields 'HELLO'
CALL dnd_rewrite_caption(caption); -- yields the LLM-rewritten string
```

When the call resolves to a streaming LLM, `CALL` forwards tokens to the
terminal as the model produces them. Non-streaming calls behave identically
to `SELECT name(arg)`.

## Composing UDFs

A UDF body can call any other registered UDF. For macros the inliner expands
nested calls depth-first; for procedurals the runtime adapter dispatches
through the function registry.

```sql
CREATE FUNCTION first_token(s STRING)  AS split(s, ' ')[0];
CREATE FUNCTION shout_first(s STRING)  AS upper(first_token(s));

SELECT shout_first(headline) FROM articles;
-- expanded plan: upper(split(headline, ' ')[0])
```

UDFs may wrap model invocations. The `models` schema is a built-in
read-only schema that hosts every registered ONNX / LLM model — calls
look like any other schema-qualified function call:

```sql
CREATE FUNCTION dnd_rewrite_caption(caption STRING) AS
    models.llama31_8b(
        dnd_rewrite_prompt(
            caption,
            random_choice(array('gothic', 'folk horror', 'cosmic horror')),
            random_choice(array('possession', 'curse', 'time loop'))
        ),
        random(0.7, 1.0));
```

## Cycle Detection

A UDF must not call itself directly or transitively. Detection differs by
body shape:

- **Macro UDFs** — direct self-reference (`A`'s body calling `A`) is
  caught at registration time, because the inliner runs against the
  partially-built registry and sees the self-cycle while substituting.
  Indirect cycles (`A → B → A`) surface at the first call site that
  closes the loop, since `B` may not yet exist when `A` is created.
- **Procedural UDFs** — caught at runtime via a per-async-context
  invocation stack. Re-entering a procedural UDF whose qualified name
  is already on the stack throws immediately. This catches both direct
  recursion and any transitive `A → B → A` chain uniformly.

```sql
-- Macro cycle (indirect, caught at call site)
CREATE FUNCTION a(x INT32) AS b(x);
CREATE FUNCTION b(x INT32) AS a(x);
SELECT a(1) FROM dual;
-- error: Cyclic UDF reference detected: a → b → a.

-- Procedural cycle (direct, caught at runtime)
CREATE FUNCTION recurse(x INT32) RETURNS INT32 BEGIN RETURN recurse(x) END
SELECT recurse(1) FROM dual;
-- error: Cyclic procedural UDF call detected: public.recurse.
```

## Scoping Rules

UDF parameters and column references live in different namespaces:

- **`param`** — substituted with the call-site argument at inline time
  (macros) or bound into a fresh scope at call time (procedurals).
- **Bare identifiers** in a macro body — column references resolved against
  the columns available at the call site.

```sql
CREATE FUNCTION boost(score FLOAT32) AS score * weight;
--                                                 ^ resolves at call site

SELECT boost(raw_score) FROM scores WHERE weight IS NOT NULL;
```

Procedural bodies don't support bare column references — parameters and
`DECLARE` variables are the only names the body can reference. Pass columns
through parameters.

### Lambda and SCAN Bindings

Lambda parameters bind bare identifiers and live in a separate namespace
from UDF parameters:

```sql
CREATE FUNCTION sum_doubled(arr ARRAY) AS
    array_reduce(arr, (a, b) -> a + b, 0);
-- arr is substituted at inline time; 'a' and 'b' are lambda-scoped.
```

## Introspection

The `system.udfs` virtual table surfaces every registered UDF:

```sql
SELECT schema, name, body_kind, is_pure, parameter_count, parameters, return_type, body
FROM system.udfs
ORDER BY schema, name;
```

Schema:

| Column            | Type    | Nullable | Description |
|-------------------|---------|----------|-------------|
| `schema`          | String  | no       | Schema the UDF lives in (e.g. `public`, `analytics`). |
| `name`            | String  | no       | Unqualified UDF name. The full call site is `[schema.]name(...)`. |
| `parameter_count` | Int32   | no       | Number of declared parameters. |
| `parameters`      | String  | no       | Comma-separated `"name TYPE [IS NOT NULL]"` rendition. |
| `return_type`     | String  | yes      | The `RETURNS` annotation (with any `IS NOT NULL` suffix), or NULL when omitted. |
| `body_kind`       | String  | no       | `"macro"` or `"procedural"`. |
| `is_pure`         | Boolean | no       | Whether the UDF was declared `PURE`. |
| `body`            | String  | no       | For macros, the body expression formatted from the AST. For procedurals, the original source text of the `CREATE FUNCTION` statement. |

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

The on-disk format is manifest version 4 and stores each UDF's owning
schema alongside the body shape:

```json
{
  "version": 4,
  "udfs": [
    {
      "schema": "public",
      "name": "shout",
      "parameters": [{"name": "name", "type": "STRING", "isNotNull": false}],
      "returnType": null,
      "returnIsNotNull": false,
      "body_kind": "macro",
      "body": "upper(name)"
    },
    {
      "schema": "analytics",
      "name": "twin",
      "parameters": [],
      "returnType": "STRING",
      "returnIsNotNull": false,
      "body_kind": "procedural",
      "is_pure": false,
      "source_text": "CREATE FUNCTION analytics.twin() RETURNS STRING BEGIN\n    DECLARE x FLOAT32 = random(0.0, 1.0);\n    RETURN concat(CAST(x AS STRING), '/', CAST(x AS STRING))\nEND"
    }
  ]
}
```

The `schema` field determines the `QualifiedName` the registry stores
the entry under at load time. Procedural entries store the verbatim
`CREATE FUNCTION` source text. The catalog re-runs macro inlining when
loading them, so the stored form is always the as-written body.

Manifest versions other than 4 are rejected at load — the file format is
not backward compatible. A catalog directory written by an older binary
must be discarded and recreated.

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
  plan — only direct expressions, function calls (including UDFs and
  `models.X(...)`), and variable references are resolvable inside the body.
- **No BREAK/CONTINUE in procedural bodies.** These are parsed but rejected
  at runtime; use `IF`/`RETURN` to short-circuit.
- **Subquery bodies don't see macro parameters.** A `param` inside
  `(SELECT ... WHERE col = param)` in a macro body survives inlining as a
  `VariableExpression` and is resolved at evaluation time against the
  procedural variable scope (if any). Pass arguments through the outer
  expression instead.

## See Also

- [Schema Introspection](schema-introspection.md) — how `search_path` resolves bare function names and how to list available schemas
- [Functions](../functions/utility.md) — built-in scalar functions usable in UDF bodies
- [Models](../models.md) — the `models` schema, whose entries UDFs commonly wrap
- [Lambda Expressions](lambda-expressions.md) — how scope shadowing interacts with UDF parameters
- [Common Table Expressions](cte.md) — for recursive logic that UDFs cannot express
- [Common Subexpression Elimination](../common-subexpression-elimination.md) — how `PURE` interacts with the CSE pass
- [system.udfs](#introspection) — querying registered UDFs from SQL
- [CREATE PROCEDURE](procedural.md#create-procedure) — the multi-statement equivalent, invoked via `CALL`, that produces rows rather than a scalar
- [CREATE MODEL](create-model.md) — sibling DDL surface for binding ONNX inference sessions to a SQL-callable name. Same procedural body shape; adds the `USING '...'` clause and the body-scoped `infer()` function.
