---
title: Structs
---

# Structs

A `Struct` is a single value that carries a fixed, ordered list of
**named fields**. Each field can be a different type — scalars, arrays,
even other structs. Structs are how DatumV groups several related values
into one cell without flattening them out across columns.

Reach for a struct when:

- A function or model returns several values at once
  (`{class, score}`, `{depth, intrinsics}`, …) and you want to keep
  them together as they flow through your query.
- You want to project a small "record" inline — `{lat, lng, label}` —
  without a separate CTE.
- You're consuming Parquet / HDF5 / model output that already arrives
  in a nested shape.

Structs are not the right answer when fields are large and queried
independently (split into columns) or when the field set is dynamic per
row (use [`Json`](type-system.md#json) — JSON is CBOR-backed and accepts
any shape, while a struct has a fixed shape interned at plan time).

## Constructing structs

The brace literal `{ field: expr, ... }` is the only way to build a
struct value in SQL. Field names are bare identifiers (or any
type-keyword name like `image`, `audio`, `video` — useful for
ONNX-style "image"-channel inputs); values are arbitrary expressions.

```sql
-- Two-field struct, types inferred from each field expression
SELECT {x: 1, y: 2} AS point

-- Fields drawn from existing columns
SELECT {name: customer_name, total: order_total, ts: order_date} AS summary
FROM orders

-- Nested structs
SELECT {point: {x: 1.0, y: 2.0}, radius: 5.0} AS circle

-- Empty struct (legal but unusual)
SELECT {} AS empty
```

Each struct literal interns a fresh shape into the per-query type
registry: two literals with the same field names + types share a
TypeId, two literals with reordered or renamed fields don't. The TypeId
is stamped on the value, so `typeof()` and bracket access on a
literal don't need to re-walk anything.

See also: [Array, Struct & Index Literals](literals.md) for the literal
syntax surface alongside arrays.

## Reading fields

Three equivalent ways to pull a field out of a struct value.

### Bracket — by name

A **string** subscript looks the field up by name. Lookup is
**case-insensitive**, regardless of how the field was declared:

```sql
SELECT s['x'] FROM t
SELECT {Score: 0.9}['SCORE']   -- returns 0.9
```

A missing field returns NULL (typed as `Struct`).

### Bracket — by ordinal

An **integer** subscript indexes by 1-based declaration order, mirroring
PostgreSQL's array-style accessor:

```sql
SELECT s[1]   -- first declared field
SELECT s[2]   -- second declared field

-- Out-of-range returns NULL rather than erroring.
SELECT {a: 1, b: 2}[5]   -- NULL
```

### Dot — by name

A qualified reference `prefix.field` resolves to a struct field lookup
when `prefix` isn't a real row column. This makes LET-bound and
DECLARE-bound structs read naturally:

```sql
SELECT LET pt = {x: 10, y: 20}, pt.x AS x, pt.y AS y FROM t
```

If `prefix` does match a row column (e.g. you also have a column called
`pt`), the column wins — quote the alias or use bracket access to
disambiguate.

When the prefix resolves to a struct but the field name doesn't match
any declared field, dot access **errors** — bracket access returns NULL.
Use whichever matches the strictness you want.

## Destructuring with LET

A LET binding can pull every field out of a struct in one step. Two
forms — named and positional — both supported on the struct path:

```sql
-- Named: bind by field name. Pattern order is independent of declaration order.
SELECT LET {alpha, beta} = {beta: 8.0, alpha: 7.0},
       alpha AS av,
       beta AS bv
FROM t
-- av = 7.0, bv = 8.0

-- Positional: bind by 1-based declaration order
SELECT LET (p, q) = {alpha: 7.0, beta: 8.0},
       p AS pv,
       q AS qv
FROM t
-- pv = 7.0, qv = 8.0
```

`LET {a, b} = some_func(x)` is the common shape when a UDF or model
call returns a multi-field struct and you want to use each field as a
plain alias downstream. See [LET Bindings — Tuple
Destructuring](let-bindings.md#tuple-destructuring) for the full
treatment.

## Struct columns and DDL

`Struct` is a `DataKind` like any other and appears in `typeof()`,
`IS Struct`, and `information_schema.columns`. What it does **not**
have is an inline field-list syntax inside `CREATE TABLE`:

```sql
-- Allowed — opaque struct column.
CREATE TABLE rows (payload Struct)

-- NOT supported — DDL doesn't accept Struct<...> with an inline
-- field list. Field shapes for table columns come from the data being
-- inserted (Parquet / HDF5 / model output) and are stored in the
-- column's schema metadata, not in DDL.
CREATE TABLE rows (payload Struct<a: Int32, b: Float32>)
```

The places that **do** accept `Struct<name: Kind, ...>` annotations are
function and model contracts — e.g. `RETURNS Struct<class: Int32, score:
Float32>` on a model declaration, or as the element annotation of an
`Array<Struct<...>>` field. The runtime treats every `Struct<...>`
annotation as the opaque `Struct` kind at the value layer; the field
shape is carried alongside in the schema (see [`system.types`](#namedtypes)
for the well-known shapes).

### Named composite types

The catalog ships a fixed vocabulary of well-known struct shapes
(`ScoredClass`, `BoundingBox`, `ScoredDetection`, `Keypoint`,
`TimedText`, …) so model contracts can reference common record shapes
by name instead of redeclaring them. Browse the live vocabulary:

```sql
SELECT name, definition FROM system.types
```

Sample entries:

| Name | Shape |
|------|-------|
| `BoundingBox` | `Struct<x: Float32, y: Float32, w: Float32, h: Float32>` |
| `ScoredClass` | `Struct<class: Int32, score: Float32>` |
| `ScoredLabel` | `Struct<label: String, score: Float32>` |
| `Keypoint` | `Struct<x: Float32, y: Float32, confidence: Float32>` |
| `TimedText` | `Struct<text: String, start_ms: Int64, end_ms: Int64>` |

User-defined `CREATE TYPE` is not yet exposed — for now the vocabulary
is engine-defined and queries can reference these names anywhere a
struct annotation is accepted.

## Arrays of structs

Arrays of structs (`Array<Struct>`) are fully supported and are the
natural carrier for object-detection results, multi-instance model
outputs, and any "one row, many records" pattern.

```sql
-- Element-wise unpacking with UNNEST: one struct per output row
SELECT u.det.class, u.det.score, u.det.bbox.x AS x
FROM images
CROSS APPLY UNNEST(detections) AS u(det)

-- ARRAY_AGG of struct literals across a group
SELECT category,
       ARRAY_AGG({id: id, score: score}) AS items
FROM products
GROUP BY category

-- Bracket-chain into an array of structs
SELECT detections[1]['bbox']['x'] AS first_x FROM images
```

Multi-dimensional arrays of structs (`Array<Struct>(rows, cols)`) are
also supported — useful for grid-shaped model outputs (e.g. a per-tile
classification result over a tiled image). Element access uses one
index per dimension, same as any other multi-dim array.

Struct fields can themselves be arrays — `{depth: Float32[], shape:
Int32[]}` is fine — and nesting is unbounded (`{a: {b: {c: ...}}}`
parses and evaluates).

## Type system integration

```sql
-- typeof() on a struct returns a Type value whose display string is
-- the shape itself.
SELECT typeof({x: 1, y: 2.0})
-- => Struct{x: Int32, y: Float64}

-- IS Struct desugars to typeof(x) = Struct
SELECT * FROM data WHERE col IS Struct

-- Cells render compactly in the results pane as Struct(N fields)
-- with the per-field detail available via bracket access.
```

NULL semantics are standard: a NULL struct propagates through field
access (`null_struct['x']` is NULL), and any field of a struct may
itself be NULL.

## What structs can't do

Structs are deliberately scoped to "carry a record around" and do not
participate in the comparison-based operators:

| Operation | Supported on struct? |
|-----------|---------------------|
| `WHERE struct = other_struct` | No — no equality |
| `ORDER BY struct_col` | No |
| `GROUP BY struct_col` | No |
| `DISTINCT` over a struct column | No |
| `JOIN ON a.struct = b.struct` | No |
| Use as `PRIMARY KEY` / unique-index column | No |
| `CAST(x AS Struct)` between different struct shapes | No |
| Hash / dedup aggregation directly on the struct | No |

The pattern is to **project the fields you'd compare on** (`GROUP BY
detection.class`, `ORDER BY result.score DESC`) rather than the struct
as a whole. This matches how Arrow / Parquet readers surface nested
records and avoids relying on an implicit, schema-dependent field
ordering for equality.

`Struct ↔ Json` is also not auto-coerced. JSON is CBOR-backed bytes and
goes through `json_value()` / `json_query()`; struct field access goes
through bracket / dot. If you need to flow data between them, project
explicit fields at the boundary.

## Persistence

Struct columns round-trip through `.datum` files like any other kind.
Field metadata (names + per-field types) lives in the column's schema,
not on every row; row payloads carry only the field values. Reference
fields (strings, blobs) inside a struct may spill to a `.datum-blob`
sidecar by the same rules as a top-level reference column.

There's no hard cap on field count, but every field counts against
per-row memory and every field appears in the schema — a struct is the
right tool when the field set is **fixed and small to moderate**, not
when it's wide and sparse.

## See Also

- [Array, Struct & Index Literals](literals.md) — the literal-syntax surface
- [LET Bindings — Tuple Destructuring](let-bindings.md#tuple-destructuring)
- [Type System](type-system.md) — full `DataKind` table and CAST rules
- [Schema Introspection](schema-introspection.md) — `system.types`, `information_schema.columns`
