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

  "JoinGraphs": [
    {
      "Label": null,
      "Reason": null,
      "Edges": [
        // CandidateIndex references Candidates[0] → orders.customer_id = customers.id
        { "LeftTable": "orders", "RightTable": "customers", "CandidateIndex": 0, "Confidence": 0.78 },
        // CandidateIndex references Candidates[1] → orders.product_id = products.id
        { "LeftTable": "orders", "RightTable": "products",  "CandidateIndex": 1, "Confidence": 0.65 }
      ],
      "ExcludedTables": null,
      "RecommendedQuery": "-- Cross-manifest join query\n-- orders.customer_id → customers.id (confidence: 78%, ManyToOne)\nSELECT ...",
      "QueryAnnotations": [
        { "ColumnName": "customer_id", "Annotation": "Join key: orders.customer_id → customers.id (78%)" }
      ],
      "EstimatedRowCount": null
    }
  ],

  "EquivalentTableGroups": null,

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
  ]
}
```

### Field Reference

| Field | Type | Description |
|-------|------|-------------|
| `Tables` | `string[]` | Names of all tables that were analyzed. |
| `Candidates` | `JoinCandidate[]` | All discovered join candidates, sorted by confidence descending. |
| `JoinGraphs` | `JoinGraph[]` | Join graphs. The first entry is the primary (recommended) graph. Additional entries represent alternate graphs created by substituting equivalent table partitions. |
| `EquivalentTableGroups` | `EquivalentTableGroup[]?` | Groups of tables with near-identical schemas that connect to the same hub tables (e.g., train/test splits). Null if none detected. |
| `TransitiveChains` | `JoinChain[]?` | Paths through 3+ tables in the primary join graph. Null if none found. |
| `Insights` | `DatasetInsight[]?` | Cross-manifest insights (join quality, schema drift, normalization hints, equivalent table partitions). Null if none. |
| `PerTableInsights` | `Dictionary<string, DatasetInsight[]>?` | Per-table column insights from single-manifest analysis (nullity, skew, encoding, outliers). Null if none. |

#### DatasetInsight Fields

Each insight separates observation (what the data shows) from risk (why it matters) from recommendation (what to do), with structured patch actions and calibrated confidence.

| Field | Type | Description |
|-------|------|-------------|
| `Kind` | `InsightKind` | Machine-readable identifier. See [Insight Kinds](#insight-kinds) below. |
| `Category` | `InsightCategory` | Domain category: `DataQuality`, `Distribution`, `Encoding`, `Redundancy`, `Dimensionality`, `Scale`, `ImageQuality`, `TemporalQuality`, or `JoinQuality`. |
| `Severity` | `InsightSeverity` | `Info` (no action required), `Warning` (may degrade model quality), or `Critical` (likely training failures). |
| `Confidence` | `double` | Calibrated confidence in [0, 1] based on evidence strength. |
| `Scope` | `InsightScope` | Granularity: `Feature`, `FeaturePair`, `FeatureGroup`, `Dataset`, or `CrossManifest`. |
| `Observation` | `string` | Factual statement about the data — only manifest-proven facts. |
| `Risk` | `string` | Why this matters for downstream ML or analytics. |
| `Recommendation` | `string` | What to do about it. |
| `Rationale` | `string?` | Deeper explanation or justification. |
| `Alternatives` | `string[]?` | Alternative approaches the consumer could take instead. |
| `AffectedFeatures` | `string[]` | Column names affected by this insight. |
| `Actions` | `InsightAction[]` | Executable transformation actions under the current apply mode. |
| `ProposedActions` | `InsightAction[]?` | Actions requiring explicit user opt-in or mode escalation. |
| `ConflictGroup` | `string?` | Mutually exclusive group — only the highest-confidence insight in a group is applied. |
| `RecommendedApplyMode` | `ApplyMode` | `AutoSafe` (lossless, apply automatically), `Suggest` (lossy but recommended), `ManualOnly` (requires review), or `Blocked` (needs domain validation). |
| `Evidence` | `Dictionary<string, Dictionary<string, JsonElement>>?` | Structured evidence keyed by feature name, then statistic name → value. |

#### InsightAction Fields

| Field | Type | Description |
|-------|------|-------------|
| `Kind` | `ActionKind` | `Drop` (remove column), `Replace` (rewrite expression), `Append` (add derived column), or `Filter` (add WHERE predicate). |
| `Column` | `string?` | Target column name. Null for `Filter` or `Append` actions that create new columns. |
| `Expression` | `string?` | SQL expression for the transformation. Null for `Drop` actions. |
| `Alias` | `string?` | Output column name for `Append` actions. |
| `Lossy` | `bool` | Whether this action discards information that cannot be recovered. |
| `Reversible` | `bool` | Whether the original data can be reconstructed from the output. |
| `BundleIdentifier` | `string?` | Groups related actions that must be applied atomically (all or none). |

#### Insight Kinds

##### Single-Table

| Kind | Category | Description |
|------|----------|-------------|
| `HighMissingness` | DataQuality | Column has > 30% null values. |
| `CriticalMissingness` | DataQuality | Column has > 80% null values — likely unusable without external data. |
| `CorrelatedMissingness` | DataQuality | Multiple columns share missingness patterns, suggesting structural gaps. |
| `InformativeMissingness` | DataQuality | The null/non-null pattern itself carries signal. |
| `ConstantFeature` | DataQuality | Column has zero variance (all non-null values identical). |
| `ZeroInflated` | Distribution | More than half the values are zero. |
| `RightSkewed` | Distribution | Right-skewed distribution (skewness above threshold). |
| `LeftSkewed` | Distribution | Left-skewed distribution (skewness below negative threshold). |
| `HeavyTailed` | Distribution | Heavy-tailed distribution (excess kurtosis above threshold). |
| `ExtremeOutliers` | Distribution | Outlier ratio exceeds threshold (Z-score based). |
| `PossibleOrdinal` | Encoding | Integer-valued with few distinct values — may be ordinal. |
| `LowCardinalityCategorical` | Encoding | Low-cardinality string column suitable for one-hot encoding. |
| `HighCardinalityCategorical` | Encoding | High-cardinality string column where one-hot would explode dimensionality. |
| `BinaryFeature` | Encoding | Exactly two distinct non-null values. |
| `NearDuplicateNumeric` | Redundancy | Near-perfect linear correlation between two numeric columns. |
| `NearDuplicateCategorical` | Redundancy | Near-perfect association (Cramér's V) between two categorical columns. |
| `FunctionalDependency` | Redundancy | One column is a deterministic function of another. |
| `PossibleIdentifier` | Dimensionality | Near-unique values — likely an identifier, not a feature. |
| `NormalizationNeeded` | Scale | Numeric range vastly exceeds its mean — normalization recommended. |
| `TinyImages` | ImageQuality | Images below minimum resolution threshold. |
| `HugeImages` | ImageQuality | Images above maximum resolution threshold. |
| `UndecodableImages` | ImageQuality | Image files that failed to decode. |

##### Syndromes (compound)

| Kind | Description |
|------|-------------|
| `ZeroInflatedSkewedNumeric` | Zero-inflated + skewed + heavy-tailed. Treatment: indicator + conditional log-transform + clip. |
| `NonNormalDistribution` | Skewed + heavy-tailed + outliers without zero-inflation. Treatment: log-transform + clip. |
| `SystematicDataGap` | Correlated missingness across columns. Treatment: group indicator. |
| `FeatureLeakageRisk` | Near-duplicate or functionally dependent columns. Treatment: keep one. |
| `UnusableFeature` | Constant + critically missing. Treatment: drop. |

##### Cross-Manifest

| Kind | Description |
|------|-------------|
| `ManyToManyJoin` | Join candidate classified as many-to-many — Cartesian product risk. |
| `HighNullKey` | Join key has high null ratio — inner joins will silently drop rows. |
| `CardinalityMismatch` | Large cardinality mismatch between join keys. |
| `DisjointRange` | Key columns have disjoint value ranges — join produces zero rows. |
| `SchemaDrift` | Same column name, different types across tables. |
| `DenormalizationHint` | Tables share overlapping columns with high value overlap. |
| `StarSchema` | Central table has many one-to-many relationships — star schema detected. |
| `EquivalentTablePartition` | Tables with identical schemas share hub connections — likely train/test split. |

#### JoinGraph Fields

| Field | Type | Description |
|-------|------|-------------|
| `Label` | `string?` | Short label identifying this graph variant. Null for the primary graph when no equivalent tables exist. |
| `Reason` | `string?` | Human-readable explanation of why this graph exists or which table substitution it represents. |
| `Edges` | `JoinGraphEdge[]` | The edges in this join graph (candidates above the graph edge threshold, default 0.5). Each edge identifies the joined tables and carries a `CandidateIndex` referencing the full `JoinCandidate` in `Candidates[]` — use it to retrieve join columns, evidence signals, join type, and quality warnings. |
| `ExcludedTables` | `string[]?` | Table names excluded from this graph (the non-preferred equivalents). Null when none. |
| `RecommendedQuery` | `string?` | Ready-to-run JOIN SQL for this graph. Null if no candidates exceed the confidence threshold. |
| `QueryAnnotations` | `QueryAnnotation[]?` | Annotations mapping joined columns to their originating candidates. |
| `EstimatedRowCount` | `long?` | Estimated row count for the bridge/fact table in this graph. Useful for inferring train/test split sizes. |

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

## Equivalent Table Detection

When the analyzer discovers tables with near-identical schemas that both connect to the same hub tables via the same join columns — but only have many-to-many, non-key edges between each other — it groups them as **equivalent table partitions**. This is the canonical signature of train/test splits, temporal partitions, or regional shards.

Detection criteria:

1. **Schema overlap ≥ 75%** — at least 75% of the smaller table's column names appear in the larger table.
2. **All inter-table edges are ManyToMany** with `UniqueKeyScore < 0.1` — no genuine foreign key relationship between the two.
3. **Shared hub connections** — both tables join to at least one common third table on the same column.

When equivalent tables are detected:

- The **primary join graph** (`JoinGraphs[0]`) uses the preferred table (the one with the strongest hub confidence) and excludes the others.
- **Alternate join graphs** are generated for each non-preferred table, swapping it in and excluding the preferred one.
- Each graph carries its own `RecommendedQuery`, `Label`, `Reason`, and `EstimatedRowCount`.
- An `EquivalentTablePartition` insight is emitted with row counts, enabling consumers to detect train/test ratios (e.g., 32M prior vs 1.4M train → ratio 22.9×).

```jsonc
{
  "JoinGraphs": [
    {
      "Label": "Primary (order_products__prior)",
      "Reason": "Preferred: strongest hub connections (max confidence 0.90)",
      "Edges": [ /* edges using order_products__prior */ ],
      "ExcludedTables": ["order_products__train"],
      "RecommendedQuery": "SELECT ... FROM order_products__prior ...",
      "EstimatedRowCount": 32434489
    },
    {
      "Label": "Alternate (order_products__train)",
      "Reason": "Substitutes order_products__train for order_products__prior",
      "Edges": [ /* edges using order_products__train */ ],
      "ExcludedTables": ["order_products__prior"],
      "RecommendedQuery": "SELECT ... FROM order_products__train ...",
      "EstimatedRowCount": 1384617
    }
  ],
  "EquivalentTableGroups": [
    {
      "Tables": ["order_products__prior", "order_products__train"],
      "SharedColumns": ["order_id", "product_id", "add_to_cart_order", "reordered"],
      "SchemaOverlap": 1.0,
      "PreferredTable": "order_products__prior",
      "Reason": "Schema overlap 100% (4/4 columns). All inter-table edges are many-to-many ...",
      "RowCounts": { "order_products__prior": 32434489, "order_products__train": 1384617 }
    }
  ]
}
```

## Consumption Patterns

### 1. Use the SQL directly

The primary graph's `RecommendedQuery` is a ready-to-run JOIN query. If you just need to combine the datasets, execute it. The SQL includes inline comments noting confidence and join type for each ON clause, and uses LEFT JOIN where null-key ratios are high enough to risk dropped rows.

```csharp
string sql = result.JoinGraphs[0].RecommendedQuery!;
```

When equivalent tables exist, iterate `JoinGraphs` to get a query for each partition.

### 2. Filter candidates by confidence and classification

`Candidates` is sorted by confidence descending. The consuming project should:

- **Accept** candidates with `Confidence ≥ 0.6` and `EstimatedJoinType` of `OneToOne`, `OneToMany`, or `ManyToOne` — these are safe joins.
- **Flag for review** anything with `ManyToMany` classification or `QualityWarnings` present — these risk cartesian explosion or data loss.
- **Reject** candidates below your minimum threshold (the engine already filters at 0.3, but consuming projects often want 0.5+).

The `Evidence` sub-object is fully transparent, so consumers can apply their own domain-specific logic. For example, if you know column names are unreliable across your datasets, you can re-weight by ignoring `NameSimilarity` and relying on `TopKJaccard` + `CardinalityRatio`.

### 3. Use the join graph for multi-table orchestration

This is the most powerful pattern. Each `JoinGraph` in `JoinGraphs` gives you edges, and `TransitiveChains` gives you paths through the primary graph. A consuming project can:

- **Build a query plan** — walk `TransitiveChains` to determine join order. `MinConfidence` on each chain tells you which path is safest.
- **Detect hub tables** — a table appearing in many edges is likely a fact table or shared dimension. `StarSchema` insights call this out explicitly.
- **Resolve ambiguity** — when two tables have multiple candidate joins (different column pairs), `CandidateIndex` on each edge tells you exactly which candidate the graph selected.
- **Handle train/test splits** — when `JoinGraphs.Count > 1`, iterate alternate graphs and use `EstimatedRowCount` to identify partition sizes.

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
| `EquivalentTablePartition` | Tables with identical schemas share hub connections — likely train/test split or temporal partition. Check `EquivalentTableGroups` for details and row counts. |

## C# Integration

### Minimal example

```csharp
CrossManifestResult result = ManifestSerializer.DeserializeCrossManifest(json)!;

// Grab the best candidate
JoinCandidate best = result.Candidates[0];
if (best.Confidence >= 0.6 && best.EstimatedJoinType != JoinClassification.ManyToMany)
{
    // Safe to auto-join — use the primary graph's query
    string joinSql = result.JoinGraphs[0].RecommendedQuery!;
}

// Or walk the graph
foreach (JoinChain chain in result.TransitiveChains ?? [])
{
    if (chain.MinConfidence >= 0.5)
    {
        // Build a multi-table pipeline from chain.Tables
    }
}

// Handle equivalent table partitions (train/test splits)
if (result.JoinGraphs.Count > 1)
{
    foreach (JoinGraph graph in result.JoinGraphs)
    {
        Console.WriteLine($"{graph.Label}: {graph.EstimatedRowCount} rows");
        Console.WriteLine(graph.RecommendedQuery);
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
