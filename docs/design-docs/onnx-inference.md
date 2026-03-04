# ONNX inference + SQL-defined models — design doc

## Overview

DatumIngest exposes ONNX (and, in time, OpenVINO and other) inference through two layered surfaces:

1. A **backend-agnostic inference abstraction** (`src/DatumIngest/Inference/`) that decouples the engine from any one runtime. Sessions, tensor bags, and dispatchers are runtime-neutral; ORT is the first concrete backend.
2. A **SQL-driven model surface** (`CREATE MODEL ... USING '...' AS BEGIN ... END`, the `infer()` scalar) that lets users define and call models without writing C#. SQL-defined models live alongside engine-baked built-ins in the `models.X` namespace.

This doc covers the inference layer's architecture, the CREATE MODEL DDL pipeline, the scalar-dispatch path, the `infer()` runtime bridge, the body-scope mechanism, and the introspection surface. It is contributor-facing — design rationale, file references, gotchas — not a user tutorial.

For built-in model integrations (MobileNetV2, YOLOX, Florence-2, the LLM family, etc.) see [`docs/models.md`](../models.md). This doc is about the *infrastructure* those built-ins ride on, plus the SQL surface that lets users add their own.

---

## Layer 1: the inference abstraction

**Location**: `src/DatumIngest/Inference/`

The abstraction is shaped around three interfaces:

| Type | Role |
|---|---|
| `IInferenceBackend` | One inference runtime (e.g. ONNX Runtime). Discovers available devices, inspects bundles for support, loads sessions. |
| `IInferenceSession` | One loaded model on one device. Stateless across calls; pure function from input tensors to output tensors plus implicit weights. |
| `IInferenceDispatcher` | Policy layer between SQL CREATE MODEL and the backends. Picks one (backend, device) target per bundle based on declared preferences and available hardware. |

Supporting types:

- **`TensorBag`** (abstract) — a named collection of `IInferenceTensor`s, owned by the session. Backend-aware allocator so adds produce native buffers without managed-to-native copies.
- **`IInferenceTensor`** — backend-agnostic handle wrapping the runtime's native type (`OrtValue`, `ov::Tensor`). `AsSpan<T>()` reads the bytes flat in row-major order; downloads from device memory on first call for non-CPU tensors.
- **`TensorSpec`** — name + element kind + dynamic-aware shape (`int?[]` where `null` = dynamic dim). Returned from `IInferenceSession.Inputs/Outputs` so callers validate at SQL-MODEL load time, not per row.
- **`BundleManifest`** — minimal "what does this bundle look like" record: session-name → file-path map, preferred backends, opset/required-ops hints, declared resident bytes. The richer applet-level bundle format lives one layer up.

### Why separate from `Models/Onnx/OnnxSessionFactory`

The legacy factory still exists; every existing built-in `OnnxModel` subclass (Moondream2, Florence2, MobileNetV2, etc.) loads through it. The factory and the new backend coexist intentionally — every consumer migrates one at a time. Both contain the same CUDA-probe logic; keep them in sync until the factory retires.

### ORT backend (`src/DatumIngest/Inference/OnnxRuntime/`)

- **`OnnxRuntimeBackend`** — lazy probe of CPU / CUDA / DirectML / CoreML EPs via `CudaRuntimeProbe.EnsureOnPath()`. OS-gated probes (DML only Windows, CoreML only macOS). `ResolveResidentBytes` enforces a "≥ file size on disk" floor so an obviously-misconfigured `DeclaredResidentBytes` is rejected.
- **`OnnxRuntimeSession`** — wraps `Microsoft.ML.OnnxRuntime.InferenceSession`. Sync `Run` is wrapped in `Task.Run` because ORT's async path requires pre-allocated outputs, which is impractical for the dynamic-shape cases the engine wants to support.
- **`OnnxRuntimeTensor`** — wraps `OrtValue`. CPU-resident in v1; the IO-binding path (keeping outputs on-device for chained inference) stays open via `IsResidentOnCpu` returning `false` in a future revision, but isn't wired today.
- **`OnnxRuntimeTensorBag`** — backed by `OrtAllocator.DefaultInstance + CreateAllocatedTensorValue`; copies via `GetTensorMutableRawData`.
- **`OnnxElementTypes`** — bidirectional `DataKind ↔ TensorElementType` map. The contract is "ORT supports it ↔ a `DataKind` value exists for it."

### Dispatcher (`src/DatumIngest/Inference/InferenceDispatcher.cs`)

- Single (backend, device) target per bundle. Multi-session bundles (Florence-2 with four ONNX files; SD with text-encoder + UNet + VAE) run all sessions on the same backend + device — mixing devices within a bundle would force constant host↔device transfers on every cross-session call.
- Backend selection: `ForcedBackend → BundleManifest.PreferredBackends → registered order`.
- Three pre-computed device rankings per `PowerPreference` (Performance / Balanced / Power-Saver).
- Pre-load file-existence check + partial-load cleanup on mid-bundle failure.

---

## Layer 2: CREATE MODEL DDL

The DDL surface lets users register a model from SQL:

```sql
CREATE [OR REPLACE] MODEL [IF NOT EXISTS] name(arg TYPE [, ...])
    RETURNS T [IS NOT NULL]
    USING 'file://...path.onnx'
    [AS] BEGIN
        ... procedural body ...
    END
```

The body is always procedural — the only legal use of a model body is to call `infer()`, which requires a procedural context to bind to. There's no expression-body form.

### Parser (`src/DatumIngest.Parsing/SqlParser.cs`)

- `CreateModelStatement` and `DropModelStatement` AST nodes (in `Ast/AstNodes.cs`).
- `CreateModelParser` and `DropModelParser` combinators, registered in `SingleStatementParser`'s `.Or()` chain.
- **`MODEL` is a contextual identifier**, not a hard keyword. The parser consumes it via `Token.EqualTo(SqlToken.Identifier).Where(t => t.Text.EqualsIgnoreCase("MODEL"))` — same pattern as `USING` for `CREATE INDEX`. This is load-bearing: the codebase + user schemas freely use `model` as a column or table name (`CREATE TABLE model`, `JOIN model AS m`). A first-pass attempt to make MODEL a reserved keyword broke `UpdateValidationTests` and `DdlParsingTests` and the user's own `INSERT INTO model` statements within an hour. The contextual parse is the correct posture.
- `USING` is also a contextual identifier (same pattern; same precedent — `CREATE INDEX … USING`).
- Body validation reuses `ValidateProceduralBody` — the same rules as procedural UDFs.

### Storage (`src/DatumIngest/Catalog/ModelRegistry.cs`)

- **`ModelDescriptor`** record mirrors `UdfDescriptor` plus two additions: `UsingPath` (raw, unresolved) and `BoundSessions: IReadOnlyDictionary<string, IInferenceSession>`. `ReturnTypeName` is non-null (required for models). `QualifiedName` is always `(models, name)`.
- **`ModelRegistry`** parallels `UdfRegistry`. `Register` returns the displaced descriptor for disposal; `Unregister` returns the removed descriptor likewise. Process-scoped, not persisted (sessions hold native handles; rehydrate cost deferred).
- **`TableCatalog.DeclaredModels`** exposes the registry; **`TableCatalog.InferenceDispatcher`** is the nullable, lazy-set dispatcher property.

### Registrar (`src/DatumIngest/Catalog/RoutineRegistrar.cs`, `ApplyCreateModelAsync`)

Steps, in order:

1. Validate parameter defaults are contiguous at the tail.
2. **Schema lockdown**. Explicit qualifiers must be `models` (case-insensitive) or absent. Any other qualifier throws with a clear error pointing at the legal forms.
3. Confirm `_catalog.InferenceDispatcher` is wired; throw "no dispatcher configured" otherwise.
4. `IF NOT EXISTS` early return on conflict.
5. Resolve the `USING` path: `file://...` → strip prefix (absolute); else → `Path.Combine(Models.ModelDirectory, ...)`. Throw `FileNotFoundException` if the file isn't on disk.
6. Build a single-session `BundleManifest` (multi-session bundles land when the first multi-session built-in needs them).
7. Eager-load via `dispatcher.LoadBundleAsync` — failures surface at CREATE-time, not at the first call site.
8. Build the descriptor; register in `DeclaredModels`; register the scalar adapter (next section).
9. `OR REPLACE`: dispose the displaced descriptor's sessions *after* the new one is in place. In-flight queries holding a reference to the displaced descriptor keep running on the now-disposed sessions until they finish — same shape as OR REPLACE for UDFs.

`ApplyDropModel` is the symmetric mirror: schema lockdown → `IF EXISTS` early return → unregister from `DeclaredModels` → unregister adapter from `_functions` → dispose sessions.

### Persistence

SQL-defined models are **not** persisted across process restarts in v1. Bound `IInferenceSession`s hold native handles that would need a re-load on rehydrate. Users re-issue `CREATE MODEL` after a restart. When the residency manager (currently engine-internal) gets a re-hydrate path, this changes.

---

## Layer 3: scalar adapter

**Location**: `src/DatumIngest/Functions/ProceduralModelFunction.cs`

Once a `ModelDescriptor` is in `DeclaredModels`, queries need to be able to *call* it. The adapter is an `IScalarFunction` registered in `FunctionRegistry` under the model's qualified name (always `models.X`):

- `ValidateArguments` checks arity against `descriptor.Parameters` and returns the declared return kind.
- `ExecuteAsync` runs the procedural body via the same interpreter `ProceduralUdfFunction` uses, with one differentiating tweak: the body frame is built via `EvaluationFrame.WithCurrentModel(_descriptor)` so the body's `infer()` calls can pull their bound `IInferenceSession` from `frame.CurrentModel`. The body frame also inherits `frame.Types` so struct literals inside the body (`{input_ids: ids, attention_mask: mask}`) intern shapes into the caller's per-query `TypeRegistry`.
- Cycle detection uses a separate `AsyncLocal<Stack<string>>` from UDFs — models can call UDFs and UDFs can call models without artificially tripping each other's cycle guards; cycles are only detected within each routine class.
- `IsPure` is always `false` — model invocations are expensive and may exhibit non-determinism (sampling LLMs, cuDNN scheduler differences). The CSE pass treats each call site as its own evaluation.

---

## Layer 4: the hoister — three-way dispatch

**Location**: `src/DatumIngest/Execution/ModelInvocationHoister.cs`, `src/DatumIngest/Execution/ModelBodyLowerer.cs`, `src/DatumIngest/Models/ProceduralModelAdapter.cs`

The planner lifts every `models.X(...)` call out of expressions into a dedicated `ModelInvocationOperator` (MIO) node — batched dispatch, residency leases, output-struct shape stamping. The post-pass `ModelBodyLowerer` then either keeps MIO in place or rewrites it into a column pipeline of `ProjectOperator` + `InferOperator` nodes. Three resulting paths:

| Source shape | Hoister output | Lowerer post-pass | Result |
|---|---|---|---|
| Built-in `IModel` | MIO | leave (no descriptor in `DeclaredModels`) | MIO dispatches via `IModel.InferBatchAsync` with full batching + residency + tracer + RowLimit + streaming sink + `PreferredBatchSize` sub-batching. |
| SQL-defined, **straight-line** body | MIO targeting `ProceduralModelAdapter` | rewrite to `Project(__decl_n = expr) → InferOperator → Project(__return = expr)` chain | Real batched `Session.Run` packing when shapes line up. This is the primary path for SQL-defined detectors. |
| SQL-defined, **branchy** body (`IF`/`WHILE`/`SET`-after-`DECLARE`/struct DECLARE/multi-input `infer`) | MIO targeting `ProceduralModelAdapter` | leave | Adapter runs the procedural body per row inside `InferBatchAsync`, gets operator-boundary parity (tracer, residency lease, etc.) without unifying with the column pipeline. |

`ModelBodyLowerer.BodyIsStraightLine` is the gating predicate. It rejects:

- DECLARE with no initializer (typed-null DECLAREs).
- Non-DECLARE / non-RETURN statements (IF/WHILE/SET/CALL/BLOCK/BREAK/CONTINUE/mid-body RETURN).
- DECLAREs whose declared type annotation starts with `Struct` (cheap textual check). Struct-typed locals can be referenced by name in downstream expressions where the lowerer would need to thread struct metadata through column refs — easier to bail than to extend `InferOperator` to unpack struct columns.
- Any `infer()` call with a struct-literal argument anywhere in its argument list. These signal multi-input dispatch, which the per-row scalar `InferFunction` path handles natively via `ExecuteMultiInputAsync` — the lowerer routes through MIO + adapter instead of trying to teach `InferOperator` multi-input.

The fourth residual path (Option A — scalar `ProceduralModelFunction` with no MIO at all) survives for **non-hoisted call sites**: `models.X(...)` inside a UDF body, inside an unhoisted clause, etc. Same per-row scalar adapter as before, no operator-boundary features. The hoister prefers MIO (and then the lowerer prefers Option D) for top-level `SELECT` call sites.

Active follow-up list with effort estimates lives in [batched-SQL-defined-models memo](../../memory/project_batched_sql_defined_models.md). Read that before extending any of these paths.

---

## Layer 5: `infer()` — the runtime bridge

**Location**: `src/DatumIngest/Functions/InferFunction.cs`

Once the body is executing, the user's `RETURN infer(x)` needs to actually dispatch to the bound session. `infer()` is the bridge:

1. Pull `frame.CurrentModel` (set by `ProceduralModelFunction`'s body frame). Throw if absent — the runtime guard.
2. Resolve `model.BoundSessions["default"]`. Multi-session lookup (`infer('session-name', struct)`) is deferred.
3. **Multi-input dispatch.** If the first argument is a `Struct` value, route to `ExecuteMultiInputAsync` — looks up the inputs struct's `TypeDescriptor` in `frame.Types`, matches each session input by case-insensitive field name, and feeds the tensor bag. Optional 2nd struct argument carries per-input explicit shapes for sessions whose inputs have ≥2 dynamic dims (BERT family — every input is `[batch, seq_len]`).
4. **Single-input dispatch.** Single-input sessions accept scalar or primitive-array args directly, optionally with an Int32-array shape override.
5. Marshal the argument(s) into a `TensorBag`:
   - `session.CreateInputBag()` for backend-allocated tensor storage.
   - Element kind dispatched on `TensorSpec.ElementKind`: Float32 / Int32 / Int64 in v1. Adding a kind = one new branch in `AddInputTensor` + `ReadOutputTensor`.
   - Scalar arg → length-1 tensor; primitive-array arg → 1-d tensor matching array length.
6. `await session.RunAsync(bag, cancellationToken)`.
7. Read the **first** output tensor via `AsSpan<T>()`; if shape product is 1 surface as a scalar `ValueRef`, otherwise `FromPrimitiveArray`. Multi-output sessions are accepted; the convention HuggingFace optimum and most transformer-to-ONNX exporters follow is to list the primary output first (e.g. `last_hidden_state` ahead of `pooler_output` for BERT). Struct-of-tensors return shape for multi-output is a follow-up.
8. Return — the body's `RETURN` coercion handles any kind mismatch with the declared model return type.

`IsPure = false` (same reasoning as `ProceduralModelFunction`). Return type rule is `SameAs(0)` — a placeholder; the actual output kind comes from the runtime tensor and is coerced at the body's return boundary.

### Signature variants

Three `FunctionSignatureVariant` entries, matched in order so the most specific shape wins:

| # | Parameters | Use case |
|---|---|---|
| 1 | `(inputs: Struct, shapes: Struct)` | Multi-input with explicit per-input shapes — required for BERT-family encoders. |
| 2 | `(value, shape: Int<array>)` | Single-input with explicit shape — required when the session has ≥2 dynamic dims. |
| 3 | `(value)` | Single-input or multi-input (struct) with shapes resolved from the session spec. |

### Shape solver

Input tensors with dynamic dims (`null` entries in `TensorSpec.Shape`) are handled by `ResolveShape` for the no-explicit-shape path:

- 0 dynamic dims → use the declared shape.
- 1 dynamic dim → it absorbs the element count after dividing out the fixed dims (most common case: a sole dynamic batch dim).
- ≥2 dynamic dims → throws with a clear error pointing at the 2-arg `infer(value, shape)` form (or the struct-of-shapes form for multi-input).

### TypeRegistry threading

`IModel.InferBatchAsync` has two overloads:

- The original `(inputs, overrides, ct)` — kept as a default interface method for backward compat.
- `(inputs, overrides, types: TypeRegistry?, ct)` — the path MIO and `ModelScalarFunction` call. `types` is the caller's per-query `TypeRegistry`. Models that build dynamic-shape struct outputs (`ProceduralModelAdapter` wrapping a SQL-defined body) intern result shapes into this registry so the caller's `ToDataValue` / struct-field-access paths can resolve the stamped TypeIds without cross-registry translation.

This shape replaces an earlier design where `ProceduralModelAdapter` created its own per-batch `TypeRegistry` — that worked for evaluating the body but failed at the MIO scatter boundary: the body's TypeIds were meaningless in `context.Types`, and downstream consumers (struct field projection, `system.tables`) saw `f0..fN` instead of declared field names.

### Field-name preservation in `BuildStructArray`

`ValueRef.ToDataValue`'s array-of-struct path (`BuildStructArray`) accepts the *array* descriptor's TypeId, hops to its `ElementTypeId`, and stamps every slot's reserved bytes with the per-element struct TypeId via `FromStructArray`. The inline array carrier's `_inline.TypeId` returns 0 by design (the `TypeId` getter is gated to arena-backed struct arrays for layout reasons), so dynamic-shape producers can't stamp the array carrier directly. **Fallback**: when no Array<Struct> TypeId is supplied AND the array has ≥1 element, `BuildStructArray` reads the first element's struct TypeId off `elements[0]._inline.TypeId` and stamps every slot from that. Empty arrays from no-OutputFields producers lose field-name info — fine since there are no rows to render — but the load-bearing common case (PP-OCR-det returning `Array<Struct{label, score, x, y, w, h}>` via `dbnet_postprocess`) round-trips correctly.

---

## Layer 6: body-scope mechanism

`infer()` is meaningful only inside a CREATE MODEL body. Calling it from a regular query was originally a runtime error ("no CurrentModel frame") — informative but late and cryptic. The body-scope mechanism upgrades that to plan-time refusal + completion exclusion.

Three pieces:

### Metadata flag

`BodyScopeRequirement { None, ModelBody }` declared on `IFunction` as a `static virtual` property (default `None`). `FunctionRegistry.RegisterScalar<T>` reads `T.BodyScope` into the descriptor. `InferFunction` declares `BodyScope = ModelBody`.

### Plan-time gate

[`BodyScopeGate`](../../src/DatumIngest/Execution/BodyScopeGate.cs) — a dedicated AST walker invoked at the top of `QueryPlanner.Plan(QueryExpression)`. Walks every reachable `FunctionCallExpression` (SELECT / WHERE / Having / Qualify / OrderBy / GroupBy / LET / Joins / From / SubqueryExpression / Insert subqueries) and throws on the first body-scoped call.

The gate is **unconditional** — no "am I in a model body?" context flag — because the planner is only ever entered for top-level queries. Model bodies are interpreted by `ProceduralModelFunction` directly and never reach `QueryPlanner`. So a positive match for `BodyScopeRequirement.ModelBody` here is always wrong, and the error message is unconditional.

A dedicated walker (vs. a check inside `ExpressionTypeResolver` or the runtime evaluator) is the right placement because:

- The planner doesn't itself resolve scalar functions — it builds operators that keep `FunctionCallExpression` AST nodes intact and the runtime evaluator looks them up per-row.
- `ExpressionTypeResolver` is a semantic-analysis surface used by the language server, not by the planner.
- The runtime evaluator fires per row, too late for the "before any rows are scanned" UX.

### Defense-in-depth runtime guard

`InferFunction.ExecuteAsync` still checks `frame.CurrentModel is null` and throws if so. Anything that bypasses planning (procedural UDF bodies, future programmatic surfaces) hits a hard floor here.

### Manifest exclusion

`CatalogManifestBuilder.Build` skips body-scoped descriptors entirely — `infer()` doesn't appear in the language-server manifest, so the editor doesn't suggest it in completion / hover / signature-help in any context. Discoverability stays via introspection (next section). A future Tier 2 could re-include body-scoped functions only when the cursor is inside a model body, but this requires a new `InsideModelBody` completion zone detected from surrounding tokens.

---

## Layer 7: introspection

Two virtual tables surface the model + function surface for users:

### `system.models`

[`ModelsTableProvider`](../../src/DatumIngest/Catalog/Providers/ModelsTableProvider.cs) merges built-ins (`ModelCatalog`) and SQL-defined models (`TableCatalog.DeclaredModels`) into one name-sorted view. 14 columns; the schema-stable origin discriminator is **`kind`** with values `builtin` / `declared`.

Mirrors the codebase's internal naming (`Models` vs `DeclaredModels`) and Postgres's `pg_proc.prokind`. Column placement is at position 13 (after `status`) so existing positional consumers keep working.

For declared rows, most metadata columns (license, modalities, source_url, etc.) surface as `NULL` — declared models don't carry those. Populated fields are `name`, `backend = "sql"`, `file_name` (raw `USING` path), `file_size_bytes` (stat the resolved path), `status = "available"` (the session is bound; if the file vanished post-load, the in-memory session still works).

### `datum_catalog.functions`

[`DatumCatalogFunctionsProvider`](../../src/DatumIngest/Catalog/Providers/DatumCatalogProviders.cs) already existed for the general function catalog. Phase 3c added a **`body_scope`** column (NOT NULL, `none` / `modelbody`). **Annotate-not-hide**: body-scoped functions still appear in this view so users can discover them via `WHERE body_scope = 'modelbody'`; the plan-time gate refuses out-of-context call sites separately.

The future `system.functions` (when `datum_catalog.*` consolidates) inherits the column for free.

---

## Open follow-ups

| Item | Where filed | Notes |
|---|---|---|
| Batched dispatch for SQL-defined models (Option B/C) | [`memory/project_batched_sql_defined_models.md`](../../memory/project_batched_sql_defined_models.md) | Two design paths sketched; pick when there's measured throughput evidence. |
| Inference toolkit (`onnx_inspect`, `inference_devices`, preprocessing helpers) | [`plans/inference-toolkit.md`](../../plans/inference-toolkit.md) | Tier 1 is load-bearing for the model-zoo / Kaggle workflow story. |
| Bundle signatures (SHA-256 verification) | [`plans/bundle-signatures.md`](../../plans/bundle-signatures.md) | New `WITH (sha256 = '...')` CREATE MODEL clause; three new `system.models` columns. |
| ORT version bump | This doc, project memo | Currently 1.20.1 (IR ≤ 10, opset ≤ 21). Newer ONNX exports hit the cap. |
| Body-scope Tier 2 completion (context-aware suggestions) | This doc | Show `infer()` in completion only when cursor is in a model body. Wants new `InsideModelBody` zone. |
| Multi-output `infer()` (struct-of-tensors return) | This doc | Multi-input via struct argument **shipped 2026-05-17**; multi-output (returning a struct of tensors, e.g. BERT's `{last_hidden_state, pooler_output}`) is still deferred. v1 picks the first output. Multi-session bundles + `infer('session-name', struct)` deferred until first multi-session user. |
| More element kinds in `infer()` | This doc | Float16, Bool, UInt8 (image tensors). Each is a new branch in `AddInputTensor` + `ReadOutputTensor`. |
| Residency-manager integration | This doc | Intercept session disposal to cache warm sessions; per-machine measured-bytes cache; VRAM budget cap. |
| OpenVINO backend | This doc | Same interfaces as ORT backend; lift via `Intel.OpenVINO` NuGet. |
| IO binding | This doc | Math says single-stage <2% copy cost; chained 2-8%. Revisit after Florence-2 profile. |

## See also

- [`docs/models.md`](../models.md) — built-in model zoo reference (the integrations that ride on this infrastructure).
- [`docs/architecture.md`](../architecture.md) — high-level engine architecture.
- [`docs/sql/procedural.md`](../sql/procedural.md) — procedural body semantics shared with UDFs.
- [`docs/sql/ddl-dml.md`](../sql/ddl-dml.md) — DDL surface (CREATE TABLE / FUNCTION / PROCEDURE / MODEL).
