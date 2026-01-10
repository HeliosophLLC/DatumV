---
title: DDL / DML
---

DatumIngest supports both persistent and session-scoped tables with DDL/DML routed through `TableCatalog`. Persistent tables materialise as `.datum` files alongside the catalog's `.datum-catalog.json`; temp tables live in-memory and die with the catalog.

### Table mutability

Every table registered in the catalog has a mutability level:

| Level | Description |
|-------|-------------|
| `ReadOnly` | Default for all data sources (CSV, Parquet, JSON, system tables). DDL/DML statements return an error. |
| `SessionOwned` | Temp tables created within a session. DDL/DML permitted. |
| `Writable` | Persistent `.datum` tables created via `CREATE TABLE`. DDL/DML permitted. |

`ITableProvider` exposes three opt-in flags — `CanAlterColumns`, `CanAppendRows`, `CanDeleteRows` — gating the corresponding mutation paths. System tables (information_schema, datum_catalog.\*, models, udfs, …) leave the defaults at `false`, so the catalog rejects mutations against them with a clear `"Table 'X' is read-only"` error.

### CREATE TABLE

Creates a persistent table backed by a `.datum` file in the catalog directory:

```sql
CREATE TABLE features (
    customer_id   Int32 PRIMARY KEY,
    tenure_months Int32 NOT NULL,
    monthly_spend Float64,
    label         String
)
```

Persistent `CREATE TABLE` requires a catalog backed by `.datum-catalog.json` (i.e. opened with a catalog path). Without one, use `CREATE TEMP TABLE` instead.

`CREATE TABLE IF NOT EXISTS` silently succeeds when a table with the same name already exists; the existing table's schema is not validated against the new declaration.

#### Type names

Column types use the kind names from the type system: `Int8`, `Int16`, `Int32`, `Int64`, `UInt8`, `UInt16`, `UInt32`, `UInt64`, `Float32`, `Float64`, `Boolean`, `String`, `Uuid`, `Date`, `DateTime`, `Time`, `Duration`. See [Type System](type-system.md) for the full set.

#### Column modifiers

| Modifier | Behavior |
|----------|----------|
| `NOT NULL` | Column rejects NULL values on `INSERT`. |
| `PRIMARY KEY` | Implies `NOT NULL`. Enforces uniqueness on `INSERT` — duplicate key values are rejected with `PrimaryKeyViolationException`. PK columns must be fixed-size kinds (Int*, UInt*, Float*, Boolean, Date/Time/DateTime/Duration, Uuid); the total key size must be ≤ 16 bytes. |
| `DEFAULT <literal>` | Column receives the literal when omitted from an `INSERT`. Accepts string / numeric / boolean / NULL literals and unary-negate over a numeric literal. Function-call defaults are not accepted. |
| `IDENTITY` / `IDENTITY(seed, step)` | Auto-generates an integer value on each `INSERT`. Bare form defaults to `seed=1, step=1`. Step may be negative. At most one `IDENTITY` column per table. The column kind must be a signed or unsigned integer (Int8/16/32/64, UInt8/16/32/64); 128-bit integers are not supported. Explicit values for an `IDENTITY` column are always rejected — drop the column from the `INSERT` column list and the catalog fills it. |

Column-modifier order in the parser is fixed: `NOT NULL → PRIMARY KEY → DEFAULT → IDENTITY`. `Int64 IDENTITY PRIMARY KEY` parse-fails; write `Int64 PRIMARY KEY IDENTITY`.

Composite primary keys are declared with a table-level constraint:

```sql
CREATE TABLE order_products (
    user_id    Int32,
    product_id Int32,
    quantity   Int32,
    PRIMARY KEY (user_id, product_id)
)
```

#### PRIMARY KEY enforcement

Single-column `PRIMARY KEY` is backed by a maintained mutable B+Tree in a `.datum-pkindex` sidecar — uniqueness checks probe the tree in `O(log table_size)` per row. Composite `PRIMARY KEY` falls back to a scan-based check (loads existing PK values into a `HashSet` at `INSERT` start). Both paths reject within-batch duplicates and `NULL` in any PK column.

#### IF NOT EXISTS

`CREATE TABLE IF NOT EXISTS` does a name-only existence check; no schema-equivalence comparison. If a table already exists, the statement is a no-op regardless of whether the requested schema matches.

#### AT 'path'

`CREATE TABLE … AT 'path'` lets the caller place the backing `.datum` file at a specific location instead of the catalog directory. This clause is rejected by default — production catalogs don't honor it. Pass `allowExplicitTablePaths: true` to the `TableCatalog` constructor to opt in (typically test fixtures only).

### CREATE TEMP TABLE

Creates an in-memory table backed by `InMemoryTableProvider`:

```sql
CREATE TEMP TABLE features (
    id    Int32 PRIMARY KEY,
    score Float32
)
```

`CREATE TEMP TABLE` works in any catalog (no `.datum-catalog.json` required). The table dies when the catalog is disposed. Temp tables support all the same column modifiers as persistent tables, including `IDENTITY`, `DEFAULT`, and `PRIMARY KEY` — but PK uniqueness uses the in-memory `HashSet` path (no `.datum-pkindex` sidecar).

### DROP TABLE

Removes a table from the catalog and deletes its backing files:

```sql
DROP TABLE features
DROP TABLE IF EXISTS features
```

For a persistent table this deletes the `.datum` file plus all companion sidecars (`.datum-blob`, `.datum-index`, `.datum-manifest`, `.datum-pkindex`).

### INSERT INTO

Appends rows to an existing table:

```sql
-- Literal values
INSERT INTO features VALUES (1, 'Alice', 0.95), (2, 'Bob', 0.42)

-- Column list (omitted columns get DEFAULT, else NULL, else error)
INSERT INTO features (customer_id, label) VALUES (3, 'churn')

-- From a query
INSERT INTO features (id, name, score)
SELECT id, name, score FROM raw_data WHERE score IS NOT NULL
```

#### Omitted-column resolution

For each target column not supplied by the `VALUES` row or `SELECT` projection:

1. If the column is `IDENTITY`, the catalog reserves the next value.
2. Otherwise if the column has a `DEFAULT`, the literal is used.
3. Otherwise if the column is `Nullable`, `NULL` is written.
4. Otherwise the entire batch is rejected with an error naming the column.

#### Type coercion

Source values are coerced to the target column's `DataKind` losslessly:

- Integer widening (Int8 → Int32) accepted; narrowing rejected at runtime when the value doesn't fit.
- Negative literals into unsigned columns rejected.
- `Float64 → Float32` accepted only when the round-trip is exact (`0.1 → Float32` rejects).
- String → numeric / numeric → string and other cross-family coercions rejected with a clear error.

#### IDENTITY

When a column is declared `IDENTITY`, supplying it in the `INSERT` column list is rejected. The catalog auto-fills it from the prologue's running counter; the counter is updated atomically with the data commit, so values are never reused even across crashes.

#### PRIMARY KEY

Each `INSERT` validates that no row duplicates an existing PK or another row in the same batch. The first violation throws `PrimaryKeyViolationException` and the entire batch is aborted (no partial commit). NULL in any PK column is also rejected.

### DELETE

Soft-deletes rows matching a predicate:

```sql
DELETE FROM features WHERE score IS NULL
DELETE FROM features                       -- unconditional: removes every live row
```

Tombstoned rows are excluded from subsequent queries. Storage is not reclaimed — a future compaction pass rewrites the file. Row indices passed to the provider's `DeleteRows` are linear over the live row sequence; `DELETE` walks a fresh scan and accumulates indices, so tombstones from prior `DELETE` calls don't shift the numbering incorrectly.

### ALTER TABLE ADD COLUMN

Adds a nullable column. Existing rows receive `NULL`:

```sql
ALTER TABLE features ADD COLUMN risk_tier String
ALTER TABLE features ADD COLUMN risk_tier String NULL
ALTER TABLE features ADD [COLUMN] risk_tier String   -- COLUMN keyword optional
```

`DEFAULT`, `NOT NULL`, and computed (`AS expr`) modifiers on `ADD COLUMN` are rejected — adding a non-null or backfilled column to an existing table requires a file rewrite that the format does not yet emit. Add the column nullable first and populate it via `INSERT … SELECT` into a new table.

### ALTER TABLE DROP COLUMN

Soft-drops a column by name:

```sql
ALTER TABLE features DROP COLUMN risk_tier
ALTER TABLE features DROP COLUMN IF EXISTS risk_tier
```

The column block stays in the footer (marked `Tombstoned`) for compaction-time reclamation, but is hidden from `GetSchema()` and from subsequent scans. Dropping a column that's part of the table's `PRIMARY KEY` is rejected.

### Batch execution

Multiple statements can be combined into a single batch. Statements execute sequentially; on failure, execution stops and no further statements run.

```sql
DROP TABLE IF EXISTS features;
CREATE TABLE features (
    id    Int32 PRIMARY KEY IDENTITY,
    name  String NOT NULL,
    score Float32 DEFAULT 0.0
);
INSERT INTO features (name, score) VALUES ('alice', 0.95), ('bob', 0.42);
INSERT INTO features (name) SELECT name FROM raw_data WHERE score IS NOT NULL;
DELETE FROM features WHERE score IS NULL
```

Semicolons between statements are optional — statement parsers are keyword-anchored, so consecutive statements without a separator disambiguate cleanly. Trailing semicolons are silently ignored.

For procedural batches — variables, conditionals, loops, multi-variable `SELECT` assignment — see [Procedural Statements](procedural.md).

## See Also

- [Schema Introspection](schema-introspection.md)
- [Type System](type-system.md)
- [SELECT](select.md)
- [Procedural Statements](procedural.md)
