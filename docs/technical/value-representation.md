# Value Representation: `DataValue` and `ValueRef`

DatumIngest carries values through the engine in two complementary forms.
`DataValue` is the universal storage currency — a 20-byte struct that lives
in rows, batches, and the on-disk format. `ValueRef` is the function-internal
currency — a managed-memory wrapper used during expression evaluation and
model dispatch. Both encode the same semantic types (the same `DataKind`s);
they trade off different things at different boundaries of the pipeline.

This page covers what each type represents, when each is used, and how
values cross between them.

## At a glance

| | `DataValue` | `ValueRef` |
|---|---|---|
| Size | 32 bytes (struct, no heap) | 20-byte tag + optional managed object reference |
| Storage | Inline payload OR arena offset OR sidecar offset | Inline payload OR managed payload (`string`, `byte[]`, `ValueRef[]`) |
| Where it lives | `Row`, `RowBatch`, `.datum` files, scan outputs, operator I/O | Expression evaluator, scalar function bodies, model `IModel.InferBatchAsync` |
| Lifetime | Bound to its arena (or to a sidecar registry) | GC-managed |
| Field names | Carried via 16-bit TypeId into per-query `TypeRegistry` (struct values); positional access stays the fast path | Same — TypeId rides on the inline tag; payload is positional `ValueRef[]` |

## `DataValue`: the row-layer currency

`DataValue` is a fixed 32-byte struct ([`src/DatumIngest/Model/DataValue.cs`](../../src/DatumIngest/Model/DataValue.cs)) that tags a value
with its `DataKind` and one of three storage shapes:

| Storage | When | Resolution |
|---|---|---|
| **Inline** | The payload fits in 16 bytes (small primitives, short UTF-8 strings, type tags, dates) | Read from the struct itself; no external lookup |
| **Arena-backed** | Payload too large to inline (long strings, vectors, struct field arrays, byte arrays produced at runtime) | `(_p0, _p1)` is `(offset, length)` into an `Arena` |
| **Sidecar-backed** | Payload sits in a `.datum-blob` companion file (large blobs from ingest, e.g. images) | `(_p0, _p1)` plus a `_storeId` byte resolve through the per-query `SidecarRegistry` |

Inline is decided per value, not per kind: a `String` may be inline (≤16
bytes UTF-8) or arena-backed depending on its actual size. A `Float32` is
always inline (4 bytes). An `Image` is always non-inline (decoded byte
arrays don't fit). The flag bits in `DataValue._flags` discriminate the
three shapes; `IsInline`, `IsArenaBacked`, `IsInSidecar` are public
predicates.

### Reading a non-inline value requires a store

Accessor overloads come in pairs. The no-store form works only for inline
values:

```csharp
public string AsString();              // inline only — throws if arena/sidecar-backed
public string AsString(IValueStore store, SidecarRegistry? registry);
```

The store-aware form resolves arena offsets via the supplied store, and
sidecar offsets via the registry. This is why operators need to thread
arenas around: a `DataValue` is only meaningful in conjunction with the
store where its payload lives. Cross-arena reads are a real bug class —
`DataValueRetention.Stabilize` exists specifically to copy a value's
payload from one arena to another when a row crosses an operator
boundary.

### Why inline-or-store, instead of always-managed?

`DataValue` predates `ValueRef` and is the type that flows through every
row. A row of 64 columns is a `DataValue[64]` — 1.28 KB stack-friendly
struct array, no per-cell heap allocation. The arena indirection means
the row's struct array stays compact even when individual columns carry
megabyte-scale payloads. This is the price for **non-allocating row
materialisation in the hot scan loop**.

The trade-off is that `DataValue` is *always* coupled to a store. You
can't pass a `DataValue` into a scalar function and expect it to read
its own contents — the function would need access to the right arena.
That's the friction `ValueRef` exists to remove.

## `ValueRef`: the function-internal currency

`ValueRef` ([`src/DatumIngest/Functions/ValueRef.cs`](../../src/DatumIngest/Functions/ValueRef.cs)) is also a struct, but its payload model is
different:

```csharp
public readonly struct ValueRef
{
    private readonly DataValue _inline;        // inline tag (kind, IsArray, inline payload, IsNull)
    private readonly object?   _materialized;  // managed payload OR null
}
```

Three states:

1. **Null.** `_inline.IsNull == true`, `_materialized == null`. The kind is
   carried by `_inline.Kind`.
2. **Inline non-null.** `_inline` carries the full value (small primitives,
   short strings, type tags). `_materialized == null`. Identical encoding
   to inline `DataValue`.
3. **Materialized non-null.** `_inline` is a typed-null tag (Kind +
   IsArray flags only). `_materialized` holds the actual payload as a
   managed object: `string` for `DataKind.String`, `byte[]` for
   `DataKind.Image` / byte arrays, `ValueRef[]` for `DataKind.Struct`
   field lists or typed array elements.

The crucial property: **a `ValueRef` carries everything it needs to read
itself.** No external store, no arena, no sidecar registry. Functions and
expression bodies operate on `ValueRef`s without threading any
ambient state.

### Recursive shape for struct and array

Struct and typed-array `ValueRef`s carry their nested children as
`ValueRef[]` in `_materialized`. Each child is itself a `ValueRef` with
its own deferred shape — long strings, byte arrays, nested
arrays/structs all the way down stay in managed memory:

```csharp
// {label: 'Plate', description: '...long...', tags: ['eggs','plate']}
ValueRef detection = ValueRef.FromStruct(
[
    ValueRef.FromString("Plate"),                  // inline tag + managed string
    ValueRef.FromString("...long..."),             // managed string (long)
    ValueRef.FromArray(DataKind.String,            // typed-array carrier
    [
        ValueRef.FromString("eggs"),
        ValueRef.FromString("plate"),
    ]),
]);
```

No arena interaction at construction time. Read access traverses the
managed tree:

```csharp
ReadOnlySpan<ValueRef> fields = detection.GetStructFields();
string label = fields[0].AsString();
ReadOnlySpan<ValueRef> tags = fields[2].GetArrayElements();
```

## The conversion boundary

Values cross between `DataValue` and `ValueRef` at exactly two boundaries
in the engine: when an expression evaluates an operand it needs in
function-friendly form, and when an expression result writes back to a
row. Both directions are handled by paired methods.

### `DataValue → ValueRef` (`ExpressionEvaluator.ToValueRef`)

Called when the evaluator hands a value to a scalar function or model.
For inline values it's a tag pass-through (no allocation). For non-inline
values it resolves the arena or sidecar payload into a managed object:

```csharp
case DataKind.String:
    return ValueRef.FromString(value.AsString(frame.Source, frame.SidecarRegistry));
case DataKind.Image:
    return ValueRef.FromBytes(DataKind.Image, value.AsByteSpan(...).ToArray());
```

After this point the value's lifetime is GC-managed, decoupled from the
producing arena. The function or model can hold onto it past the source
row's lifecycle without `Stabilize` calls.

### `ValueRef → DataValue` (`ValueRef.ToDataValue`)

Called when a function result, expression result, or model output needs
to land in a row. For inline / null values it's a direct pass-through.
For materialized payloads it writes to the supplied target store and
returns a `DataValue` with the resulting offsets:

```csharp
public DataValue ToDataValue(IValueStore targetStore)
{
    if (IsNull) return _inline;
    if (_materialized is null) return _inline;

    return _materialized switch
    {
        string s when _inline.Kind == DataKind.String =>
            DataValue.FromString(s, targetStore),
        ValueRef[] fields when _inline.Kind == DataKind.Struct =>
            DataValue.FromStruct(MaterialiseEach(fields, targetStore),
                targetStore, _inline.TypeId),
        ValueRef[] elements when _inline.IsArray =>
            BuildTypedArray(_inline.Kind, elements, targetStore),
        // ...
    };
}
```

For nested structures (struct fields that contain arrays, arrays of
structs, etc.) the recursion happens here: `MaterialiseEach` /
`BuildTypedArray` call `ToDataValue` on each child, so every nested
non-inline leaf writes to the arena exactly once during the descent. **A
`ValueRef` tree of arbitrary depth materialises in a single recursive
pass.**

If the consumer doesn't need a `DataValue` at all — display, hashing,
caching, terminal sink — they traverse `GetStructFields()` /
`GetArrayElements()` and read leaves directly. The arena is never
touched.

## Where each form is used

The boundaries are stable and worth remembering:

| Layer | Type | Why |
|---|---|---|
| `RowBatch` storage | `DataValue` | Compact `DataValue[]` per row, batched arena |
| Scan output | `DataValue` | Reads directly into `RowBatch.Arena` |
| `FilterOperator` predicate evaluation | `ValueRef` (Phase 2) | Predicate-only contexts skip the arena entirely |
| `ProjectOperator` expression evaluation | `ValueRef` internally → `DataValue` to write the row | One arena write per emitted column, not per intermediate |
| `JoinOperator` keys, `OrderByOperator` keys | `DataValue` | Hash / compare against a stable representation |
| Scalar function `Execute(...)` | `ValueRef` | Functions never see arenas, never call `Stabilize` |
| `IModel.InferBatchAsync` | `ValueRef` in, `ValueRef` out | Models receive resolved managed payloads, return managed payloads; the operator's scatter step calls `ToDataValue` once per row |
| `.datum` file pages | `DataValue` shape (decoded inline + sidecar pointers) | Fixed-stride mmap-friendly layout |

The pattern: **`DataValue` lives on the row layer, `ValueRef` lives on the
expression layer**, and the conversion happens at the boundary the
expression evaluator owns.

## What this buys

### 1. Function chains don't write intermediate results to the arena

```sql
SELECT models.llm(concat('summarize: ', text)) FROM articles
```

Without deferred materialisation, `concat`'s output writes to the arena
before `models.llm` reads it back. With `ValueRef`, `concat` produces a
managed `string`, the LLM model reads it directly via `value.AsString()`
on the input `ValueRef`, and only the LLM's response writes to the arena
at the operator scatter. For a million-row query with 2 KB prompts that's
~2 GB of arena writes saved per query.

### 2. Predicate contexts write zero arena bytes

```sql
WHERE upper(name) = 'ALICE'
```

The predicate path uses ValueRef end-to-end (`ExpressionEvaluator.EvaluateAsBoolean`
calls `EvaluateAsValueRef`, which dispatches the binary `=` and the
`upper(...)` call without going through `ToDataValue` once). The
boolean result drives the filter; no arena allocation happens for the
function chain.

### 3. Functions are simpler

A scalar function body looks like:

```csharp
public ValueRef Execute(ReadOnlySpan<ValueRef> args, in EvaluationFrame frame)
{
    StringBuilder sb = new();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].IsNull) continue;
        sb.Append(args[i].AsString());
    }
    return ValueRef.FromString(sb.ToString());
}
```

No `IValueStore` parameter. No `SidecarRegistry`. No `frame.Source` /
`frame.Target` distinction. No `Stabilize` calls when the function holds
a value across an arena boundary. The function is pure managed-memory
code, easy to write, easy to test, no arena bug-class to worry about.

## Architectural invariants worth knowing

- **Names live in the type registry, accessed via TypeId.** Each struct
  value carries a 16-bit TypeId (in `DataValue._charCount`, on the
  inline tag of a `ValueRef`) that indexes into the per-query
  `TypeRegistry` on `ExecutionContext.Types`. The registry maps id →
  `TypeDescriptor`, which carries field names, kinds, and nested
  type-ids. Plan-time-resolved positional access stays the hot path:
  `models.classify(image).label` rewrites to a positional struct-field
  access, and the runtime never consults the registry. Consumers that
  can't resolve shape statically — formatters, output writers,
  polymorphic UDFs, `typeof()` introspection — read names off the
  registry. Structurally-equal shapes intern to the same id, so
  `value.TypeId == other.TypeId` is a fast structural-equality check.
- **`DataValue` is always coupled to its store.** A `DataValue` carrying
  arena offsets is meaningful only with the right `Arena` in hand.
  Cross-arena reads are bugs; the stabilise step bridges them when a
  row needs to outlive its source arena.
- **`ValueRef` is store-free.** Hold one across operator boundaries
  freely; the GC manages the lifetime of any `_materialized` payload.
- **Conversion is the only crossing.** `ToValueRef` and `ToDataValue` are
  the only two methods that span the boundary. Anything else either
  works in DataValue-land or in ValueRef-land — never both.

## Reading the source

| What | Where |
|---|---|
| `DataValue` struct, factories, accessors | [`src/DatumIngest/Model/DataValue.cs`](../../src/DatumIngest/Model/DataValue.cs) |
| Storage flag bits, inline-vs-arena-vs-sidecar dispatch | [`src/DatumIngest/Model/DataValue.cs`](../../src/DatumIngest/Model/DataValue.cs) (search `DataValueFlags`) |
| `Arena` | [`src/DatumIngest/Model/Arena.cs`](../../src/DatumIngest/Model/Arena.cs) |
| `ValueRef` struct, recursive constructors, `ToDataValue` | [`src/DatumIngest/Functions/ValueRef.cs`](../../src/DatumIngest/Functions/ValueRef.cs) |
| `DataValue → ValueRef` boundary (`ToValueRef`) | [`src/DatumIngest/Execution/ExpressionEvaluator.cs`](../../src/DatumIngest/Execution/ExpressionEvaluator.cs) |
| Scalar function interface using `ValueRef` | [`src/DatumIngest/Functions/IScalarFunction.cs`](../../src/DatumIngest/Functions/IScalarFunction.cs) |
| Model interface using `ValueRef` | [`src/DatumIngest/Models/IModel.cs`](../../src/DatumIngest/Models/IModel.cs) |
| Model invocation operator (where `ToDataValue` happens at scatter) | [`src/DatumIngest/Execution/Operators/ModelInvocationOperator.cs`](../../src/DatumIngest/Execution/Operators/ModelInvocationOperator.cs) |
