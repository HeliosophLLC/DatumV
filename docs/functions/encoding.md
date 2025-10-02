---
title: Encoding & Hashing Functions
category: encoding
---

# Encoding & Hashing Functions

[< Back to Functions Reference](string.md) | [SQL Reference](../sql/select.md)

Functions for UUID generation and inspection, cryptographic hashing, base encoding, and categorical feature encoding.

## UUID

### uuidv4

`uuidv4()` -> Uuid | QU: 1

Generate a random version 4 UUID (PG 18).

```sql
SELECT uuidv4() AS random_id FROM data
```

### gen_random_uuid

`gen_random_uuid()` -> Uuid | QU: 1

Alias for `uuidv4()`.

```sql
SELECT gen_random_uuid() AS id FROM data
```

### uuidv7

`uuidv7([shift])` -> Uuid | QU: 1

Generate a time-ordered version 7 UUID. Optional Duration shift offsets the embedded timestamp (PG 18).

```sql
-- Generate a UUID with a timestamp shifted 1 hour into the future
SELECT uuidv7(make_duration(0, 1, 0, 0)) AS future_id FROM data
```

### is_uuid

`is_uuid(str)` -> Boolean | QU: 1

Returns Boolean -- whether the string is a valid UUID.

```sql
SELECT CAST(id_column AS Uuid) AS id FROM raw_data WHERE is_uuid(id_column)
```

### uuid_str

`uuid_str(uuid)` -> String | QU: 1

Format UUID as lowercase hyphenated string.

### uuid_bytes

`uuid_bytes(uuid)` -> UInt8Array | QU: 1

Extract UUID as 16-byte UInt8Array (big-endian).

```sql
-- Hash composite keys using UUID bytes
SELECT sha256(bytes_concat(uuid_bytes(id1), uuid_bytes(id2))) AS composite_hash FROM joins
```

### uuid_extract_version

`uuid_extract_version(uuid)` -> Int16 | QU: 1

Extract version number as Int16 from an RFC 9562 UUID. Returns null for non-RFC 9562 variants (PG 18).

```sql
SELECT uuid_extract_version(event_id) AS uuid_ver FROM events
```

### uuid_extract_timestamp

`uuid_extract_timestamp(uuid)` -> DateTime | QU: 1

Extract embedded timestamp from a v1 or v7 UUID as DateTime. Returns null for other versions (PG 18).

```sql
-- Extract timestamp from v7 UUIDs for temporal analysis
SELECT uuid_extract_timestamp(event_id) AS created_at FROM events
```

## Byte Array

### bytes_concat

`bytes_concat(a, b, ...)` -> UInt8Array | QU: 1

Concatenate two or more byte arrays. Null args treated as empty.

### bytes_slice

`bytes_slice(bytes, start, len)` -> UInt8Array | QU: 1

Extract sub-array by position and length (0-based, clamped).

### bytes

`bytes(a, b, ...)` -> UInt8Array | QU: 1

Construct a byte array from integer values (each 0-255). Values outside this range produce an error.

## Hashing

### md5

`md5(str)` -> String | QU: 2

MD5 hash as lowercase hex string. PostgreSQL compatible.

### md5_bytes

`md5_bytes(val)` -> UInt8Array | QU: 2

MD5 hash as UInt8Array. Accepts String (UTF-8) or UInt8Array.

### sha256

`sha256(val)` -> UInt8Array | QU: 2

SHA-256 hash as UInt8Array. Accepts String (UTF-8) or UInt8Array. Use `hex_encode()` for hex digest.

```sql
SELECT hex_encode(sha256(name)) AS name_hash FROM users
```

### sha512

`sha512(val)` -> UInt8Array | QU: 2

SHA-512 hash as UInt8Array. Accepts String (UTF-8) or UInt8Array. Use `hex_encode()` for hex digest.

### crc32

`crc32(val)` -> Float32 | QU: 2

CRC-32 checksum as Float32. Accepts String (UTF-8) or UInt8Array.

## Base Encoding

### base64_encode

`base64_encode(bytes)` -> String | QU: 1

Encode byte array as Base64 string.

### base64_decode

`base64_decode(str)` -> UInt8Array | QU: 1

Decode Base64 string to byte array.

### hex_encode

`hex_encode(bytes)` -> String | QU: 1

Encode byte array as lowercase hex string.

### hex_decode

`hex_decode(str)` -> UInt8Array | QU: 1

Decode hex string to byte array.

## Categorical Encoding

### one_hot

`one_hot(value, label1, label2, ...)` -> Vector | QU: 1

One-hot encode against an explicit domain. Returns Vector[K] with 1.0 at matching index; zero vector for unknown values.

```sql
-- Low-cardinality: explicit domain one-hot
SELECT one_hot(color, 'red', 'green', 'blue') AS color_vec
FROM products
```

### one_hot_unk

`one_hot_unk(value, label1, label2, ...)` -> Vector | QU: 1

One-hot encode with unknown bucket. Returns Vector[K+1]; unknown values activate the last dimension.

```sql
-- With unknown bucket for unseen categories
SELECT one_hot_unk(species, 'cat', 'dog', 'bird') AS species_vec
FROM animals
```

### label_encode

`label_encode(value, label1, label2, ...)` -> Float32 | QU: 1

Label-encode against an explicit domain. Returns the zero-based Float32 index; -1 for unknown values.

```sql
-- Label encoding for ordinal features
SELECT label_encode(size, 'S', 'M', 'L', 'XL') AS size_idx
FROM orders
```

### label_encode_unk

`label_encode_unk(value, label1, label2, ...)` -> Float32 | QU: 1

Label-encode with unknown bucket. Returns the zero-based Float32 index; K (domain size) for unknown values.

### hash_encode

`hash_encode(value, num_buckets)` -> Vector | QU: 2

Feature-hash a string into a fixed-size one-hot Vector. Uses XxHash32 modulo num_buckets. Handles any cardinality without an explicit domain.

```sql
-- High-cardinality: feature hashing (no vocabulary needed)
SELECT hash_encode(zip_code, 256) AS zip_features
FROM addresses
```

## See Also

- [Temporal Functions](temporal.md) -- date/time construction and extraction
- [Aggregate Functions](aggregate.md) -- grouping and reduction functions
- [SQL Reference](../sql/select.md) -- full SQL dialect documentation
