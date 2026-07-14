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

Notes:

- `timestamptz` values are stored and compared as UTC instants regardless of the session time zone, and currently also render in UTC. Use [`AT TIME ZONE`](type-system.md#at-time-zone) to convert a value to a specific zone's wall-clock time.
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
