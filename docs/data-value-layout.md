# `DataValue` Byte Layout

[← Back to README](../README.md) · [Value Representation](value-representation.md) · [`.datum` Format](datum-format.md)

`DataValue` is a 20-byte struct ([`src/DatumIngest/Model/DataValue.cs`](../src/DatumIngest/Model/DataValue.cs)) with explicit `[StructLayout(LayoutKind.Explicit)]`. Three values fit in a 64-byte cache line. No managed reference fields, so a `DataValue[]` is invisible to the GC.

This page is the byte-level reference: which fields live at which offsets, how the storage flags discriminate inline / arena / sidecar, and how the type registry's TypeId rides on every struct value.

## Field offsets

| Offset | Size | Field | Role |
|---|---|---|---|
| 0 | 1 | `_kind` (`DataKind`) | Type discriminator (Int32, String, Struct, Type, …) |
| 1 | 1 | `_flags` (`DataValueFlags`) | Storage shape + null flag |
| 2 | 2 | `_charCount` (`ushort`) | Multi-purpose; meaning depends on storage shape (see below) |
| 4 | 4 | `_p0` (`int`) | Payload word 0 |
| 8 | 4 | `_p1` (`int`) | Payload word 1 |
| 12 | 4 | `_p2` (`int`) | Payload word 2 |
| 16 | 4 | `_p3` (`int`) | Payload word 3 |

The 16-byte payload region (`_p0`–`_p3`) is reinterpreted per storage shape: as inline scalar bits, as `(arena_offset, length)`, as a sidecar slot, or as a packed inline array.

## `DataValueFlags`

```
0x01  IsNull        Typed null. Other bits and payload are ignored.
0x02  InArena       Payload lives in an IValueStore (typically Arena).
0x04  InSidecar     Payload lives in a .datum-blob sidecar.
0x08  IsArray       This value is a typed array, not a scalar.
0x10  InlineArray   Array payload packed into _p0–_p3 (≤ 16 bytes total).
0x20  IsMultiDim    Array carries an explicit shape (ndim ≥ 2) as an int32[ndim]
                    prefix at the head of its payload bytes. Only valid with IsArray.
0x40  reserved
0x80  reserved
```

Storage flags are mutually exclusive: `None` = inline, or exactly one of `InArena` / `InSidecar`. `IsNull` overrides every payload interpretation. `IsMultiDim` is orthogonal to the storage flags — it can combine with any of inline / arena / sidecar.

## Storage shapes

### Inline scalar (`_flags == None`)

The value sits entirely in `_p0`–`_p3`. `_charCount` carries kind-specific sizing for strings (UTF-8 byte length and char count, both 0–16); ignored for fixed-width primitives.

| Kind | Payload encoding |
|---|---|
| `Int32` / `Float32` | `_p0` |
| `Int64` / `Float64` / `DateTime` | `_p0`+`_p1` |
| `Int128` / `Decimal` / `Uuid` | full 16 bytes |
| `String` / `Json` (≤ 16 UTF-8 bytes) | UTF-8 packed across `_p0`–`_p3`; `_charCount` low = byte length, high = char count |
| `Type` | `_p0` low byte = represented `DataKind`; `_charCount` = TypeId |

### Arena-backed (`InArena`)

`(_p0, _p1)` is `(arena_offset, length_in_bytes)` into the `IValueStore` supplied to the accessor. Used for long strings, byte arrays, vectors, struct field arrays, and intermediate values produced at runtime.

### Sidecar-backed (`InSidecar`)

The 16-byte `ArraySlot` layout (sidecar pointer):

| Bytes | Meaning |
|---|---|
| 0–7 | 64-bit absolute offset into `.datum-blob` |
| 8–12 | 40-bit length (1 TiB cap) |
| 13–14 | Per-element on-disk TypeId (struct elements only) |
| 15 | Reserved (codec slot) |

The low byte of `_charCount` carries the sidecar `storeId` used to look up the right `IBlobSource` via the per-query `SidecarRegistry`.

### Inline array (`IsArray | InlineArray`)

Element bytes pack contiguously into `_p0`–`_p3` (16-byte cap). Element count lives in the low byte of `_charCount`. `Kind` is the *element* kind. Used for small typed arrays — `Float32[4]`, `Int32[4]`, `UInt8[16]`, `Float64[2]` — that would otherwise pay for an arena allocation.

### Arena/sidecar typed array (`IsArray | InArena` or `IsArray | InSidecar`)

Element bytes live externally; `(_p0, _p1)` or the sidecar slot points at them. `Kind` is the per-element kind. For `Struct` elements stored in a sidecar, each element's slot carries its own TypeId in bytes 13–14 (see "Type registry" below).

### Multi-dim array (`IsArray | IsMultiDim`, combined with any storage flag)

A multi-dim array carries its per-dimension shape as an `int32[ndim]` prefix at the head of its payload bytes — inline, arena, and sidecar tiers all use the same prefix-in-payload layout. Element bytes follow the prefix contiguously in row-major order. `ndim` lives in the high byte of `_charCount`; `Kind` is the per-element kind.

```
┌─────────────────────────────────────────────────────────────────┐
│ Payload bytes:                                                  │
│   [int32 × ndim shape prefix][element bytes...]                 │
└─────────────────────────────────────────────────────────────────┘
```

Element-access helpers (`AsArraySpan<T>`, `InlineArrayBytes`, `ElementCount`) transparently skip the prefix; `GetShape(store)` exposes the dims as a `ReadOnlySpan<int>`. The flag is only set for `ndim ≥ 2` — 1-D arrays stay flat with no flag and no prefix.

Multi-dim arrays support only fixed-width primitive element kinds. `String`, `Struct`, `Image`, `Audio`, `Video`, `Json`, `PointCloud`, and `UInt8` (byte arrays) are explicitly rejected — they reuse `_charCount` for `storeId` / `TypeId` already, and the byte-array kind collides with the element-count derivation. DDL with a multi-dim shape on a reference-element kind is rejected at `CREATE TABLE` time.

Constructed by `FromArenaMultiDimArray<T>`, `FromInlineMultiDimArray<T>`, `FromMultiDimArrayInSidecar`, or attached at INSERT time by `LiteralCoercion.EnforceFixedShape` when the target column declares `Array<T>(N, M, …)` with `ndim ≥ 2`.

## The `_charCount` slot

Two bytes at offset 2, repurposed by storage shape:

| Storage shape | Meaning |
|---|---|
| Inline string / Json | low byte = UTF-8 byte length; high byte = char count |
| Reference-store string / Json | full char count (0 = unknown, 65535 = overflow sentinel) |
| Sidecar pointer | low byte = `storeId` |
| Inline array | low byte = element count |
| **Multi-dim array (any storage)** | **high byte = ndim** (combines with the low-byte usages above) |
| **Struct** (any storage) | **TypeId — index into per-query `TypeRegistry`** |
| `Type` value | TypeId of the represented type (when the represented kind is `Struct` or array-of-struct) |

The TypeId reuse is what makes structs self-describing without a wider value carrier. See [Type System (SQL)](sql/type-system.md) for the consumer-facing model.

## Type registry encoding

Struct values carry a 16-bit `TypeId` in `_charCount`. The TypeId indexes into `ExecutionContext.Types`, a per-query `TypeRegistry` that maps id → `TypeDescriptor` (kind + nullability + field names + nested type-ids).

- `TypeId = 0` (`TypeRegistry.NoType`) means "no type registered" — happens for values constructed by paths that don't resolve a shape (legacy code, hand-built test values).
- Non-zero TypeIds are stable within a query; structurally-equal shapes intern to the same id, so `value.TypeId == other.TypeId` is a structural-equality fast path.
- The registry is shared by child execution contexts (subqueries, parallel branches) but not across queries.

For arrays of structs, the TypeId rides on each *element's* sidecar slot (bytes 13–14), not on the array carrier. Per-element TypeIds let a heterogeneous `Array<Struct>` carry varied shapes without a registry walk at the array level. Inline / arena array carriers can't store per-element TypeIds today; sidecar is the path that supports it.

## Persistence: runtime ↔ on-disk TypeIds

Runtime TypeIds are per-query and not stable across processes. `.datum` files persist a stable on-disk TypeId space:

- The footer carries a type-table indexing each registered type's descriptor blob (in the sidecar) by on-disk id.
- Per-struct-column footers carry their on-disk TypeId in the column header.
- Each sidecar struct-element slot carries its on-disk TypeId in bytes 13–14.

At scan time, `IDatumFileTableProvider.EnsureTypeTableLoaded(context)` reads the footer's type-table, deserialises descriptors, interns them into the query's runtime `TypeRegistry`, and registers the on-disk → runtime mapping in `context.TypeIdTranslations` (a `TypeIdTranslationTable` keyed by sidecar `storeId`).

The decoder translates each slot's on-disk TypeId via the translator before stamping the runtime TypeId on the materialised `DataValue`. The translator is passed through `ScanAsync` rather than cached on the provider, so a catalog-shared provider scanned by two concurrent queries doesn't race on its TypeId space.

## Reading values

A `DataValue` carrying inline bits is self-contained. Non-inline values resolve through the `IValueStore` (arena or sidecar) supplied to the accessor:

```csharp
public string AsString();                                             // inline only — throws otherwise
public string AsString(IValueStore store, SidecarRegistry? registry); // resolves arena or sidecar
```

For struct values, name-based field access goes through the registry:

```csharp
TypeDescriptor? type = value.GetTypeDescriptor(context.Types);
DataValue label = value.GetField("label", context.Types, store);
```

Plan-time-resolved positional access skips the registry entirely:

```csharp
DataValue[] fields = value.AsStruct(store);
DataValue label = fields[0];   // position fixed by the planner
```

The hot path (filter, projection, join) uses positional access. The registry is consulted only by paths that don't know the shape statically — formatters, output writers, polymorphic UDFs, debug print.

## Reading the source

| What | Where |
|---|---|
| `DataValue` struct, factories, accessors | [`src/DatumIngest/Model/DataValue.cs`](../src/DatumIngest/Model/DataValue.cs) |
| `DataValueFlags` discriminator | same file, search `DataValueFlags` |
| `TypeRegistry` / `TypeDescriptor` | [`src/DatumIngest/Model/TypeRegistry.cs`](../src/DatumIngest/Model/TypeRegistry.cs), [`TypeDescriptor.cs`](../src/DatumIngest/Model/TypeDescriptor.cs) |
| `TypeIdTranslationTable` | [`src/DatumIngest/Model/TypeIdTranslationTable.cs`](../src/DatumIngest/Model/TypeIdTranslationTable.cs) |
| Sidecar slot layout, encoder, decoder | [`src/DatumIngest/DatumFile/V2/Encoding/VariableSlotPageEncoderV2.cs`](../src/DatumIngest/DatumFile/V2/Encoding/VariableSlotPageEncoderV2.cs), [`Decoding/VariableSlotPageDecoderV2.cs`](../src/DatumIngest/DatumFile/V2/Decoding/VariableSlotPageDecoderV2.cs) |
| Footer type-table persistence | [`src/DatumIngest/DatumFile/V2/FooterV2.cs`](../src/DatumIngest/DatumFile/V2/FooterV2.cs), [`TypeDescriptorSerializer.cs`](../src/DatumIngest/DatumFile/V2/TypeDescriptorSerializer.cs) |
