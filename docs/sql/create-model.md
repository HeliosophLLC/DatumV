---
title: CREATE MODEL
---

`CREATE MODEL` registers a model file (currently ONNX) under the `models`
schema and binds it to a SQL-defined body that uses [`infer()`](#infer)
to dispatch the underlying inference session. Once registered, the model
is callable as `models.<name>(...)` from any query — exactly the same
call shape as the engine's [built-in models](../models.md).

The DDL is the user-facing surface for the inference layer described in
[`design-docs/onnx-inference.md`](../design-docs/onnx-inference.md).
Use it when you have an ONNX file you want to query without writing C#:
download the file, point `USING` at it, write a one-line body, and
`SELECT models.<name>(col) FROM table`.

## Syntax

```sql
CREATE [OR REPLACE] MODEL [IF NOT EXISTS] [models.]name(
    @param TYPE [IS NOT NULL] [= default] [, ...]
) RETURNS TYPE [IS NOT NULL]
  USING 'file://...'
[AS] BEGIN
    [DECLARE @var TYPE [= expr] ;]
    [SET @var = expr ;]
    [IF predicate statement [ELSE statement]]
    [WHILE predicate statement]
    RETURN infer(@param)
END

DROP MODEL [IF EXISTS] [models.]name;

SELECT models.name(arg1, arg2) FROM ...;
```

A few non-obvious rules covered in detail below:

- The body is **always procedural** (`BEGIN … END`). There is no expression-body form.
- `RETURNS` is **required** — the planner needs a known scalar shape.
- The model **always** lives in the `models` schema — `CREATE MODEL my_model(...)` and `CREATE MODEL models.my_model(...)` are equivalent; any other schema qualifier is rejected.
- `infer()` is the only meaningful operation inside a model body — it's also only callable from inside a model body (see [`infer()`](#infer)).

## A complete example

```sql
-- Register a softmax model from disk.
CREATE MODEL softmax(@x Float32[]) RETURNS Float32[]
    USING 'file:///opt/datum/models/softmax.onnx'
AS BEGIN
    RETURN infer(@x)
END;

-- Call it.
SELECT models.softmax(
    [CAST(1.0 AS Float32), CAST(2.0 AS Float32), CAST(3.0 AS Float32)])
FROM (VALUES (1)) t(d);
-- yields approximately [0.09003, 0.24473, 0.66524]
```

Because the body is procedural, the same shape supports preprocessing or
postprocessing alongside the model dispatch:

```sql
CREATE MODEL classify(@logits Float32[]) RETURNS Float32[]
    USING 'file:///opt/datum/models/classifier.onnx'
AS BEGIN
    DECLARE @scores Float32[] = infer(@logits);
    RETURN @scores
END;
```

## The USING clause

`USING` declares the on-disk path of the model file. Two forms:

- **`file://<absolute>`** — the prefix is stripped and the rest is treated
  as an absolute path. Useful for tests and for files outside the host's
  models directory.
- **Relative path** — resolved against the host's models directory
  (`TableCatalog.Models.ModelDirectory`), which is configured via the
  `--models <path>` flag on `datum-shell` or the `DATUM_MODELS`
  environment variable. See [Models › Setup](../models.md#setup) for
  the full resolution order.

```sql
-- Absolute (Linux/macOS):
CREATE MODEL m(@x Float32[]) RETURNS Float32[]
    USING 'file:///home/me/onnx/m.onnx'
AS BEGIN RETURN infer(@x) END;

-- Absolute (Windows):
CREATE MODEL m(@x Float32[]) RETURNS Float32[]
    USING 'file://C:/onnx/m.onnx'
AS BEGIN RETURN infer(@x) END;

-- Relative (resolves to <models-dir>/onnx/m.onnx):
CREATE MODEL m(@x Float32[]) RETURNS Float32[]
    USING 'onnx/m.onnx'
AS BEGIN RETURN infer(@x) END;
```

The file must exist at `CREATE MODEL` time — load is eager. If the file
is missing or the file isn't a valid ONNX model the runtime supports,
the error surfaces immediately rather than at the first call site.

The runtime currently uses ONNX Runtime 1.20.1, which supports IR
versions ≤ 10 and opsets ≤ 21 (for the `ai.onnx` domain). Newer ONNX
exports may need to be re-saved with an older `ir_version` /
`opset_import` to load. A small Python script will do it:

```python
import onnx
m = onnx.load("model.onnx")
m.ir_version = 10
m.opset_import[0].version = 21
onnx.save(m, "model.onnx")
```

## Schema lockdown

CREATE MODEL **always** lands in the `models` schema. Both forms below
register the same `models.classify`:

```sql
CREATE MODEL classify(@x Float32[]) RETURNS Float32[]
    USING 'file://...' AS BEGIN RETURN infer(@x) END;

CREATE MODEL models.classify(@x Float32[]) RETURNS Float32[]
    USING 'file://...' AS BEGIN RETURN infer(@x) END;
```

Any other qualifier is rejected:

```sql
CREATE MODEL public.classify(@x Float32[]) RETURNS Float32[]
    USING 'file://...' AS BEGIN RETURN infer(@x) END;
-- error: CREATE MODEL public.classify: models must live in the
--        'models' schema. Use 'CREATE MODEL classify' or
--        'CREATE MODEL models.classify'.
```

Built-in models also live in `models.X`. The lockdown means every model
in your system is reachable as `models.<name>(...)` regardless of where
it came from.

## `infer()`

`infer(arg)` dispatches the model's bound inference session and returns
the result. It is **only callable from inside a CREATE MODEL body** — a
plan-time gate refuses every other call site:

```sql
SELECT infer(1.0) FROM dual;
-- error: infer() is only valid inside a CREATE [OR REPLACE] MODEL
--        body. ...
```

For discoverability, `datum_catalog.functions.body_scope` records which
functions have this constraint:

```sql
SELECT function_name, body_scope
FROM datum_catalog.functions
WHERE body_scope <> 'none';
-- function_name | body_scope
-- infer         | modelbody
```

### Argument shape

The v1 surface supports **single-input, single-output** ONNX sessions.
The argument's runtime kind must match the session's input tensor's
declared element kind. Three element kinds are wired today:

| Element kind | Scalar accepted | Array accepted |
|---|---|---|
| `Float32` | yes | `Float32[]` |
| `Int32` | yes | `Int32[]` |
| `Int64` | yes | `Int64[]` |

A scalar argument is wrapped into a length-1 tensor; a primitive-array
argument flows through as a 1-d tensor matching the array length. For
sessions with a leading dynamic dim (`[?, 224, 224, 3]` for example),
the dynamic dim absorbs whatever element count the input provides.

### Return shape

The return value matches the session's output tensor's element kind.
Output tensors with a single element surface as a scalar `ValueRef`;
multi-element outputs surface as a primitive array. The body's `RETURN`
coerces the value to the model's declared `RETURNS TYPE` if the kinds
differ.

### What's deferred

- **Multi-input / multi-output** models. The v1 surface is single-input
  only; multi-tensor will likely take a struct-of-tensors argument shape
  (`infer(struct{input_ids: ..., attention_mask: ...})`).
- **Multi-session bundles** (Florence-2, SD). The v1 surface assumes a
  single ONNX file; multi-session will likely take a session-name
  argument (`infer('decoder', struct{...})`).
- **Float16, Bool, UInt8** element kinds (the second is common for
  preprocessing flags; the third is the typical image-tensor kind).
  Each is one new branch when needed.

If you hit one of these limits, the runtime error names the limit
explicitly so you know what's missing.

## Body statements

Inside a `BEGIN … END` block:

| Statement | Description |
|---|---|
| `DECLARE @var TYPE [= expr]` | Declares a local variable, optionally initialised. Without `= expr`, the variable is a typed NULL. |
| `SET @var = expr` | Reassigns an existing variable. |
| `IF predicate stmt [ELSE stmt]` | Conditional branch. Either arm can be a single statement or a `BEGIN … END` block. |
| `WHILE predicate stmt` | Loops while the predicate holds. |
| `BEGIN … END` | Block, primarily useful as the arm of `IF`/`WHILE`. |
| `RETURN expr` | Terminates the body and returns the expression's value. |

These are the same statement shapes [procedural UDFs](udf.md#procedural-udfs)
support, with two restrictions:

- `BREAK`/`CONTINUE` are not supported inside model bodies. Use
  `IF`/`RETURN` to short-circuit.
- A model body cannot contain a `SELECT` or other query statement —
  only direct expressions, `infer()`, and other function calls.

Every control-flow path through the body must end in `RETURN`.

## Shared syntax details

Most modifiers behave exactly the same as on `CREATE FUNCTION`. The
short version:

### OR REPLACE

```sql
CREATE OR REPLACE MODEL classify(@x Float32[]) RETURNS Float32[]
    USING 'file:///opt/models/v2.onnx'
AS BEGIN RETURN infer(@x) END;
```

Re-loads the new model file, registers the new descriptor, and disposes
the previously-bound session. In-flight queries that already captured
a reference to the old descriptor keep running on the old session
until they finish.

### IF NOT EXISTS

`CREATE MODEL IF NOT EXISTS` is a no-op when a model with that name
already exists. The original definition wins; the new file is **not**
loaded.

### IS NOT NULL on parameters

Append `IS NOT NULL` to a parameter to require a non-null argument. A
NULL at the call site throws an error before the body runs.

```sql
CREATE MODEL classify(@x Float32[] IS NOT NULL) RETURNS Float32[]
    USING 'file:///opt/models/classify.onnx'
AS BEGIN RETURN infer(@x) END;
```

### Default parameter values

A parameter declared with `= expr` becomes optional at the call site.
Defaults must sit at the tail of the parameter list (same rule as UDFs).

```sql
CREATE MODEL classify(@x Float32[], @threshold Float32 = 0.5)
    RETURNS Float32[]
    USING 'file://...'
AS BEGIN RETURN infer(@x) END;
```

### DROP MODEL

```sql
DROP MODEL classify;
DROP MODEL IF EXISTS classify;
DROP MODEL models.classify;          -- explicit schema, equivalent
```

Dropping a model unregisters the descriptor, removes the scalar adapter
(so subsequent `models.classify(...)` calls fail with "not registered"),
and disposes the bound session. `DROP MODEL` rejects any non-`models`
schema qualifier with the same lockdown error as `CREATE MODEL`.

## Calling a model

A registered model is callable from any query as
`models.<name>(arg, ...)`. The argument count must match the declared
arity (or fall within the `min–max` range when defaults are present).

```sql
SELECT models.classify(image)            FROM photos;
SELECT models.classify(image, 0.7)       FROM photos;        -- override default
SELECT models.classify(image, 0.7) AS cls FROM photos LIMIT 10;
```

Because the body is procedural and `infer()` is non-deterministic from
the planner's perspective (an inference call can have subtle scheduler
non-determinism), the engine treats each call site as its own
evaluation — there is no CSE of `models.X(col)` across two call sites
in the same query. If you need to evaluate once and reuse, bind via
[`LET`](let-bindings.md):

```sql
SELECT classification, classification[0] AS top
FROM (
    SELECT models.classify(image) AS classification FROM photos
);
```

## Performance

SQL-defined models currently dispatch **one row at a time** through the
scalar pipeline. The engine's batched-dispatch path
(`ModelInvocationOperator`) is wired only for engine-baked built-in
models — see [`design-docs/onnx-inference.md`](../design-docs/onnx-inference.md#layer-4-the-hoister-option-a--sql-defined-models-bypass-mio)
for the rationale. For most models the per-row overhead is dominated by
the inference cost itself, so the practical impact is small. For very
cheap models (fast classifiers), batched dispatch can move the needle —
the platform will grow that path when there's measured evidence to
justify it.

## Persistence

SQL-defined models are **not** persisted across process restarts in v1.
The bound `IInferenceSession` instances hold native handles that would
need a re-load on rehydrate; rather than carry that complexity now,
models stay process-scoped. Re-issue `CREATE MODEL` after a restart.

## Introspection

The `system.models` virtual table surfaces every registered model — both
engine built-ins and your `CREATE MODEL` declarations:

```sql
SELECT name, kind, backend, file_size_bytes, status
FROM system.models
ORDER BY kind, name;
```

The schema-stable origin discriminator is the **`kind`** column:

| `kind`       | Meaning |
|--------------|---------|
| `builtin`    | Engine-baked entry from the model catalog (e.g. MobileNetV2, Llama, YOLOX). See [Models](../models.md). |
| `declared`   | A user-written `CREATE MODEL` registration. |

For declared rows, the metadata-rich columns (`license`,
`license_holder`, `category`, `modalities`, `source_url`,
`display_name`, `parameters`) are NULL — the engine has no upstream
metadata to surface. The columns that *are* populated for declared
rows: `name`, `backend = "sql"`, `file_name` (the raw `USING` path),
`file_size_bytes` (stat the resolved path), `status` (always
`"available"` because the session is bound at CREATE-MODEL time).

## Limitations

- **Single ONNX file.** Multi-session bundles (text-encoder + UNet + VAE,
  vision-language pipelines) are not yet supported through `CREATE
  MODEL`. Built-ins handle multi-session today; the SQL surface inherits
  it when there's a first multi-session user.
- **Single input + single output tensor.** `infer()` rejects sessions
  with more than one input or output. Multi-tensor support is mechanical
  but waits for a real model that needs it.
- **Element kinds Float32 / Int32 / Int64 only.** Float16, Bool, UInt8,
  and the rest are runtime errors today; each is one branch to add.
- **No persistence.** Re-issue `CREATE MODEL` after process restart.
- **No CSE / batched dispatch for SQL-defined models.** Each call site
  evaluates per row. See [Performance](#performance).
- **Schema is fixed.** Models always live under `models.X` — no
  user-chosen schema.
- **Body restrictions.** No `BREAK`/`CONTINUE`; no nested `SELECT`.

## See Also

- [User-Defined Functions](udf.md) — the closest sibling DDL surface; same body shape, no `infer()` and no schema lockdown.
- [Procedural Functions and Procedures](procedural.md) — the broader procedural-body family this shares syntax with.
- [Models](../models.md) — the engine's built-in model zoo (`kind = 'builtin'` rows in `system.models`).
- [Schema Introspection](schema-introspection.md) — how `system.models` and `datum_catalog.functions` fit into the broader catalog surface.
- [`design-docs/onnx-inference.md`](../design-docs/onnx-inference.md) — the design rationale for the inference layer, including the hoister Option A trade-off and why `infer()` is body-scoped.
