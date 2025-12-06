---
title: JSON Functions
category: json
---

JSON values are stored as canonical CBOR (RFC 7049 §3.9) inside the engine — a binary representation that's compact, fast to walk, and produces a single bit-pattern per logical value (so `=`, hashing, GROUP BY, and DISTINCT on `Json` values work via raw byte equality without re-parsing).

The canonical pattern is **parse once, read many**: `json_parse` runs the JSON tokeniser one time and emits CBOR into the arena; subsequent `json_value` / `json_query` calls walk those CBOR bytes by path with no further parsing. This matches the typical LLM use case — extract structured output from a model, store the parsed result in a [`LET` binding](../sql/let-bindings.md), then read multiple fields from the same document.

```sql
SELECT
  LET parsed = json_parse(models.llama('Extract: ' || raw_text)),
  json_value(parsed, '$.name') AS name,
  json_value(parsed, '$.age') AS age,
  json_value(parsed, '$.email') AS email
FROM documents
```

## Path syntax

The `json_value` and `json_query` functions accept a small JSONPath subset:

| Form | Meaning |
|---|---|
| `$` | The whole document. |
| `$.field` | Descend into an object's named field. |
| `$.foo.bar` | Chained field descent. |
| `$.arr[N]` | Descend into an array's N-th element (zero-based). |
| `$.foo[2].bar` | Combinations work in any order. |

Not supported: wildcards (`[*]`), recursive descent (`..`), filter expressions (`?(...)`), negative indices.

Missing keys, out-of-range indices, and type mismatches (descending into a non-container) all return SQL NULL rather than throwing.

### json_parse

`json_parse(text: String) → Json` | QU: 5

Parses a JSON text string into a `Json` value backed by canonical CBOR. Null input returns null. Throws when the input is not valid JSON, and when a number exceeds `Int64` range and is not finite as `Float64` (the engine's conservative number policy — see "Number representation" below).

```sql
-- Parse an LLM response and pull fields from it.
SELECT
  LET doc = json_parse('{"name": "Alice", "age": 30, "tags": ["admin", "vip"]}'),
  json_value(doc, '$.name') AS name,
  json_value(doc, '$.age') AS age,
  json_query(doc, '$.tags') AS tags
```

The `String → Json` cast is equivalent to `json_parse`.

### json_value

`json_value(doc: Json, path: String) → typed scalar` | QU: 3

Extracts a scalar value at `path` from a `Json` document. Returns the typed scalar — `Int64` for an integer field, `Float64` for a float, `String` for a string, `Boolean` for a boolean, typed null for JSON's `null`. Returns SQL NULL when the path is missing or resolves to an object/array (use `json_query` for those).

Because `json_value` returns a typed scalar (not a string), `typeof()` works naturally on the result:

```sql
SELECT
  LET doc = json_parse('{"score": 0.97, "label": "cat"}'),
  typeof(json_value(doc, '$.score')) AS score_type,  -- 'Float64'
  typeof(json_value(doc, '$.label')) AS label_type   -- 'String'
```

Path traversal is O(n) on the source CBOR (no offset table). For LLM-output documents (tens to hundreds of fields) each lookup is microseconds; far faster than re-parsing the JSON text.

### json_query

`json_query(doc: Json, path: String) → Json` | QU: 4

Extracts an object or array subdocument at `path` as a new `Json` value. Returns SQL NULL when the path is missing or resolves to a scalar (use `json_value` for scalars).

Subdocument extraction is implemented as a slice view over the source CBOR bytes — no copy in the function-chain path. At the arena boundary (when the value is materialised into a column), only the slice's bytes are copied (not the whole source document).

```sql
SELECT
  LET doc = json_parse('{"user": {"name": "Bob", "addr": {"city": "NYC"}}}'),
  json_query(doc, '$.user') AS user,
  json_value(json_query(doc, '$.user.addr'), '$.city') AS city  -- 'NYC'
```

### json_to_text

`json_to_text(doc: Json) → String` | QU: 5

Re-emits a `Json` value as JSON text. Used by output writers and explicit `CAST(value AS String)`. Round-trip is structurally identical but not byte-identical to the source JSON: the canonical CBOR encoding chosen at parse time may have collapsed equivalent representations (e.g. `1.0` → `1` when the value is exactly integral, map keys re-sorted in length-then-lex order).

```sql
SELECT json_to_text(json_parse('{"b": 2, "a": 1}'))
-- Result: '{"a":1,"b":2}'  (keys sorted by Canonical mode)
```

The `Json → String` cast is equivalent to `json_to_text`.

## Number representation

JSON has one number type; CBOR distinguishes integers from floats. The engine's number policy at parse time is conservative:

- Integers that fit in `Int64` encode as CBOR signed integers (smallest-fit per Canonical mode — a value like `5` is one byte, not nine).
- Integers in the unsigned range that exceed `Int64.MaxValue` encode as CBOR unsigned integers (`UInt64`).
- Numbers with fractional parts encode as CBOR `Float64`.
- Numbers exceeding both `Int64`/`UInt64` and finite `Float64` representation throw at parse time rather than silently downcasting.

Integer fields read back via `json_value` come out as `Int64` (the narrowest type that always fits the canonical encoding).

## Equality and ordering

Two `Json` values compare equal via `=` when their canonical CBOR bytes are byte-identical. Because Canonical mode normalises map key ordering and integer encoding, `{"a":1,"b":2}` and `{"b":2,"a":1}` produce identical CBOR and therefore compare equal. This makes `GROUP BY json_col` and `DISTINCT` over JSON columns work without decoding.

`Json` values are not orderable — `<`, `>`, `ORDER BY` on a `Json` column produce no defined ordering. Extract a comparable scalar via `json_value` first.

## See Also

- [Type System](../sql/type-system.md) — `Json` as a `DataKind`, `String ↔ Json` casts.
- [LET Bindings](../sql/let-bindings.md) — the parse-once, read-many pattern.
