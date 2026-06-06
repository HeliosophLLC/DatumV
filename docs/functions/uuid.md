---
title: UUID Functions
category: uuid
---

# UUID Functions

[← Back to Functions](string.md) · [SQL Reference](../sql/select.md)

UUID generation, formatting, and inspection. Versions 4 and 7 cover the common cases — v4 for random identifiers, v7 for time-ordered identifiers that sort by creation time. The extraction functions follow RFC 9562 / PostgreSQL 18 semantics.

## uuidv4

`uuidv4()` → Uuid | QU: 1

Generate a random version 4 UUID.

```sql
SELECT uuidv4() AS id FROM data
```

## gen_random_uuid

`gen_random_uuid()` → Uuid | QU: 1

Alias for `uuidv4()`. PostgreSQL-compatible spelling.

```sql
SELECT gen_random_uuid() AS id FROM data
```

## uuidv7

`uuidv7([shift Interval])` → Uuid | QU: 1

Generate a time-ordered version 7 UUID. The high bits encode the current Unix timestamp at millisecond precision, so v7 UUIDs sort by creation time and cluster well on B-tree indexes.

The optional `shift` argument is an `Interval` that offsets the embedded timestamp. Useful for backfill, time-travel testing, or any workload that needs UUIDs sorted into a non-current window. Matches the Postgres-18 signature.

```sql
-- Current-time UUIDs
SELECT uuidv7() AS id FROM data

-- Backfill: produce UUIDs as if they were generated a year ago
SELECT uuidv7(INTERVAL '-1 year') AS legacy_id FROM data
```

## uuid_str

`uuid_str(uuid)` → String | QU: 1

Format a UUID as its canonical lowercase hyphenated string (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`).

```sql
SELECT uuid_str(id) AS id_text FROM events
```

## uuid_extract_version

`uuid_extract_version(uuid)` → Int16 | QU: 1

Returns the version number of an RFC 9562 UUID (1–8). Returns NULL for non-RFC 9562 variants (the nil UUID, the max UUID, or Microsoft GUIDs).

```sql
SELECT uuid_extract_version(uuidv7())  -- 7
```

## uuid_extract_timestamp

`uuid_extract_timestamp(uuid)` → TimestampTz | QU: 1

Returns the embedded timestamp from a version 1, 6, or 7 UUID. Returns NULL for other versions (v4 has no embedded time) or non-RFC 9562 variants. v7 timestamps have millisecond precision; v1 timestamps have 100-nanosecond precision.

```sql
-- Filter rows by the time embedded in the row's id
SELECT * FROM events WHERE uuid_extract_timestamp(id) > '2026-01-01'
```

## See Also

- [Cryptographic Hash Functions](crypto.md) — hash UUIDs into bucket keys via `digest(uuid_str(id), 'sha256')`
- [Temporal Functions](temporal.md) — date/time construction, extraction, and arithmetic
- [Random & Sampling Functions](random.md) — `hash_split` for reproducible splits keyed on UUIDs
