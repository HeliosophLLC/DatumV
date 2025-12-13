---
title: User-Defined Functions
---

User-defined functions (UDFs) in DatumIngest are SQL macros: a named scalar
expression with declared parameters that the planner inlines at every call
site. The body can be any scalar expression — including model invocations,
template strings, nested UDF calls, and references to columns or other UDFs
— and it executes in the calling site's scope, with parameter references
substituted by the call's argument expressions.

UDFs are registered against a `TableCatalog` with `CREATE FUNCTION` and
referenced through the `udf.` namespace. By the time the planner builds the
operator tree, no UDF call sites remain; the body has been spliced in at
each reference. This means UDFs cost nothing at execution time beyond the
work the body itself implies, and non-deterministic primitives in the body
(`random_string`, `random_float32`, `models.*`) re-evaluate per call site
exactly as they would if the user had typed the body inline.

## Syntax

```sql
CREATE [OR REPLACE] FUNCTION [IF NOT EXISTS] name(
    @param1 TYPE [IS NOT NULL] [, @param2 TYPE [IS NOT NULL] ...]
) [RETURNS TYPE [IS NOT NULL]] AS expression;

DROP FUNCTION [IF EXISTS] name;

SELECT udf.name(arg1, arg2) FROM ...;

EXEC udf.name(arg1, arg2);
```

### CREATE FUNCTION

Registers a UDF. The body is parsed as a scalar expression, validated for
unresolved UDF references and direct cycles, and stored on the catalog.
Subsequent queries that reference `udf.name` see the inlined body.

```sql
CREATE FUNCTION shout(@name STRING) AS upper(@name);

SELECT udf.shout(first_name) FROM users;
-- equivalent to: SELECT upper(first_name) FROM users
```

Parameters use the `@`-prefix at the declaration site and are referenced
the same way inside the body — the same notation as procedural variables
(see [Procedural Statements](procedural.md#variables)). The inliner
substitutes each `@param` reference with the corresponding call-site
argument expression at plan time:

```sql
CREATE FUNCTION add(@a INT32, @b INT32) AS @a + @b;

SELECT udf.add(price, tax) FROM orders;
-- equivalent to: SELECT price + tax FROM orders
```

A bare identifier in the body (no `@`-prefix, not a built-in function)
resolves against the columns available at the call site — see
[Scoping Rules](#scoping-rules).

#### OR REPLACE

`CREATE OR REPLACE FUNCTION` overwrites an existing UDF with the same name.
Without `OR REPLACE`, redefinition is rejected. Useful while iterating on
prompt templates and call shapes from a session.

```sql
CREATE OR REPLACE FUNCTION shout(@name STRING) AS lower(@name);
```

#### IF NOT EXISTS

`CREATE FUNCTION IF NOT EXISTS` is a no-op when a UDF with that name is
already registered. The original definition wins; no error is raised. Use
this when a setup script may run more than once.

```sql
CREATE FUNCTION IF NOT EXISTS shout(@name STRING) AS upper(@name);
```

#### IS NOT NULL on parameters

Append `IS NOT NULL` to a parameter type to declare that the call site
must pass a non-null argument. The inliner wraps the substituted argument
with a runtime null check; passing a NULL throws an
`InvalidOperationException` naming the parameter so the user can locate
the offending call site.

```sql
CREATE FUNCTION shout(@name STRING IS NOT NULL) AS upper(@name);

SELECT udf.shout(first_name) FROM users WHERE first_name IS NOT NULL;
-- works fine

SELECT udf.shout(NULL) FROM dual;
-- error: UDF 'udf.shout' parameter '@name' must not be null.
```

`IS NOT NULL` on a parameter shifts the null check from "body's three-
valued logic propagates NULL" to "fail loud, fail early." Use it for
parameters where a NULL would either crash the body in a confusing way
or silently return a wrong answer.

#### RETURNS

The optional `RETURNS TYPE` annotation enforces the declared kind: the
inliner wraps the substituted body with an implicit `CAST` to the
declared type, so the call site sees the declared kind regardless of
the body's natural type.

```sql
CREATE FUNCTION truncated(@x FLOAT64) RETURNS INT32 AS @x;

SELECT udf.truncated(3.7) FROM dual;
-- yields 3 as Int32
```

Add `IS NOT NULL` to the return type to enforce that the body produces
a non-null value:

```sql
CREATE FUNCTION parsed(@s STRING) RETURNS INT32 IS NOT NULL
AS try_cast(@s, INT32);

SELECT udf.parsed('42') FROM dual;
-- yields 42

SELECT udf.parsed('not a number') FROM dual;
-- error: UDF 'udf.parsed' return value must not be null.
```

The two annotations stack: the declared `CAST` runs first, then the
null assertion checks the cast result. Either annotation alone is also
valid.

### DROP FUNCTION

Removes a registered UDF. Subsequent calls to `udf.name(...)` fail at plan
time with "UDF 'udf.name' is not registered". `DROP FUNCTION IF EXISTS` is
a no-op when the UDF doesn't exist.

```sql
DROP FUNCTION shout;
DROP FUNCTION IF EXISTS shout;
```

### Calling a UDF

UDFs are called through the `udf.` namespace, mirroring the `models.`
namespace for model invocations. The argument list must match the declared
parameter count; mismatches raise an error at plan time.

```sql
SELECT udf.add(1, 2) FROM dual;        -- yields 3
SELECT udf.add(1)    FROM dual;        -- error: expects 2 arguments, got 1
```

The bare expression form (no `FROM`) works wherever the planner accepts a
tableless query, so you can validate a UDF's body in isolation:

```sql
SELECT udf.shout('hello') FROM dual;   -- yields 'HELLO'
```

UDF names are case-insensitive: `udf.Shout` and `udf.SHOUT` resolve to the
same descriptor.

### Direct execution with EXEC

`EXEC` executes a function as a standalone statement and returns its result as
a single-row, single-column result set. It is equivalent to writing
`SELECT udf.name(args)` without a `FROM` clause, but makes the intent — "run
this function, give me the result" — explicit in multi-statement scripts.

```sql
EXEC udf.shout('hello');           -- yields 'HELLO'
EXEC udf.dnd_rewrite_caption(cap); -- yields the LLM-rewritten string
```

`EXEC` accepts any callable expression that the expression parser recognises,
including namespace-qualified names (`udf.`, `models.`) and calls with any
number of arguments:

```sql
EXEC upper('hello');               -- built-in scalar
EXEC models.llama31_8b($prompt);   -- model call with a parameter
```

The full UDF inlining and model-hoisting pipeline applies — `EXEC udf.fn(x)`
and `SELECT udf.fn(x)` produce the same value. They differ in delivery: when
the call resolves to a streaming model (an LLM), `EXEC` forwards tokens to
the terminal (in `datum-shell`) or to a live streaming pane (in `datum-devweb`)
as the model produces them, while `SELECT` collects the full response before
rendering. Non-deterministic primitives in the body (`random_string`,
`random_float32`, model calls) re-evaluate on each execution, exactly as they
would inline.

## Composing UDFs

A UDF's body can call any other registered UDF. The inliner expands nested
calls children-first, so the resulting plan has no UDF references at any
depth.

```sql
CREATE FUNCTION first_token(@s STRING)  AS split(@s, ' ')[0];
CREATE FUNCTION shout_first(@s STRING)  AS upper(udf.first_token(@s));

SELECT udf.shout_first(headline) FROM articles;
-- expanded plan: upper(split(headline, ' ')[0])
```

UDFs may also wrap model invocations, which is the canonical use case:
factor a recurring prompt-building expression out of every consumer SQL.

```sql
CREATE FUNCTION dnd_rewrite_prompt(
    @caption STRING,
    @tone STRING,
    @threat STRING
) AS `Rewrite this caption in D&D style.
Tone: ${@tone}, Core Threat: ${@threat}
Caption: ${@caption}
Return only the new caption.`;

CREATE FUNCTION dnd_rewrite_caption(@caption STRING) AS
    models.llama31_8b(
        udf.dnd_rewrite_prompt(
            @caption,
            random_string('gothic', 'folk horror', 'cosmic horror'),
            random_string('possession', 'curse', 'time loop')
        ),
        random_float32(0.7, 1.0));

SELECT udf.dnd_rewrite_caption(caption) FROM photos LIMIT 10;
```

The `random_string` and `random_float32` calls in the body re-evaluate at
each call site, so each row gets a fresh roll. The `models.llama31_8b`
invocation is hoisted out of the inlined expression by the existing model
hoister and dispatches as a single batched call per chunk of rows.

## Cycle Detection

A UDF must not reference itself directly (`udf.A` in `A`'s body) or
transitively (`A → B → A`). Direct self-reference is caught at registration
time when the body is validated. Indirect cycles can't always be caught at
registration — `B` may not exist when `A` is created — so they surface at
the first call site that closes the loop:

```sql
CREATE FUNCTION a(@x INT32) AS udf.b(@x);
CREATE FUNCTION b(@x INT32) AS udf.a(@x);  -- registers cleanly
SELECT udf.a(1) FROM dual;
-- error: Cyclic UDF reference detected: a → b → a.
```

## Scoping Rules

UDF parameters and the body's column references live in different
namespaces:

- **`@param`** — substituted with the call-site argument expression at
  inline time. The body sees no `@param` references in the planner's
  operator tree.
- **Bare identifiers** in the body — column references resolved against
  the columns available at the call site.

```sql
CREATE FUNCTION boost(@score FLOAT32) AS @score * weight;
--                                                 ^ not a parameter

-- 'weight' resolves to the column at the call site:
SELECT udf.boost(raw_score) FROM models WHERE weight IS NOT NULL;
```

This is "macro-style" scoping for the bare-identifier path. It enables
small composition tricks (a UDF that adapts to the surrounding query's
schema) but means a UDF's behavior isn't fully determined by its
declaration alone — readers need to know the calling context. When a
body needs to be self-contained, every column reference should arrive
through a parameter.

### Lambda and SCAN Bindings

Lambda parameters and SCAN accumulator names bind bare identifiers, not
`@`-prefixed variables. They live in a separate namespace from UDF
parameters, so a lambda parameter named `x` and a UDF parameter named
`@x` don't interact:

```sql
CREATE FUNCTION sum_doubled(@arr ARRAY) AS
    array_reduce(@arr, (a, b) -> a + b, 0);
-- @arr is substituted at inline time; the lambda's 'a' and 'b' bind
-- bare names that the inliner never touches.
```

## Execution Model

UDFs incur no runtime overhead. Inlining happens once per `Plan(...)` call
on the parsed AST, before operator construction. The planner sees the
substituted body and applies all its usual optimizations (model hoisting,
common-subexpression elimination, predicate pushdown).

A UDF that appears N times in a query produces N independent inlined
copies of the body. CSE may consolidate textually identical sub-expressions
that share the same arguments, but non-deterministic primitives (random
functions, model calls) inside the body remain independent — each call
site gets its own random draw and its own model dispatch.

`CREATE FUNCTION` and `DROP FUNCTION` apply to the catalog as a side
effect of `Plan(...)` and return an empty result set. The DDL is
process-scoped: each `TableCatalog` has its own UDF registry, and
restarting the host clears them.

## Introspection

The `system_udfs` virtual table surfaces every registered UDF as queryable
rows. Use it from any session to see what's defined, the parameter list,
the declared return type, and the formatted body:

```sql
SELECT name, parameter_count, parameters, return_type, body
FROM system_udfs
ORDER BY name;
```

Schema:

| Column            | Type   | Nullable | Description                                                                          |
|-------------------|--------|----------|--------------------------------------------------------------------------------------|
| `name`            | String | no       | Unqualified UDF name. Call sites use the `udf.` prefix.                              |
| `parameter_count` | Int32  | no       | Number of declared parameters. `0` for nullary UDFs.                                 |
| `parameters`      | String | no       | Comma-separated `"@name TYPE [IS NOT NULL], @name TYPE"` rendition. Empty for nullary. |
| `return_type`     | String | yes      | The `RETURNS` annotation including any `IS NOT NULL` suffix, or NULL when omitted.   |
| `body`            | String | no       | Body expression formatted from the AST. Whitespace may differ from input.            |

`system_udfs` is auto-registered against every `TableCatalog` — no host
setup required.

## Persistence

A `TableCatalog` constructed with a catalog-file path persists its UDF
registry across process restarts. The host opts in by passing the path:

```csharp
TableCatalog catalog = new(pool, catalogPath: "/data/.datum-catalog.json");
```

When the path is supplied:

- Existing UDFs are loaded from the file at construction. The directory
  doesn't have to exist yet — it's created on first save.
- `CREATE FUNCTION` and `DROP FUNCTION` write through to the same file
  atomically (write to `.tmp`, rename into place) so a crash mid-save
  never leaves a partial file.
- Per-entry failures (a body that no longer parses, an unresolved
  reference, a future schema feature this binary doesn't recognise) are
  skipped with a warning collected on
  `TableCatalog.CatalogLoadReport.Warnings`. A single corrupt entry
  doesn't take down the session.
- A catalog file written by a newer binary (higher `version`) is treated
  as opaque; the registry stays empty. The next save will downgrade the
  file to the current binary's format.

When no path is supplied (the parameterless constructor), the registry is
in-memory only — UDFs disappear when the process exits.

The on-disk format is a JSON document with this shape:

```json
{
  "version": 1,
  "udfs": [
    {
      "name": "shout",
      "parameters": [{"name": "name", "type": "STRING", "isNotNull": false}],
      "returnType": null,
      "returnIsNotNull": false,
      "body": "upper(@name)"
    }
  ]
}
```

The `name` field on a parameter stores the bare identifier without the
`@`-prefix; the body string round-trips with the prefix because the
parser re-applies it on load.

The file format is forward-compatible with future catalog sections (bound
data files, materialised views, fingerprints). New sections are additive;
existing readers ignore unknown top-level fields.

## Limitations

- **No recursion.** A UDF may not reference itself directly or
  transitively. Recursive logic belongs in a recursive CTE.
- **Scalar only.** UDFs return a single value per call. Table-valued
  functions are not supported.
- **One signature per name.** Overloading by parameter count or types
  is not supported. Re-define an existing UDF with `CREATE OR REPLACE`.
- **No compiled code.** UDFs are SQL expression macros. Authoring UDFs
  in C# or another host language is not supported through this surface.
- **Subquery bodies don't see parameters.** Parameter substitution does
  not walk into subqueries embedded in the body; a `@param` reference
  inside `(SELECT ... WHERE col = @param)` survives inlining as a
  `VariableExpression` and is resolved at evaluation time against the
  procedural variable scope (if any). Pass arguments through the outer
  expression instead.

## See Also

- [Functions](../functions/utility.md) — built-in scalar functions usable in UDF bodies
- [Models](../models.md) — `models.X(...)` invocations that UDFs commonly wrap
- [Lambda Expressions](lambda-expressions.md) — how scope shadowing interacts with UDF parameters
- [Common Table Expressions](cte.md) — for recursive logic that UDFs cannot express
- [system_udfs](#introspection) — querying registered UDFs from SQL
