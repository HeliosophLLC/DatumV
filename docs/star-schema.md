# Star Schema Detection

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Language Server](language-server.md) · [Programmatic API](api.md) · [Compute Backend](compute.md)

`StarSchemaDetector` discovers hub/spoke relationships from table manifests without executing any queries. A hub is a table with a unique key column that connects to multiple spoke tables via foreign key relationships. The detector reuses the column matching and evidence scoring infrastructure to find candidates, then groups them by hub table.

## Algorithm

Detection runs in three phases:

1. **Column matching** — All table pairs are compared using `ColumnMatcher.FindCandidatePairs()`. Candidate pairs pass through name similarity (Levenshtein with `_id`/`_key` suffix bonuses), type compatibility, and column role gate filters.

2. **Evidence scoring** — Each column match is scored by `JoinEvidenceScorer.ScoreEvidence()`, producing a composite confidence from six signals (name, type, TopK Jaccard, cardinality ratio, range overlap, unique key). Candidates below `CandidateMinConfidence` (default 0.45) are rejected.

3. **Hub extraction** — Surviving candidates are classified by cardinality (`OneToMany`, `ManyToOne`, `OneToOne`, or `ManyToMany`). Many-to-many pairs are excluded. The remaining candidates are grouped by (hub table, key column), and groups with at least `MinSpokeCount` (2) spokes become hubs.

## Evidence signals

Each candidate pair carries a `JoinEvidence` object with independent signals and a weighted composite:

| Signal | Range | Description |
|--------|-------|-------------|
| `NameSimilarity` | 0–1 | Levenshtein-based similarity with suffix bonuses for `_id`, `_key`, `_code`. |
| `TypeCompatibility` | 0–1 | 1.0 exact match, 0.8 coercible (Scalar↔UInt8, Date↔DateTime), 0.5 String↔JsonValue, 0.0 incompatible. |
| `TopKJaccard` | 0–1 | Jaccard similarity of TopK value sets (case-insensitive, skips continuous numerics). When both columns have a [vocabulary sidecar](#vocabulary-sidecars), exact Jaccard from the full value sets replaces this estimate. |
| `CardinalityRatio` | 0–1 | min(NDV) / max(NDV). Close to 1.0 suggests both columns draw from the same domain. |
| `RangeOverlap` | 0–1 or null | Intersection / union of numeric [min, max] ranges. Null for non-numeric columns. |
| `NullKeyRatio` | 0–1 | Maximum null ratio across both columns. High values indicate risky join keys. |
| `UniqueKeyScore` | 0 or 1 | 1.0 if NDV ≈ RowCount on at least one side, 0.0 otherwise. |
| `ExactJaccard` | 0–1 or null | Exact Jaccard from vocabulary value sets. Null when either column lacks a vocabulary. |
| `ContainmentLeftInRight` | 0–1 or null | Fraction of left vocabulary values present in the right vocabulary. Null without vocabularies. |
| `ContainmentRightInLeft` | 0–1 or null | Fraction of right vocabulary values present in the left vocabulary. Null without vocabularies. |
| `CompositeConfidence` | 0–1 | Weighted combination. Default weights: name 0.30, type 0.15, TopK 0.20, cardinality 0.15, range 0.10, unique key 0.10. When `RangeOverlap` is null, its weight redistributes proportionally. When `ExactJaccard` is available, it replaces `TopKJaccard` in the composite. |

## Join classification

`EstimatedJoinType` is one of:

| Value | Meaning |
|-------|---------|
| `OneToOne` | Both sides have near-unique keys (NDV/RowCount ≥ 0.95) — one row matches at most one row. |
| `OneToMany` | Left side has unique keys; right side may have duplicates. |
| `ManyToOne` | Right side has unique keys; left side may have duplicates. |
| `ManyToMany` | Neither side has unique keys — excluded from star schema detection. |

## Cardinality handling

| Classification | Hub assignment |
|----------------|----------------|
| `OneToMany` | Left table is the hub, right table is the spoke. |
| `ManyToOne` | Right table is the hub, left table is the spoke. |
| `OneToOne` | Added in both directions — both tables are tried as hub. The table that accumulates enough spokes from other relationships naturally becomes the hub via the `MinSpokeCount` filter. |
| `ManyToMany` | Excluded — neither side has unique keys. |

One-to-one pairs arise when the "many" side has a near-unique foreign key (NDV/RowCount ≥ 0.95). For example, in the Olist dataset, `olist_order_payments_dataset.order_id` has NDV 99,683 across 103,886 rows (ratio 0.9595). Both payments and orders pass the uniqueness threshold, so the join is classified as one-to-one. Without one-to-one support, the orders table would only have order_items as a spoke (below the minimum), and no star schema would be detected.

## Deduplication

When multiple candidates connect the same hub to the same spoke on the same key (e.g., via different column pairs), only the highest-confidence candidate is retained.

## Vocabulary sidecars

When a column's estimated distinct count is below the vocabulary threshold, the statistics collector builds an exhaustive set of all distinct values and persists it as a `.datum-vocab` sidecar file. Unlike TopK (which only captures the most frequent values), vocabularies capture the complete value domain.

Vocabularies enhance star schema detection by enabling exact set metrics:

| Metric | TopK-only | With vocabulary |
|--------|-----------|-----------------|
| Jaccard | Approximate (TopK intersection / union) | Exact (full set intersection / union) |
| Containment | Not available | Exact directional containment (L⊂R, R⊂L) |

These metrics feed into the composite confidence score used by `StarSchemaDetector`. When both columns in a candidate pair have vocabularies, the exact Jaccard replaces the TopK Jaccard in the weighted composite.

## Output

`StarSchemaResult` contains:

| Field | Type | Description |
|-------|------|-------------|
| `Tables` | `string[]` | All table names that were analyzed. |
| `Hubs` | `HubTable[]` | Discovered hubs, ordered by descending spoke count, then table name. |
| `UnmatchedTables` | `string[]` | Tables not participating in any hub/spoke relationship. |

Each `HubTable` has:

| Field | Type | Description |
|-------|------|-------------|
| `TableName` | `string` | The hub table name. |
| `KeyColumns` | `string[]` | The unique key column(s) on the hub side. |
| `Spokes` | `SpokeTable[]` | Connected spoke tables, ordered by descending confidence. |
| `SpokeCount` | `int` | Number of spokes (shortcut for `Spokes.Count`). |

Each `SpokeTable` has:

| Field | Type | Description |
|-------|------|-------------|
| `TableName` | `string` | The spoke table name. |
| `ForeignKeyColumns` | `string[]` | The foreign key column(s) on the spoke side. |
| `Confidence` | `double` | Evidence-weighted join confidence. |
| `JoinClassification` | `JoinClassification` | The cardinality classification (`OneToMany`, `ManyToOne`, or `OneToOne`). |

## C# usage

```csharp
StarSchemaResult result = StarSchemaDetector.Detect(manifests);

foreach (HubTable hub in result.Hubs)
{
    Console.WriteLine($"Hub: {hub.TableName} (key: {string.Join(", ", hub.KeyColumns)})");

    foreach (SpokeTable spoke in hub.Spokes)
    {
        Console.WriteLine($"  ← {spoke.TableName} [{string.Join(", ", spoke.ForeignKeyColumns)}] " +
            $"confidence={spoke.Confidence:F2} join={spoke.JoinClassification}");
    }
}
```

## CLI

The `star-schema` command reads all registered sources, builds manifests, runs star schema detection, and writes the result as JSON.

```bash
datum-ingest star-schema --source orders.csv --source items.csv --source payments.csv --output star.json
```

When `--output` is omitted, the JSON is written to stdout. Sources can also be loaded from a catalog file:

```bash
datum-ingest star-schema --catalog catalog.json --output star.json
```

### JSON output format

```json
{
  "tables": ["orders", "items", "payments"],
  "hubs": [
    {
      "tableName": "orders",
      "keyColumns": ["order_id"],
      "spokes": [
        {
          "tableName": "items",
          "foreignKeyColumns": ["order_id"],
          "confidence": 0.82,
          "joinClassification": "OneToMany"
        },
        {
          "tableName": "payments",
          "foreignKeyColumns": ["order_id"],
          "confidence": 0.78,
          "joinClassification": "OneToOne"
        }
      ],
      "spokeCount": 2
    }
  ],
  "unmatchedTables": []
}
```

### Serialization API

`ManifestSerializer` provides methods for star schema JSON round-tripping:

```csharp
// Serialize
string json = ManifestSerializer.SerializeStarSchema(result);

// Deserialize
StarSchemaResult? result = ManifestSerializer.DeserializeStarSchema(json);

// Write to file
await ManifestSerializer.WriteStarSchemaToFileAsync(result, "star.json");
```
