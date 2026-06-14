---
title: Cryptographic Hash Functions
category: crypto
---

# Cryptographic Hash Functions

Cryptographic hash functions backed by the .NET runtime. All hashes accept a `String` (hashed as UTF-8) or a `UInt8[]` byte array. Null input propagates to null output.

`md5` returns a 32-character lowercase hex `String` for PostgreSQL compatibility; every other hash returns the raw digest as `UInt8[]`. Pair them with [`encode`](encoding.md#encode) to get a hex or base64 string.

## md5

`md5(input)` → String | QU: 1

MD5 digest as a 32-character lowercase hex string. Accepts `String` (UTF-8) or `UInt8[]`. PostgreSQL-compatible.

```sql
SELECT md5('hello world')
-- 5eb63bbbe01eeed093cb22bb8f5acdc3
```

## sha1

`sha1(input)` → UInt8[] | QU: 1

SHA-1 digest as a 20-byte `UInt8[]`. Accepts `String` (UTF-8) or `UInt8[]`. SHA-1 is collision-broken; use `sha256` for new code.

```sql
SELECT encode(sha1('hello world'), 'hex')
-- 2aae6c35c94fcfb415dbe95f408b9ce91ee846ed
```

## sha256

`sha256(input)` → UInt8[] | QU: 1

SHA-256 digest as a 32-byte `UInt8[]`. Accepts `String` (UTF-8) or `UInt8[]`.

```sql
SELECT encode(sha256(payload), 'hex') AS digest FROM events
```

## sha384

`sha384(input)` → UInt8[] | QU: 1

SHA-384 digest as a 48-byte `UInt8[]`. Accepts `String` (UTF-8) or `UInt8[]`.

## sha512

`sha512(input)` → UInt8[] | QU: 1

SHA-512 digest as a 64-byte `UInt8[]`. Accepts `String` (UTF-8) or `UInt8[]`.

```sql
SELECT encode(sha512(token), 'base64') AS digest FROM api_keys
```

## digest

`digest(data, algorithm)` → UInt8[] | QU: 1

pgcrypto-style dispatcher: hashes `data` (`String` or `UInt8[]`) with the named algorithm and returns the raw digest as `UInt8[]`. Algorithm names are case-insensitive and ignore hyphens, underscores, and spaces — `sha256`, `SHA-256`, and `Sha 256` all select the same algorithm.

Supported algorithms: `md5`, `sha1`, `sha256`, `sha384`, `sha512`.

```sql
SELECT encode(digest(body, 'sha256'), 'hex') AS body_hash FROM uploads
```

Calling `digest` with `sha224` raises an error — SHA-224 is not in the .NET BCL. Use `sha256` instead. An unknown algorithm name raises a function argument error listing the supported set.

## See Also

- [Encoding & Decoding Functions](encoding.md) — convert digests to and from hex, base64, or escape text
- [UUID Functions](uuid.md) — UUID generation, parsing, and timestamp extraction
- [Random & Sampling Functions](random.md) — `hash_split` for reproducible train/val/test splits
