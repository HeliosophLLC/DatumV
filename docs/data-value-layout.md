# `DataValue` Byte Layout

[← Back to README](../README.md) · [Value Representation](value-representation.md) · [`.datum` Format](datum-format.md)

`DataValue` is a 32-byte struct ([`src/DatumIngest/Model/DataValue.cs`](../src/DatumIngest/Model/DataValue.cs)) with explicit `[StructLayout(LayoutKind.Explicit)]`. Two values fit in a 64-byte cache line. No managed reference fields, so a `DataValue[]` is invisible to the garbage collector.

This page is the byte-level reference: which fields live at which offsets, how the storage flags discriminate inline / arena / sidecar, where each kind keeps its inline metadata, and how the type registry's TypeId rides on every struct value.

## Field offsets

| Offset | Size | Field | Role |
|---|---|---|---|
| 0 | 1 | `_kind` (`DataKind`) | Type discriminator (Int32, String, Image, Struct, Type, …) |
| 1 | 1 | `_flags` (`DataValueFlags`) | Storage shape + null flag |
| 2 | 2 | `_charCount` (`ushort`) | Multi-purpose; meaning depends on storage shape (see below) |
| 4 | 4 | `_p0` (`int`) | Payload word 0 |
| 8 | 4 | `_p1` (`int`) | Payload word 1 |
| 12 | 4 | `_p2` (`int`) | Payload word 2 |
| 16 | 4 | `_p3` (`int`) | Payload word 3 |
| 20 | 4 | `_p4` (`int`) | Payload word 4 (kind-specific metadata / String+JSON cached hash low) |
| 24 | 4 | `_p5` (`int`) | Payload word 5 (kind-specific metadata / String+JSON cached hash high) |
| 28 | 4 | `_p6` (`int`) | Payload word 6 (TypeId for Struct/Type in low 16 bits; per-kind tail otherwise) |

The 28-byte payload region (`_p0`–`_p6`) is reinterpreted per storage shape and per kind: as inline scalar bits, as a unified 64-bit-offset / 40-bit-length pair plus kind-specific metadata, or as a packed inline string / array.

## `DataValueFlags`

```
0x01  IsNull        Typed null. Other bits and payload are ignored.
0x02  InArena       Payload lives in an IValueStore (typically Arena).
0x04  InSidecar     Payload lives in a .datum-blob sidecar.
0x08  IsArray       This value is a typed array, not a scalar.
0x10  InlineArray   Array payload packed into the inline payload region.
0x20  IsMultiDim    Array carries an explicit shape (ndim ≥ 2) as an int32[ndim]
                    prefix at the head of its payload bytes. Only valid with IsArray.
0x40  reserved
0x80  reserved
```

Storage flags are mutually exclusive: `None` = inline, or exactly one of `InArena` / `InSidecar`. `IsNull` overrides every payload interpretation.

## Storage shapes

### Inline scalar (`_flags == None`)

The value sits in the inline payload region. `_charCount` carries kind-specific sizing for strings (UTF-8 byte length and char count); ignored for fixed-width primitives. `_p6` low 16 bits carry the TypeId for `Type` values.

| Kind | Payload encoding |
|---|---|
| `Int32` / `Float32` | `_p0` |
| `Int64` / `Float64` | `_p0`+`_p1` |
| `DateTime` | `_p0`+`_p1` (ticks) + `_p2` (offset minutes) |
| `Int128` / `UInt128` / `Decimal` / `Uuid` | bytes 0–15 of payload |
| `Point2D` | two `float32`s in `_p0` / `_p1` |
| `Point3D` | three `float32`s in `_p0` / `_p1` / `_p2` |
| `VideoFrame` | `(videoId, frameIndex)` inline at `_p0` / `_p1` |
| `String` / `Json` (≤ 27 UTF-8 bytes) | UTF-8 packed across payload bytes 0–26; `_charCount` low byte = byte length, high byte = char count |
| `Type` | `_p0` low byte = represented `DataKind`; `_p6` low 16 bits = TypeId of the represented type |

### Reference-backed (`InArena` or `InSidecar`) — unified layout

```
payload[0..7]    offset (8 B, 64-bit; spans _p0+_p1)
payload[8..12]   length (5 B, 40-bit / 1 TiB cap; spans _p2 + low byte of _p3)
_charCount.low   sidecar storeId (for IsInSidecar); char count cache (for arena String/JSON)
payload[16..27]  kind-specific metadata (see per-kind table below)
```

`BackedOffset` / `BackedLength` decode arena and sidecar values uniformly via these slots. `SidecarOffset` / `SidecarLength` exist as aliases.

The remaining 12 payload bytes (`_p4`+`_p5`+`_p6` = bytes 16–27 of payload) carry per-kind inline metadata:

#### `String` / `Json`

```
_p4 + _p5  (bytes 16–23)  XxHash64 of UTF-8 / CBOR bytes (8 B; zero = no cached hash)
_p6         (bytes 24–27) reserved
```

The cached hash is consulted by `DataValue.GetHashCode()` for non-inline strings — load-bearing for hash joins, group-by, distinct, and set operations on string columns. Sidecar-backed values constructed without bytes (`FromStringInSidecar`) carry zero (the "no cached hash" sentinel); `CompareStrings` falls back to its safe no-hash path.

#### `Image`

```
_p4 low 16 bits      width (uint16)
_p4 high 16 bits     height (uint16)
_p5 byte 0           channels (1=gray, 3=RGB, 4=RGBA, …)
_p5 bytes 1–3        reserved (format / colorspace / bit_depth slots, unwired today)
_p6                  reserved
```

Populated end-to-end at every Image production path (`ZipDeserializer` ingest → `ImageHeaderParser`; all image-producing scalar functions + model outputs via [`ImageDataValueFactory`](../src/DatumIngest/Functions/Image/ImageDataValueFactory.cs) at the `ValueRef → DataValue` materialization boundary; INSERT / literals / in-memory tables; `.datum` decode via 4 KB header peek in [`VariableSlotPageDecoderV2.DecodeImageWithInlineDimensions`](../src/DatumIngest/DatumFile/V2/Decoding/VariableSlotPageDecoderV2.cs); `DataValueRetention.Stabilize` forwarding). Accessors: `ImageWidth`, `ImageHeight`, `ImageChannels`. SQL: `image_width()`, `image_height()` short-circuit on inline metadata before calling `SKBitmap.Decode`.

#### `Audio`

```
_p4         (bytes 16–19) sample_rate (uint32, Hz)
_p5 byte 0  (byte 20)     channels
_p5 byte 1  (byte 21)     bit_depth
_p5 bytes 2–3             reserved
_p6         (bytes 24–27) frame_count (uint32, samples per channel)
```

Populated via [`AudioDataValueFactory`](../src/DatumIngest/Functions/Audio/AudioDataValueFactory.cs) using [`AudioHeaderParser`](../src/DatumIngest/Functions/Audio/AudioHeaderParser.cs). WAV is supported today; MP3 / FLAC / OGG fall through to zero-sentinel metadata until parsers are added. Accessors: `AudioSampleRate`, `AudioChannels`, `AudioBitDepth`, `AudioFrameCount`. SQL: `audio_sample_rate()` (companions to be added as needed).

#### `Video`

```
_p4 low 16 bits      width (uint16)
_p4 high 16 bits     height (uint16)
_p5 low 16 bits      fps_x256 (uint16; 8.8 fixed-point — multiply by 1/256.0 for the float fps)
_p5 byte 2           codec discriminator (enum byte: 0=unknown, 1=H264, 2=H265, 3=AV1, 4=VP9, …)
_p5 byte 3           reserved
_p6                  frame_count (uint32)
```

Populated via [`VideoDataValueFactory`](../src/DatumIngest/Functions/Video/VideoDataValueFactory.cs) using [`VideoHeaderParser`](../src/DatumIngest/Functions/Video/VideoHeaderParser.cs) (Sdcb.FFmpeg-based — reads `Codecpar` without spinning up a decoder). Codec discriminator: 0=unknown, 1=H264, 2=H265, 3=AV1, 4=VP9, 5=VP8, 6=MPEG4, 7=MPEG2, 8=Theora. Accessors: `VideoWidth`, `VideoHeight`, `VideoFpsX256`, `VideoCodec`, `VideoFrameCount`. SQL: `video_width()`, `video_height()` (return real values for any container FFmpeg can demux; NULL when FFmpeg fails to open the bytes).

#### `PointCloud`

```
_p4         (bytes 16–19) point_count (uint32)
_p5 byte 0  (byte 20)     attribute_flags (bit 0 = has_color, bit 1 = has_normals, …)
_p5 bytes 1–3             reserved
_p6                       reserved
```

Populated via `ValueRef.MaterializePointCloudWithMetadata` at the `ValueRef → DataValue` boundary; the `PointCloudHeader` is parsed (cheap — the blob already starts with a 40-byte header) and the count + flags are stamped. Accessors: `PointCloudCount`, `PointCloudAttributes`. SQL: `point_cloud_count()`, `point_cloud_has_color()` short-circuit on inline metadata before reading the full blob.

#### `Mesh`

```
_p4         (bytes 16–19) vertex_count (uint32)
_p5         (bytes 20–23) triangle_count (uint32)
_p6 byte 0  (byte 24)     attribute_flags (bit 0 = has_color, bit 1 = has_normals, bit 2 = has_uvs, bit 3 = has_texture)
_p6 bytes 1–3             reserved
```

Populated via `ValueRef.MaterializeMeshWithMetadata`. Accessors: `MeshVertexCount`, `MeshTriangleCount`, `MeshAttributes`. SQL: `mesh_vertex_count()`, `mesh_triangle_count()` short-circuit on inline metadata.

#### Sidecar-backed reference arrays (`IsArray | InSidecar`)

The slot block in the sidecar follows the per-element `ArraySlot` layout (16 bytes: 64-bit offset + 40-bit length + per-element TypeId + codec discriminator). The container DataValue uses the same 64-bit offset / 40-bit length encoding to address the slot block itself; `_charCount.low` carries the sidecar storeId.

### Inline string (`_flags == None`, `Kind == String` / `Json`)

```
struct bytes 4..30    UTF-8 bytes (capacity = DataValue.MaxInlineUtf8Bytes = 27)
_charCount low byte   utf8 byte count (0..27)
_charCount high byte  char count (0..27)
```

Strings whose UTF-8 form fits in 27 bytes stay inline. The on-disk `.datum` VariableSlot is still 16 bytes, so in-memory inline strings of 17–27 bytes spill to the sidecar at encode time via `VariableSlotPageEncoderV2`'s spill path — transparent to the data model.

### Inline array (`IsArray | InlineArray`)

Element bytes pack contiguously into payload bytes 0–N. Element count lives in `_charCount.low`; `Kind` is the element kind. Multi-dim arrays use `_charCount.high` for `ndim` and carry a leading `int32 × ndim` shape prefix in the inline payload.

### Multi-dim array (`IsArray | IsMultiDim`)

Shape prefix `int32[ndim]` at the head of the payload bytes (inline) or arena/sidecar bytes (reference-backed). `ndim` in `_charCount.high`. Element-access helpers (`AsArraySpan<T>`, `ElementCount`, `GetShape`) transparently skip the prefix.

## The `_charCount` slot

Two bytes at offset 2, mode-multiplexed:

| Storage shape | Meaning |
|---|---|
| Inline string / JSON | low byte = UTF-8 byte length; high byte = char count |
| Arena-backed String / JSON | full char count (0 = unknown; 65535 = overflow sentinel) |
| Sidecar pointer | low byte = `storeId` |
| Inline array | low byte = element count |
| Multi-dim array (any storage) | high byte = ndim (combines with low-byte usage) |

TypeId is **not** in `_charCount` — it moved to `_p6` low 16 bits in the 32-byte migration, freeing this slot from one of its overloaded meanings.

## Type registry encoding

Struct and `Type` values carry a 16-bit `TypeId` in the low 16 bits of `_p6`. The TypeId indexes into `ExecutionContext.Types`, a per-query `TypeRegistry` that maps id → `TypeDescriptor` (kind + nullability + field names + nested type-ids).

- `TypeId = 0` (`TypeRegistry.NoType`) means "no type registered" — happens for values constructed by paths that don't resolve a shape (legacy code, hand-built test values, sidecar-backed `Array<Struct>` container — per-element TypeIds live on the slot, not the array carrier).
- Non-zero TypeIds are stable within a query; structurally-equal shapes intern to the same id, so `value.TypeId == other.TypeId` is a structural-equality fast path.
- The registry is shared by child execution contexts (subqueries, parallel branches) but not across queries.

For `Array<Struct>`, each element's slot carries its own TypeId (slot bytes 13–14), not the array container. Per-element TypeIds let a heterogeneous array carry varied shapes without a registry walk at the array level.

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

Inline metadata accessors are free — they read directly from the payload bytes without dereferencing the arena or sidecar:

```csharp
ushort w = value.ImageWidth;        // image width without decode
uint count = value.PointCloudCount; // point count without arena fault
uint rate = value.AudioSampleRate;  // WAV sample rate without decode
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
| Size constant, layout assertions | same file, `DataValue.SizeBytes`, `DataValue.MaxInlineUtf8Bytes` |
| `DataValueFlags` discriminator | same file, search `DataValueFlags` |
| Sentinel `ArenaOffset` / `ArenaLength` | [`src/DatumIngest/Model/ArenaCoordinates.cs`](../src/DatumIngest/Model/ArenaCoordinates.cs) |
| Per-kind metadata factories | [`Functions/Image/ImageDataValueFactory.cs`](../src/DatumIngest/Functions/Image/ImageDataValueFactory.cs), [`Functions/Audio/AudioDataValueFactory.cs`](../src/DatumIngest/Functions/Audio/AudioDataValueFactory.cs), [`Functions/Video/VideoDataValueFactory.cs`](../src/DatumIngest/Functions/Video/VideoDataValueFactory.cs) |
| Audio header parser (WAV) | [`Functions/Audio/AudioHeaderParser.cs`](../src/DatumIngest/Functions/Audio/AudioHeaderParser.cs) |
| Image header parser | [`Functions/Image/ImageHeaderParser.cs`](../src/DatumIngest/Functions/Image/ImageHeaderParser.cs) |
| `TypeRegistry` / `TypeDescriptor` | [`src/DatumIngest/Model/TypeRegistry.cs`](../src/DatumIngest/Model/TypeRegistry.cs), [`TypeDescriptor.cs`](../src/DatumIngest/Model/TypeDescriptor.cs) |
| `TypeIdTranslationTable` | [`src/DatumIngest/Model/TypeIdTranslationTable.cs`](../src/DatumIngest/Model/TypeIdTranslationTable.cs) |
| Sidecar slot layout, encoder, decoder | [`src/DatumIngest/DatumFile/V2/Encoding/VariableSlotPageEncoderV2.cs`](../src/DatumIngest/DatumFile/V2/Encoding/VariableSlotPageEncoderV2.cs), [`Decoding/VariableSlotPageDecoderV2.cs`](../src/DatumIngest/DatumFile/V2/Decoding/VariableSlotPageDecoderV2.cs) |
| Footer type-table persistence | [`src/DatumIngest/DatumFile/V2/FooterV2.cs`](../src/DatumIngest/DatumFile/V2/FooterV2.cs), [`TypeDescriptorSerializer.cs`](../src/DatumIngest/DatumFile/V2/TypeDescriptorSerializer.cs) |
