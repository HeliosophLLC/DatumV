---
title: Encoding & Decoding Functions
category: encoding
---

# Encoding & Decoding Functions

[← Back to Functions](string.md) · [SQL Reference](../sql/select.md)

Convert between byte arrays (`UInt8[]`) and text using one of three formats. PostgreSQL-compatible: `encode`/`decode` are the same surface and the same format set (`'base64'`, `'hex'`, `'escape'`) as Postgres' bytea encoders. The format string is case-insensitive. Null input propagates to null output.

## encode

`encode(bytes, format)` → String | QU: 1

Encode a byte array as text. Supported formats:

- **`'hex'`** — lowercase hexadecimal, two characters per byte.
- **`'base64'`** — standard base64 (`+`, `/`, `=` padding), emitted unbroken. Postgres breaks output at 76 characters with newlines; this implementation does not, so the result round-trips through `decode` without preprocessing.
- **`'escape'`** — PostgreSQL bytea escape format: zero bytes and high-bit bytes become `\nnn` (three octal digits), backslashes double to `\\`, other bytes pass through as ASCII.

```sql
-- Hex digest from a cryptographic hash
SELECT encode(sha256(payload), 'hex') AS digest FROM events

-- Base64 for transport
SELECT encode(thumbnail_bytes, 'base64') AS data_url_body FROM uploads
```

An unknown format raises a function argument error.

## decode

`decode(text, format)` → UInt8[] | QU: 1

Inverse of `encode`. Parses a text value back into a byte array using the same `'hex'` / `'base64'` / `'escape'` formats. Malformed input raises a function argument error with the position and reason.

```sql
-- Decode a base64-encoded blob back to bytes
SELECT decode(encoded_payload, 'base64') AS payload FROM messages

-- Verify a hex digest matches recomputing the hash
SELECT * FROM uploads
WHERE decode(expected_sha256_hex, 'hex') = sha256(body)
```

For the `'escape'` format, `decode` accepts `\\` for a literal backslash and `\nnn` for an octal byte; all other ASCII bytes pass through. High-bit (non-ASCII) characters in the source string raise an error — they must be written as `\nnn` escapes.

## See Also

- [Cryptographic Hash Functions](crypto.md) — `md5`, `sha1`, `sha256`, `sha384`, `sha512`, and the `digest` dispatcher
- [UUID Functions](uuid.md) — UUID generation and inspection
- [String Functions](string.md) — text manipulation, including `length` and `octet_length`
