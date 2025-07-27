# Cross-Manifest Join Analysis

When multiple manifests are available, `CrossManifestAnalyzer` discovers join candidates across tables using a multi-signal evidence pipeline. Everything is statistics-only — no data leaves the source. The consuming project gets a richly annotated recommendation and decides what to execute.

## Output JSON Shape

The result is serialized via `ManifestSerializer.SerializeCrossManifest(result)` and deserialized via `ManifestSerializer.DeserializeCrossManifest(json)`. Both use AOT-compatible source-generated JSON.

```jsonc
{
  "Tables": ["orders", "customers", "products"],

  "Candidates": [
    {
      "LeftTable": "orders",
      "RightTable": "customers",
      "LeftColumns": ["customer_id"],
      "RightColumns": ["id"],
      "Evidence": {
        "NameSimilarity": 0.72,
        "TypeCompatibility": 1.0,
        "TopKJaccard": 0.60,
        "CardinalityRatio": 0.85,
        "RangeOverlap": null,
        "NullKeyRatio": 0.02,
        "UniqueKeyScore": 1.0,
        "CompositeConfidence": 0.78
      },
      "Confidence": 0.78,
      "EstimatedJoinType": "ManyToOne",
      "EstimatedFanout": 1.0,
      "QualityWarnings": null
    }
  ],

  "JoinGraph": [
    { "LeftTable": "orders", "RightTable": "customers", "CandidateIndex": 0, "Confidence": 0.78 },
    { "LeftTable": "orders", "RightTable": "products",  "CandidateIndex": 1, "Confidence": 0.65 }
  ],

  "TransitiveChains": [
    { "Tables": ["customers", "orders", "products"], "Edges": [0, 1], "MinConfidence": 0.65 }
  ],

  "Insights": [
    {
      "Kind": "StarSchema",
      "Category": "JoinQuality",
      "Scope": "CrossManifest",
      "Severity": "Info",
      "Message": "Star schema detected: 'orders' ..."
    }
  ],

  "RecommendedQuery": "-- Cross-manifest join query\n-- orders.customer_id → customers.id (confidence: 78%, ManyToOne)\nSELECT ...",

  "QueryAnnotations": [
    { "ColumnName": "customer_id", "Annotation": "Join key: orders.customer_id → customers.id (78%)" }
  ]
}
```

### Field Reference

| Field | Type | Description |
|-------|------|-------------|
| `Tables` | `string[]` | Names of all tables that were analyzed. |
| `Candidates` | `JoinCandidate[]` | All discovered join candidates, sorted by confidence descending. |
| `JoinGraph` | `JoinGraphEdge[]` | Candidates above the graph edge threshold (default 0.5), forming a join graph. |
| `TransitiveChains` | `JoinChain[]?` | Paths through 3+ tables in the join graph. Null if none found. |
| `Insights` | `DatasetInsight[]?` | Cross-manifest insights (join quality, schema drift, normalization hints). Null if none. |
| `RecommendedQuery` | `string?` | A ready-to-run JOIN SQL query. Null if no candidates exceed the confidence threshold. |
| `QueryAnnotations` | `QueryAnnotation[]?` | Annotations mapping joined columns to their originating candidates. |

### Evidence Signals

Each `JoinCandidate` carries a `JoinEvidence` object with six independent signals and a weighted composite:

| Signal | Range | Description |
|--------|-------|-------------|
| `NameSimilarity` | 0–1 | Levenshtein-based similarity with suffix bonuses for `_id`, `_key`, `_code`. |
| `TypeCompatibility` | 0–1 | 1.0 exact match, 0.8 coercible (Scalar↔UInt8, Date↔DateTime), 0.5 String↔JsonValue, 0.0 incompatible. |
| `TopKJaccard` | 0–1 | Jaccard similarity of TopK value sets (case-insensitive, skips continuous numerics). |
| `CardinalityRatio` | 0–1 | min(NDV) / max(NDV). Close to 1.0 suggests both columns draw from the same domain. |
| `RangeOverlap` | 0–1 or null | Intersection / union of numeric [min, max] ranges. Null for non-numeric columns. |
| `NullKeyRatio` | 0–1 | Maximum null ratio across both columns. High values indicate risky join keys. |
| `UniqueKeyScore` | 0 or 1 | 1.0 if NDV ≈ RowCount on at least one side, 0.0 otherwise. |
| `CompositeConfidence` | 0–1 | Weighted combination. Default weights: name 0.30, type 0.15, TopK 0.20, cardinality 0.15, range 0.10, unique key 0.10. When `RangeOverlap` is null, its weight redistributes proportionally. |

### Join Classification

`EstimatedJoinType` is one of:

| Value | Meaning |
|-------|---------|
| `OneToOne` | Both sides have near-unique keys — one row matches at most one row. |
| `OneToMany` | Left side has unique keys; right side may have duplicates. |
| `ManyToOne` | Right side has unique keys; left side may have duplicates. |
| `ManyToMany` | Neither side has unique keys — risk of cartesian explosion. |

## Consumption Patterns

### 1. Use the SQL directly

`RecommendedQuery` is a ready-to-run JOIN query. If you just need to combine the datasets, execute it. The SQL includes inline comments noting confidence and join type for each ON clause, and uses LEFT JOIN where null-key ratios are high enough to risk dropped rows.

### 2. Filter candidates by confidence and classification

`Candidates` is sorted by confidence descending. The consuming project should:

- **Accept** candidates with `Confidence ≥ 0.6` and `EstimatedJoinType` of `OneToOne`, `OneToMany`, or `ManyToOne` — these are safe joins.
- **Flag for review** anything with `ManyToMany` classification or `QualityWarnings` present — these risk cartesian explosion or data loss.
- **Reject** candidates below your minimum threshold (the engine already filters at 0.3, but consuming projects often want 0.5+).

The `Evidence` sub-object is fully transparent, so consumers can apply their own domain-specific logic. For example, if you know column names are unreliable across your datasets, you can re-weight by ignoring `NameSimilarity` and relying on `TopKJaccard` + `CardinalityRatio`.

### 3. Use the join graph for multi-table orchestration

This is the most powerful pattern. `JoinGraph` gives you edges and `TransitiveChains` gives you paths. A consuming project can:

- **Build a query plan** — walk `TransitiveChains` to determine join order. `MinConfidence` on each chain tells you which path is safest.
- **Detect hub tables** — a table appearing in many edges is likely a fact table or shared dimension. `StarSchema` insights call this out explicitly.
- **Resolve ambiguity** — when two tables have multiple candidate joins (different column pairs), `CandidateIndex` on each edge tells you exactly which candidate the graph selected.

### 4. React to insights

`Insights` are actionable warnings about your data topology:

| Insight | What to do |
|---------|-----------|
| `ManyToManyJoin` | Add a GROUP BY or dedup before joining. |
| `HighNullKey` | Filter nulls from the join key or use LEFT JOIN. |
| `CardinalityMismatch` | One table is a tiny lookup — consider broadcasting it. |
| `DisjointRange` (Critical) | The columns share a name but have no overlapping values — the join will produce zero rows. |
| `SchemaDrift` | Same column name, different types across tables — needs a CAST. |
| `DenormalizationHint` | Tables are near-duplicates — consider merging instead of joining. |
| `StarSchema` | Classic dimensional layout detected — use the fact table as the FROM anchor. |

## C# Integration

### Minimal example

```csharp
CrossManifestResult result = ManifestSerializer.DeserializeCrossManifest(json)!;

// Grab the best candidate
JoinCandidate best = result.Candidates[0];
if (best.Confidence >= 0.6 && best.EstimatedJoinType != JoinClassification.ManyToMany)
{
    // Safe to auto-join
    string joinSql = result.RecommendedQuery!;
}

// Or walk the graph
foreach (JoinChain chain in result.TransitiveChains ?? [])
{
    if (chain.MinConfidence >= 0.5)
    {
        // Build a multi-table pipeline from chain.Tables
    }
}
```

### Programmatic analysis

```csharp
List<ManifestWithName> manifests =
[
    new("orders", ordersManifest),
    new("customers", customersManifest),
];

// Default thresholds
CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests);

// Custom thresholds
CrossManifestThresholds thresholds = new()
{
    CandidateMinConfidence = 0.4,
    GraphEdgeMinConfidence = 0.6,
    ChainMaxDepth = 3,
};

CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests, thresholds);
```

### TableCatalog integration

When a `TableCatalog` holds multiple tables with manifests, cross-manifest analysis is available automatically:

```csharp
if (catalog.HasJoinSuggestions)
{
    CrossManifestResult result = catalog.GetOrComputeCrossManifest();
}
```

Results are cached — subsequent calls return the same instance until the catalog changes.

## CLI

```bash
# Analyze two or more manifest files
datum-ingest cross-manifest --manifest orders.datum-manifest --manifest customers.datum-manifest

# Write result to file
datum-ingest cross-manifest --manifest a.json --manifest b.json --output result.json
```

## REPL

```
.join-suggestions
```

Displays join candidates, transitive chains, insights, and recommended SQL for all tables in the current session.

## gRPC

| RPC | Request | Response | Description |
|-----|---------|----------|-------------|
| `GetJoinSuggestions` | `GetJoinSuggestionsRequest` | `GetJoinSuggestionsResponse` | Returns `CrossManifestResult` as JSON. |
| `GetStats` | `GetStatsRequest` (session ID) | `GetStatsResponse` | Returns a unified manifest JSON combining all tables. |

The `AddSource` response includes a `has_join_suggestions` flag indicating whether enough tables are registered for cross-manifest analysis.
