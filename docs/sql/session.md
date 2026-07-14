---
title: Session Settings
---

# Session Settings

Session settings are per-catalog state that SQL statements can read and change at runtime. Two settings exist: the schema **search path** and the session **time zone**. Both follow PostgreSQL syntax.

## SET TIME ZONE

```sql
SET TIME ZONE 'America/New_York';
SET TIME ZONE UTC;
SET TIME ZONE DEFAULT;        -- reset to UTC
SET TIME ZONE LOCAL;          -- same as DEFAULT

-- Configuration-parameter spellings (equivalent):
SET timezone = 'America/New_York';
SET timezone TO 'America/New_York';
SET time_zone TO 'Europe/Paris';
```

Sets the session time zone. Zone names are IANA identifiers (`America/New_York`, `Europe/Paris`) or fixed-zone names (`UTC`, `GMT`); Windows zone ids also resolve. Unknown names raise an error and leave the setting unchanged. The default is UTC.

The session time zone drives the interpretation and extraction of `timestamptz` values (storage is always UTC instants):

- **Bare literals and string casts** — `TIMESTAMPTZ '2026-01-15 12:00:00'` (and any string→`timestamptz` cast or INSERT coercion without an explicit offset) interprets the wall clock in the session zone. An explicit offset (`+05`, `Z`) always wins. Plain `timestamp` literals take the wall clock as written; an explicit offset is ignored.
- **Casts** — `timestamp → timestamptz` anchors the naive wall clock in the session zone; `timestamptz → timestamp` converts to the session zone's wall clock and drops the zone. `date → timestamptz` is the session zone's midnight; `timestamptz → date` is the session zone's calendar date at that instant.
- **`date_part` / `EXTRACT`** — wall-clock fields (`hour`, `day`, …) read the session zone's clock face; `epoch` is zone-independent; the `timezone` / `timezone_hour` / `timezone_minute` fields report the session zone's UTC offset at that instant (DST-aware).
- **`date_trunc`** — truncates at session-zone boundaries: `date_trunc('day', …)` lands on the session zone's midnight.
- **`CURRENT_DATE` / `CURRENT_TIME`** — read the session zone's clock face. `CURRENT_TIMESTAMP` / `now()` return an absolute instant, zone-independent.

A `SET TIME ZONE` takes effect from the next statement onward, including later statements in the same batch; the zone is stable within a single statement.

Display and export also follow the session zone:

- **Result display** — `timestamptz` cells render as the session zone's wall clock (`2026-01-15 12:00:00` for a 17:00 UTC instant under `America/New_York`).
- **CSV / JSON export** — `timestamptz` values render in ISO 8601 with the session zone's offset (`2026-01-15T12:00:00.0000000-05:00`). The instant is preserved: re-importing parses the offset back to the same UTC ticks.
- **Arrow / Parquet export** — deliberately session-independent: `timestamptz` is a UTC-normalized instant with UTC schema metadata regardless of the session zone, so schema fingerprints and binary round-trips never vary with `SET TIME ZONE`.

Notes:

- Comparison-time implicit coercion (`WHERE tz_col > '2026-01-15 12:00'` with a string literal) anchors bare wall clocks to UTC, not the session zone. Use an explicit `TIMESTAMPTZ '…'` literal or cast for session-zone interpretation.
- PG's numeric-offset form (`SET TIME ZONE -7`) and interval form are not supported; name the zone instead.
- The setting is session-scoped, not persisted: a reopened catalog starts back at UTC.

## SET search_path

```sql
SET search_path = myapp, public;
SET search_path TO myapp, public;
```

Replaces the schema search path used to resolve unqualified table names. The leftmost schema is consulted first. Every named schema must exist; unknown schemas raise an error rather than being silently accepted.

## SHOW

```sql
SHOW timezone;      -- one row, column "TimeZone":    'UTC'
SHOW TIME ZONE;     -- same
SHOW search_path;   -- one row, column "search_path": 'public, system'
```

Reports the current value of a session setting as a single-row, single-column result. Unknown setting names are rejected at plan time. The value is read when the statement executes, so `SET TIME ZONE 'x'; SHOW timezone` in one batch reports the new value.

The same values are available inside expressions via [`current_setting`](../functions/utility.md#current_setting):

```sql
SELECT current_setting('timezone'), current_setting('search_path');
```

## Interaction with procedural SET

`SET name = value` at statement level assigns a procedural variable (see [Procedural Statements](procedural.md#variables)). The names `timezone`, `time_zone`, and `search_path` are reserved in `SET` position for the session settings above and cannot be used as procedural variable names there.
