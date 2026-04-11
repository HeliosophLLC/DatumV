---
title: Planner-Time Inline-Metadata Accessor Elision
---

Inline-metadata accessor elision is a plan-time pass that recognises calls to scalar functions whose body is a direct payload-byte read on the argument's [`DataValue`](data-value-layout.md), and rewrites them into a dedicated AST node that the evaluator handles without going through `IScalarFunction.ExecuteAsync`. The rewrite eliminates per-call dispatch overhead — `ValueTask` allocation, the `ArrayPool` argument rent, function-by-name lookup, and the per-call activity span — on the common case where the metadata is cached as a struct field.

The pass is implemented by [`InlineAccessorElider`](../../src/DatumIngest/Execution/InlineAccessorElider.cs) and runs during [`QueryPlanner.Finalize`](../../src/DatumIngest/Execution/QueryPlanner.cs), between [`ImageMetadataLowerer`](../../src/DatumIngest/Execution/ImageMetadataLowerer.cs) and [`CommonSubexpressionEliminator`](common-subexpression-elimination.md). The upstream lowerer rewrites composite image-metadata calls (`pixel_count`, `dimensions(img, literal)`) into compositions of the elidable accessors below, so a single planner pipeline turns both the primitive accessors and their composites into struct-byte reads.

## Why It's Worth Doing

A function like `image_width(img)` looks like a heavy call (decoding image bytes to read pixel dimensions) but its body is two struct-field reads — the value's `_kind` for a guard, then a `ushort` slice of its `_p4` payload word. The dispatch shell costs more than the work: each invocation pays for argument materialisation through an `ArrayPool<ValueRef>`-rented buffer, a `ValueTask<ValueRef>` allocation (when the function is genuinely async), a registry lookup by name, and a per-call `ActivitySource` span.

Eliding the dispatch turns the call into a single synchronous switch case in the evaluator — the argument's already a `ValueRef`, the read is one byte off its inline `DataValue` carrier, and the result `ValueRef` is constructed on the stack. The user writes `image_width(img)` and the engine treats it as essentially free.

The substrate this elision rides on — per-kind inline metadata stamped at construction time — is documented in [Value Representation](value-representation.md) and the byte map in [Data Value Layout](data-value-layout.md).

## Eligibility

A function is eligible for elision when it implements [`IInlineMetadataAccessor`](../../src/DatumIngest/Functions/IInlineMetadataAccessor.cs). The marker is a one-property interface — `InlineAccessorField Field { get; }` — that names which inline-metadata field the function reads. Implementing it is an opt-in choice by the function author: the function continues to ship its original `ExecuteAsync` body, which the evaluator delegates back to whenever the inline metadata isn't available (the **fallback** path described below).

For a call site to be rewritten, all of the following must hold:

- The function resolved through the live `FunctionRegistry` (honouring the catalog search path) implements `IInlineMetadataAccessor`. A user UDF shadowing a built-in accessor's bare name does not get elided.
- The call has exactly one argument and no modifier clauses (no `DISTINCT`, no `ORDER BY`, no `WITHIN GROUP`). Today's accessors are all unary; the gate is defensive.

The resolved instance's `Field` value identifies which inline-metadata field to read; the [`InlineAccessorDescriptors`](../../src/DatumIngest/Execution/InlineAccessorField.cs) static table maps each field to the expected argument kind, the result kind, and the canonical SQL function name used to recover the fallback function at evaluation time.

## Currently Elided Functions

| Function | Kind | Field source | Fallback |
|---|---|---|---|
| `image_width(img)` | Image | `_p4` low 16 bits | SkiaSharp decode |
| `image_height(img)` | Image | `_p4` high 16 bits | SkiaSharp decode |
| `image_channels(img)` | Image | `_p5` low byte | SkiaSharp decode (`BytesPerPixel`) |
| `audio_sample_rate(a)` | Audio | `_p4` | NULL (no decode fallback today) |
| `video_width(v)` | Video | `_p4` low 16 bits | NULL (no decode fallback today) |
| `video_height(v)` | Video | `_p4` high 16 bits | NULL (no decode fallback today) |
| `point_cloud_count(pc)` | PointCloud | `_p4` | Blob-header read |
| `point_cloud_has_color(pc)` | PointCloud | `_p5` low byte, `HasColor` bit | Blob-header read |
| `mesh_vertex_count(m)` | Mesh | `_p4` | Blob-header read |
| `mesh_triangle_count(m)` | Mesh | `_p5` | Blob-header read |
| `length(s)` | String | `_charCount` high byte (inline only) | `Rune` walk over materialised string |
| `octet_length(s)` | String | `_charCount` low byte (inline only) | `Encoding.UTF8.GetByteCount(string)` |

## How It Works

The rewrite replaces a `FunctionCallExpression` with a sealed-record [`InlineAccessorExpression(Expression Argument, InlineAccessorField Field)`](../../src/DatumIngest/Execution/InlineAccessorExpression.cs). Record equality on `(Argument, Field)` is what lets the downstream CSE pass deduplicate repeated accessor calls without extra machinery.

### Planner Pass

[`InlineAccessorElider.Elide(plan, registry, searchPath)`](../../src/DatumIngest/Execution/InlineAccessorElider.cs) walks every operator's expression tree via `QueryOperator.RewriteExpressions`. The walk is children-first (so an accessor nested inside arithmetic gets rewritten alongside the outer expression), and it matches `FunctionCallExpression` whose resolved function implements `IInlineMetadataAccessor`. The pass is wired into `QueryPlanner.Finalize`:

```
ModelInvocationHoister.Hoist(plan, modelCatalog)
  ↓
ModelBodyLowerer.LowerSqlDefinedBodies(plan, declaredModels)
  ↓
ImageMetadataLowerer.Lower(plan, functionRegistry, searchPath)
  ↓
InlineAccessorElider.Elide(plan, functionRegistry, searchPath)   ← here
  ↓
CommonSubexpressionEliminator.Eliminate(plan, functionRegistry)
```

Elision runs **before** CSE so duplicate accessor calls in `WHERE` + `SELECT` + `ORDER BY` collapse onto the rewritten node's record-equal form.

### Evaluator Fast Path

At evaluation time, the evaluator's expression switch handles `InlineAccessorExpression` directly:

1. Evaluate the argument as a `ValueRef`.
2. If null, return a typed null of the result kind.
3. Look at the argument's inline `DataValue` carrier and read the bytes for the named field.
4. If the read produces a non-sentinel value, return it as a `ValueRef`.
5. **Otherwise fall back**: look up the original function by name (descriptor table → registry), rent a single-element `ValueRef[]` from `ArrayPool`, dispatch through `ExecuteAsync` exactly as the pre-elision call would have, and return its result.

The fallback preserves correctness bit-for-bit when the inline metadata isn't available. Three reasons it might not be:

- **Unstamped zero sentinel.** The producing path didn't populate the field. Common for legacy values, model outputs that bypass the central `*DataValueFactory` helpers, or audio/video formats the header parser doesn't recognise.
- **Wrong tier.** For string accessors, only the inline tier carries a usable `_charCount`. Arena-backed strings stamp it at construction, but the `ValueRef` the evaluator sees has lost that information (non-inline strings reach the evaluator as a materialised `string`).
- **Wrong kind.** A type-mismatched argument (e.g. passing a String to `image_width`) reads as zero and falls through to the function's own error path.

## CSE Interaction

`InlineAccessorExpression` is structurally equatable, so two `image_width(t.img)` calls in `WHERE` and `SELECT` collapse to a single hoisted enrichment via the same machinery described in [Common Subexpression Elimination](common-subexpression-elimination.md). The fingerprint in `QueryExplainer.Fingerprint` renders an elided node as its canonical function form (e.g. `image_width(img)`), so EXPLAIN reads naturally and the dedup key remains stable across the rewrite.

## Examples

### Single elidable call

```sql
SELECT image_width(img) AS w FROM images
```

Plans to a `ProjectOperator` whose column expression is an `InlineAccessorExpression(ColumnReference("img"), ImageWidth)`. No `IScalarFunction.ExecuteAsync` is invoked on the stamped path.

### Nested under arithmetic

```sql
SELECT image_width(img) / 2 AS half FROM images
```

The elider's children-first walk rewrites the inner accessor before considering the outer binary expression. The result is `BinaryExpression(InlineAccessor(img, ImageWidth), Divide, LiteralExpression(2))`.

### Cross-clause dedup via CSE

```sql
SELECT image_width(img) AS w
FROM images
WHERE image_width(img) > 100
```

Both call sites elide first; CSE then collapses them onto a shared `RowEnricherOperator` placed upstream of the filter. The filter and the projection both read a hidden column instead of evaluating the accessor twice.

### Unstamped fallback

```sql
-- img produced by a custom UDF that didn't route through ImageDataValueFactory
SELECT image_width(img) FROM model_outputs
```

The elider still fires — it's a plan-time decision based on the function, not the runtime data. At evaluation time, the inline `_p4` reads as 0; the evaluator delegates to `ImageWidthFunction.ExecuteAsync`, which decodes the image bytes via SkiaSharp and returns the real width. Identical observable result, slightly slower (decode cost), and the function-body fallback is the same code path it would have used with no elider.

## Adding a New Elidable Function

The pattern is the same shape as the nine media accessors that shipped first. To add one:

1. **Have the inline-metadata substrate ready.** The relevant byte(s) must be stamped on the value at construction time. See [Data Value Layout](data-value-layout.md) for the per-kind byte map.
2. **Implement the function.** A regular `IScalarFunction` whose body reads the inline byte(s) first and falls back to a decode (or returns NULL) when the bytes read as the unstamped sentinel.
3. **Add an [`InlineAccessorField`](../../src/DatumIngest/Execution/InlineAccessorField.cs) entry** and a descriptor row with the argument kind, result kind, and the SQL function name.
4. **Mark the function class `IInlineMetadataAccessor`** — one-line `Field` getter returning the new enum value.
5. **Add an evaluator case** in `TryReadInlineMetadata` (same file as the evaluator handler) that reads the inline payload and returns either the stamped `ValueRef` or `null` (which triggers fallback to the function's `ExecuteAsync`).

The function's own decode/walk fallback is what the evaluator delegates to on the slow path, so it has to ship a correct slow path. Nothing in the elision infrastructure changes the function's contract — only when its `ExecuteAsync` runs.

## Runtime Side

Unlike CSE, this pass adds no new operator types. The rewrite is purely at the expression level; the evaluator gains one new `case InlineAccessorExpression iax:` in both [`EvaluateAsync`](../../src/DatumIngest/Execution/ExpressionEvaluator.cs) (DataValue path) and `EvaluateAsValueRefAsync` (ValueRef fast path). The hot path is synchronous and allocation-free in the stamped case; the fallback uses the same `ArrayPool` argument buffer and `DatumActivity.Scalars` activity span as the un-elided dispatch.

The pass relies on every operator with expression slots overriding `QueryOperator.RewriteExpressions`. `Project`, `Filter`, `OrderBy`, `GroupBy`, `Window`, `Limit`, `RowEnricher`, `FullTextSearch`, and `ModelInvocation` all override. **New operators with expression slots must override too** or the elider (and any future plan-time rewrite pass) will silently skip their expressions.

## See Also

- [Common Subexpression Elimination](common-subexpression-elimination.md) — the planner pass that runs after elision and deduplicates repeated elided nodes.
- [Value Representation](value-representation.md) — the per-kind inline-metadata layout this pass reads from.
- [Data Value Layout](data-value-layout.md) — byte-level reference for the payload fields.
- [Execution Plans](execution-plans.md) — how the rewritten nodes render in EXPLAIN output.
