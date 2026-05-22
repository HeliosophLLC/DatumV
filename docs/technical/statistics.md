# Statistics & Manifest

DatumV collects per-column statistics in a single pass and generates structured JSON manifests describing dataset features for ML pipeline integration.

## Statistics

The `stats` command collects per-column statistics:

| Statistic | Source | Applies To |
|-----------|--------|------------|
| Non-null count | CountAccumulator | All columns |
| Null/empty count | CountAccumulator | All columns |
| Distinct count estimate | CardinalityAccumulator | All columns (HyperLogLog, ±2%) |
| Top-K values | TopKAccumulator | All columns (default K=10) |
| Min, Max, Mean, Variance, StdDev | NumericAccumulator | Float32, UInt8 |
| Zero count, Zero ratio | NumericAccumulator | Float32, UInt8 |
| Outlier count, Outlier ratio | NumericAccumulator | Float32, UInt8 (Z-score > 3) |
| Histogram | HistogramAccumulator | Float32, UInt8 (reservoir sampling, 50 bins) |
| Percentiles (P1–P99) | QuantileAccumulator | Float32, UInt8 (reservoir sampling, linear interpolation) |
| Min/Max string length | StringLengthAccumulator | String, JsonValue |
| Element count range, element-wise min/max/mean/var/std | VectorStatsAccumulator | Vector, Matrix, Tensor |
| Rank range (dimensionality) | VectorStatsAccumulator | Vector, Matrix, Tensor |
| Zero element count, Zero element ratio, Zero vector count | VectorStatsAccumulator | Vector, Matrix, Tensor |
| Width/Height range, channel distribution | ImageStatsAccumulator | Image (header-only parsing) |
| Orientation distribution (landscape/portrait/square) | ImageStatsAccumulator | Image |
| Megapixel distribution (min/max/mean/var/std) | ImageStatsAccumulator | Image |
| Aspect ratio min/max/mean/std | ImageStatsAccumulator | Image |
| File size min/max/mean/var/std | ImageStatsAccumulator | Image |
| Extreme dimension counts (tiny/huge) | ImageStatsAccumulator | Image |
| Byte-length min/max/mean/var/std | BinarySizeAccumulator | UInt8Array |
| Earliest/Latest date | TemporalRangeAccumulator | Date, DateTime |
| Shannon entropy | EntropyAccumulator | Float32, UInt8, String, JsonValue, Date, DateTime |
| Top-K coverage ratio | CategoricalDiagnosticsAccumulator | Float32, UInt8, String, JsonValue, Date, DateTime |
| Rare category ratio | CategoricalDiagnosticsAccumulator | Float32, UInt8, String, JsonValue, Date, DateTime |

Accumulators support `Merge()` for parallel collection using Chan et al. algorithm for combining Welford's running statistics.

### Rare category threshold

Categories with fewer than 5 observations are classified as rare. This threshold is a fixed heuristic — low enough to avoid flagging moderately infrequent values, high enough to catch singletons and near-singletons that often indicate data entry errors or extreme long-tail categories. The threshold is defined as the `RareThreshold` constant on `CategoricalDiagnosticsAccumulator`.

### Image header parsing

The `ImageStatsAccumulator` extracts dimensions and channel count from image headers without full decoding — no external image library required:

| Format | Detection |
|--------|-----------|
| JPEG | SOF0/SOF2 marker → width, height, components |
| PNG | IHDR chunk → width, height, color type → channels |
| WebP | VP8/VP8L/VP8X → width, height, alpha flag → channels |

### Extreme dimension detection

Images with extreme dimensions are flagged for automatic dataset warnings:

| Flag | Condition | Use case |
|------|-----------|----------|
| Tiny | width < 32 or height < 32 | Thumbnails, corrupted images, accidental micro-crops |
| Huge | width > 4096 or height > 4096 | DSLR originals, unresized captures, memory-heavy outliers |

Thresholds are defined as the `TinyThreshold` (32) and `HugeThreshold` (4096) constants on `ImageStatsAccumulator`. Only successfully decoded images are evaluated — undecodable images (unknown dimensions) are excluded from both counts.

### Column interactions

The `manifest` command also computes pairwise interaction statistics between columns. Numeric (Float32, UInt8) and categorical (String, JsonValue, Date, DateTime) columns receive value-based measures appropriate to their pair type. Image, binary, and multidimensional columns participate in missingness correlation only.

| Measure | Pair Type | Algorithm |
|---------|-----------|----------|
| Pearson r | Numeric × Numeric | Online co-moment (West 1979), O(1) memory |
| Spearman ρ | Numeric × Numeric | Reservoir sampling (10K pairs) → rank transform → Pearson on ranks |
| Cramér's V | Categorical × Categorical | Bounded contingency table (1K categories), χ² → V |
| ANOVA F | Categorical × Numeric | Per-group Welford (1K groups), F = MS_between / MS_within |
| Mutual Information | Numeric & Categorical | Reservoir sampling (10K pairs), numeric bins (20), MI in bits |
| Theil's U | Numeric & Categorical | Asymmetric uncertainty coefficient U(A\|B) = MI / H(A), derived from MI reservoir |
| Missingness Correlation | All | Pearson r between null masks (1=null, 0=present), O(1) memory |

Missingness correlation detects structurally correlated missing data — for example, when `income` is null, `credit_score` is often null too. This is useful for identifying data leakage, pipeline bugs, or columns that share an upstream data source.

### Controlling interactions

Interaction computation scales O(C²) with the number of columns due to pairwise analysis. For wide schemas (50+ columns), this can consume significant memory — primarily from Spearman and Mutual Information reservoirs (~156 KB per numeric pair).

**CLI:** The `manifest` command always computes interactions. The `index-manifest` command makes interactions opt-in via `--with-interactions`, since index generation is typically used on raw source files where column count is unknown ahead of time.

```bash
# Interactions included (opt-in)
datumv index-manifest --source csv:data=data.csv --with-interactions

# Interactions excluded (default)
datumv index-manifest --source csv:data=data.csv
```

**Programmatic API:** Interaction collection is controlled by composition — instantiate `ColumnInteractionCollector` and pass the results to `ManifestBuilder.Build()`, or omit it entirely. No configuration flags are needed.

```csharp
StatisticsCollector collector = new();

// With interactions:
ColumnInteractionCollector interactionCollector = new();
// ... feed rows to both collectors ...
QueryResultsManifest manifest = ManifestBuilder.Build(
    collector.GetStatistics(), columnKinds, rowCount,
    interactionCollector.GetInteractions());

// Without interactions:
QueryResultsManifest manifest = ManifestBuilder.Build(
    collector.GetStatistics(), columnKinds, rowCount);
```

## Index + Manifest Co-Generation

The `index-manifest` command generates both a `.datum-index` and a `.datum-manifest` file in a single pass over the source data. This avoids reading the data twice when both artifacts are needed.

```bash
# Generate both files (interactions excluded by default)
datumv index-manifest --source csv:data=measurements.csv

# With pairwise interaction analysis
datumv index-manifest --source csv:data=measurements.csv --with-interactions

# Override manifest output path
datumv index-manifest --source csv:data=measurements.csv --output custom-manifest.json

# All index flags are supported
datumv index-manifest --source csv:data=measurements.csv --chunk-size 5000 --bloom-columns category --index-columns id
```

No SQL query is required — the command scans raw source tables directly (equivalent to `SELECT *`). Output files default to `<source-file>.datum-index` and `<source-file>.datum-manifest`.

## Manifest

The `manifest` command generates a structured JSON manifest describing every column in a query result with type-specific statistics.

### Usage

```bash
# Print to stdout
datumv manifest "SELECT * FROM data" --source csv:data=measurements.csv

# Write to file
datumv manifest "SELECT * FROM data" --source csv:data=measurements.csv --output manifest.json
```

### Feature types

Each column produces a polymorphic `FeatureManifest` subclass based on its `DataKind`:

| DataKind | Manifest Type | Extra Fields |
|----------|--------------|---------------|
| Float32, UInt8 | `NumericFeatureManifest` | min, max, mean, variance, stdDev, histogram, quantiles, zeroCount, zeroRatio, outlierCount, outlierRatio, integerValued |
| String, JsonValue | `StringFeatureManifest` | minLength, maxLength |
| Vector | `VectorFeatureManifest` | minLength, maxLength, elementStats, zeroElementCount, zeroElementRatio, zeroVectorCount |
| Matrix, Tensor | `TensorFeatureManifest` | minRank, maxRank, minElementCount, maxElementCount, elementStats, zeroElementCount, zeroElementRatio, zeroVectorCount |
| Image | `ImageFeatureManifest` | width/height ranges, channelCounts, orientationCounts, undecodableCount, tinyImageCount, hugeImageCount, fileSizeStats, megapixelStats, aspectRatioStats |
| UInt8Array | `BinaryFeatureManifest` | sizeStats (byte-length distribution) |
| Date, DateTime | `TemporalFeatureManifest` | earliest, latest (ISO 8601) |

All feature types share: `name`, `kind`, `count`, `nullCount`, `validCount`, `estimatedDistinctCount`, `isConstant`, `isNearConstant`, `topKValues`, `dominantValueRatio`, `nullRatio`, `missingRuns`, `entropy`, `entropyApproximate`.

| Derived Flag | Definition | Purpose |
|--------------|------------|--------|
| `isConstant` | `estimatedDistinctCount <= 1` | Constant columns carry no information and break many model types. |
| `isNearConstant` | `dominantValueRatio > 0.98` | A single value dominates more than 98 % of rows — the column is likely a useless feature. |

### Integer detection

`NumericFeatureManifest` includes an `integerValued` boolean that is `true` when every observed value has no fractional part. This fact is deterministic (not heuristic) and is the foundation for distinguishing continuous numeric columns from discrete/ordinal ones. The detection is performed during histogram binning via reservoir sampling.



### Example output

```json
{
  "rowCount": 5000,
  "generatedAtUtc": "2026-03-15T12:00:00Z",
  "features": [
    {
      "type": "numeric",
      "name": "image_id",
      "kind": "Float32",
      "count": 5000,
      "nullCount": 0,
      "validCount": 5000,
      "estimatedDistinctCount": 4998,
      "min": 1.0,
      "max": 581929.0,
      "mean": 291485.3,
      "variance": 28341558.2,
      "standardDeviation": 5323.7,
      "histogram": { "binEdges": [...], "counts": [...] },
      "quantiles": { "p01": 1.0, "p05": 29097.5, "p25": 145371.8, "p50": 291485.3, "p75": 436598.8, "p95": 553881.1, "p99": 581929.0 },
      "integerValued": true,
      "entropy": 11.2,
      "entropyApproximate": false,
      "isConstant": false,
      "isNearConstant": false,
      "dominantValueRatio": 0.0002,
      "topKValues": [],
    },
    {
      "type": "image",
      "name": "file_bytes",
      "kind": "Image",
      "count": 5000,
      "nullCount": 0,
      "validCount": 5000,
      "estimatedDistinctCount": 5000,
      "minWidth": 128,
      "maxWidth": 4096,
      "minHeight": 96,
      "maxHeight": 3072,
      "channelCounts": { "3": 4950, "4": 50 },
      "orientationCounts": { "landscape": 3100, "portrait": 1700, "square": 200 },
      "undecodableCount": 0,
      "tinyImageCount": 0,
      "hugeImageCount": 0,
      "fileSizeStats": { "count": 5000, "min": 5234, "max": 2456789, "mean": 178234.5, ... },
      "megapixelStats": { "count": 5000, "min": 0.012, "max": 12.58, "mean": 2.15, "variance": 3.42, "standardDeviation": 1.85 },
      "aspectRatioStats": { "count": 5000, "min": 0.5, "max": 3.1, "mean": 1.28, "variance": 0.048, "standardDeviation": 0.22 },
      "topKValues": []
    }
  ],
  "interactions": [
    {
      "columnA": "image_id",
      "columnB": "width",
      "pearson": 0.02,
      "spearman": 0.03,
      "mutualInformation": 0.15,
      "theilUAB": 0.01,
      "theilUBA": 0.02,
      "missingnessCorrelation": null
    }
  ]
}
```

## Schema Matching

When multiple manifests are available, the schema matching infrastructure discovers join candidates and star-schema hubs across tables using only manifest statistics — no data access required.

### Pipeline

1. **Column matching** — Pairwise column discovery via Levenshtein name similarity (with suffix bonuses for `_id`, `_key`, `_code`) and type compatibility scoring. Role-based gates filter candidates based on column role classification.
2. **Evidence scoring** — Each candidate column pair receives six evidence signals:

| Signal | Source | Purpose |
|--------|--------|---------|
| Name similarity | Levenshtein distance | How closely column names match |
| Type compatibility | DataKind comparison | 1.0 exact, 0.8 coercible (Float32↔UInt8, Date↔DateTime), 0.5 String↔JsonValue |
| TopK Jaccard | TopK value sets | Value-domain overlap (case-insensitive, skips continuous numerics) |
| Cardinality ratio | min(NDV) / max(NDV) | Whether columns draw from the same domain |
| Range overlap | Numeric [min, max] intersection / union | Physical range compatibility |
| Unique key score | NDV / RowCount ≥ 0.95 | Whether at least one side is a primary key |

3. **Composite confidence** — Weighted combination of all signals (default weights: name 0.30, type 0.15, TopK 0.20, cardinality 0.15, range 0.10, unique key 0.10). When range overlap is null (non-numeric), its weight redistributes proportionally.
4. **Join classification** — Automatic 1:1, 1:N, N:1, N:M classification from NDV/RowCount ratios.
5. **Star schema detection** — Groups OneToMany/ManyToOne candidates by (hub table, key column). Tables with ≥2 spoke relationships are identified as hub tables.

### Thresholds

All thresholds are configurable via `SchemaMatchingThresholds`:

| Threshold | Default | Purpose |
|-----------|---------|---------|
| NameSimilarityMinThreshold | 0.4 | Column name matching sensitivity |
| CandidateMinConfidence | 0.3 | Minimum confidence to include a candidate |

## Planning Integration

Manifests are not just for external consumption — they feed back into the query planner for data-driven cardinality estimation.

### Sidecar Discovery

After tables are registered in the `TableCatalog`, call `catalog.DiscoverSidecars()` to auto-discover `.datum-manifest`, `.datum-index`, and `.datum-schema` sidecar files alongside each source file. Tables that already have a registered artifact are skipped, and each sidecar file is read at most once per unique source path. The CLI, gRPC compute backend, and `.source` interactive command all call this method after source registration. See [Programmatic API — Sidecar Auto-Discovery](api.md#sidecar-auto-discovery) for details.

### Temp Table Auto-Manifest

When a session-owned temp table is populated via `CREATE TEMP TABLE AS SELECT` or `INSERT INTO`, a column statistics manifest is automatically generated using `StatisticsCollector` (all 15 accumulators) and `ManifestBuilder.Build()`. The manifest is written as a `.datum-manifest` sidecar and registered on the catalog, giving the query planner immediate access to accurate cardinality estimates, null ratios, and histograms.

For tables mutated after initial population (`UPDATE`, `DELETE`, `ALTER TABLE ADD COLUMN`), the manifest becomes stale. Use `ANALYZE` to rebuild:

```sql
ANALYZE features   -- rebuilds manifest and source index
```

This follows the PostgreSQL convention. See [SQL Reference — ANALYZE](../sql/ddl-dml.md#analyze) for details.

### Cost Model

When planning a query, the `QueryPlanner` checks for a registered manifest:

1. **Row count override** — The manifest's `RowCount` replaces the provider's estimate. This is especially valuable for CSV, JSON, JSONL, and ZIP providers that cannot report row counts from metadata alone.
2. **Column statistics attachment** — Per-column `FeatureManifest` entries are attached to the `ScanOperator`, making `estimatedDistinctCount` (NDV) and `nullRatio` available to the cost model.

The `QueryExplainer` then uses these statistics for selectivity estimation instead of fixed heuristics:

- **Equality** (`column = value`): selectivity = 1/NDV
- **Not-equal** (`column != value`): selectivity = 1 − 1/NDV
- **IS NULL**: selectivity = actual null ratio from the manifest
- **IS NOT NULL**: selectivity = 1 − null ratio
- **IN**: selectivity = count × 1/NDV
- **Equi-join** (`a.x = b.x`): rows = left × right / max(NDV_left, NDV_right)

When no manifest is available, the cost model falls back to the same fixed heuristics used before (10% for equality, 33% for range predicates, etc.).
