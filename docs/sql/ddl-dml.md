---
title: DDL / DML
---

DatumIngest supports session-scoped temp tables with full DDL/DML. Temp tables are stored as `.datum` files in a per-session directory; source indexes and column statistics manifests are auto-generated so the query planner has accurate cardinality estimates and can apply chunk pruning.

### Table Mutability

Every table registered in the catalog has a mutability level:

| Level | Description |
|-------|-------------|
| `ReadOnly` | Default for all data sources. DDL/DML statements return an error. |
| `SessionOwned` | Temp tables created within a session. All DDL/DML permitted. |
| `Writable` | Reserved for future use (external mutable sources). |

Attempting `INSERT`, `UPDATE`, `DELETE`, `ALTER TABLE`, or `DROP TABLE` on a read-only table returns an error.

### CREATE TEMP TABLE

Creates a session-scoped table with an explicit column definition:

```sql
CREATE TEMP TABLE features (
    customer_id   INT PRIMARY KEY,
    tenure_months INT NOT NULL,
    monthly_spend FLOAT64,
    label         STRING
)
```

Composite primary keys are declared with a table-level constraint:

```sql
CREATE TEMP TABLE order_products (
    user_id    INT,
    product_id INT,
    quantity   INT,
    PRIMARY KEY (user_id, product_id)
)
```

Supported column modifiers:

| Modifier | Behavior |
|----------|----------|
| `NOT NULL` | Column rejects NULL values on INSERT (both VALUES and SELECT sources). |
| `PRIMARY KEY` | Implies `NOT NULL`. Enforces uniqueness on INSERT — duplicate key values are rejected. UPDATE on a PRIMARY KEY column is prohibited. |

Both inline `col INT PRIMARY KEY` and table-level `PRIMARY KEY (col1, col2)` syntax are supported.
When a table has a primary key, each `INSERT` validates that no new rows duplicate an existing or
in-batch key value. Violations return an error and the entire batch is rejected.

`CREATE TEMP TABLE IF NOT EXISTS` silently succeeds when the table already exists.

### CREATE TEMP TABLE AS SELECT

Creates and populates a temp table from a query in a single statement:

```sql
CREATE TEMP TABLE features AS
SELECT customer_id, tenure_months, total_charges / NULLIF(tenure_months, 0) AS avg_spend
FROM customers
```

The schema is inferred from the query output. A source index and column statistics manifest are auto-generated after materialization when the table contains rows.

### INSERT INTO

Appends rows to an existing table:

```sql
-- Literal values
INSERT INTO features VALUES (1, 'Alice', 0.95), (2, 'Bob', 0.42)

-- Column list (unmapped columns filled with NULL)
INSERT INTO features (customer_id, label) VALUES (3, 'churn')

-- From a query
INSERT INTO features SELECT id, name, score FROM raw_data WHERE score IS NOT NULL
```

NOT NULL columns are validated before rows are appended — a NULL value in any non-nullable column
rejects the entire batch with an error. A source index and column statistics manifest are auto-rebuilt
after each INSERT into a session-owned table.

### UPDATE

Replaces column values in a table. Supports constant literals, arbitrary expressions
(referencing the same row), and WHERE predicates:

```sql
UPDATE features SET label = 'retain'
UPDATE features SET score = score * 1.1 WHERE status = 'active'
UPDATE features SET label = category, score = score + 0.05
```

#### UPDATE...FROM (join-based enrichment)

Follows PostgreSQL semantics. The target table is **not** repeated in the FROM clause;
the WHERE clause provides both the join condition and any additional row filters:

```sql
-- Enrich a feature table from a raw-scores source
UPDATE features SET score = raw.value
FROM raw
WHERE features.id = raw.id

-- With an explicit target alias
UPDATE features AS f SET score = raw.value * 1.1
FROM raw
WHERE f.id = raw.id

-- Multi-table join: features ← raw ← model
UPDATE features SET score = raw.value * m.weight
FROM raw
JOIN model AS m ON raw.model_id = m.id
WHERE features.id = raw.id
```

SET column names are always unqualified. SET expressions can reference columns from both
the target table and source tables using qualified form (`alias.column`).

When multiple source rows match the same target row, the last match wins (indeterminate
order, matching PostgreSQL documented behavior).

UPDATE on a PRIMARY KEY column is not permitted. To change a row's key, DELETE the row
and re-INSERT with the new key values.

### DELETE

Removes rows using tombstone bitmaps:

```sql
DELETE FROM features WHERE score IS NULL
```

Tombstoned rows are excluded from subsequent queries. The underlying storage is not compacted.

### ALTER TABLE ADD COLUMN

Adds a column to an existing table:

```sql
ALTER TABLE features ADD COLUMN risk_tier STRING
ALTER TABLE features ADD COLUMN flag INT NOT NULL DEFAULT 0
```

Existing rows receive the `DEFAULT` value (or NULL when no default is specified).

#### Computed Columns

Use `AS expr` to derive a column from existing columns. The expression is evaluated
against every existing row and the result is persisted (materialized):

```sql
ALTER TABLE features ADD COLUMN total FLOAT64 AS price * quantity
ALTER TABLE features ADD COLUMN upper_name STRING AS UPPER(name)
ALTER TABLE features ADD COLUMN flag BOOLEAN NOT NULL AS score > 0.5
```

`DEFAULT` and `AS` are mutually exclusive — a column is either constant-filled or computed, not both.
The expression can reference any column that exists at the time of the ALTER, and supports
arithmetic, function calls, CASE, CAST, and all operators available in SELECT expressions.

### DROP TABLE

Removes a table from the catalog and deletes its backing file:

```sql
DROP TABLE features
DROP TABLE IF EXISTS features
```

### ANALYZE

Rebuilds the source index and column statistics manifest for a table:

```sql
ANALYZE features
```

Use `ANALYZE` after a series of mutations (`UPDATE`, `DELETE`, `ALTER TABLE ADD COLUMN`) to refresh statistics for the query planner. `INSERT` and `CREATE TEMP TABLE AS SELECT` auto-generate sidecars, so `ANALYZE` is only needed after other DDL/DML operations.

Follows the PostgreSQL convention — no `TABLE` keyword required.

### Batch Execution

Multiple statements can be separated by semicolons:

```sql
DROP TABLE IF EXISTS features;
CREATE TEMP TABLE features AS SELECT * FROM raw_data;
ALTER TABLE features ADD COLUMN risk FLOAT64 DEFAULT 0.0;
ALTER TABLE features ADD COLUMN total FLOAT64 AS price * quantity;
UPDATE features SET risk = 0.9 WHERE churn_score > 0.8;
ANALYZE features
```

Statements execute sequentially. On failure, execution stops and no further statements run.

## See Also

- [Schema Introspection](schema-introspection.md)
- [Type System](type-system.md)
- [SELECT](select.md)
