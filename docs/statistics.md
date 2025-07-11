# Statistics & Manifest

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Providers](providers.md) · [Architecture](architecture.md) · [Programmatic API](api.md)

DatumQuery collects per-column statistics in a single pass and generates structured JSON manifests describing dataset features for ML pipeline integration.

## Statistics

The `stats` command collects per-column statistics:

| Statistic | Source | Applies To |
|-----------|--------|------------|
| Non-null count | CountAccumulator | All columns |
| Null/empty count | CountAccumulator | All columns |
| Distinct count estimate | CardinalityAccumulator | All columns (HyperLogLog, ±2%) |
| Top-K values | TopKAccumulator | All columns (default K=10) |
| Min, Max, Mean, Variance, StdDev | NumericAccumulator | Scalar, UInt8 |
| Zero count, Zero ratio | NumericAccumulator | Scalar, UInt8 |
| Outlier count, Outlier ratio | NumericAccumulator | Scalar, UInt8 (Z-score > 3) |
| Histogram | HistogramAccumulator | Scalar, UInt8 (reservoir sampling, 50 bins) |
| Percentiles (P1–P99) | QuantileAccumulator | Scalar, UInt8 (reservoir sampling, linear interpolation) |
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
| Shannon entropy | EntropyAccumulator | Scalar, UInt8, String, JsonValue, Date, DateTime |
| Top-K coverage ratio | CategoricalDiagnosticsAccumulator | Scalar, UInt8, String, JsonValue, Date, DateTime |
| Rare category ratio | CategoricalDiagnosticsAccumulator | Scalar, UInt8, String, JsonValue, Date, DateTime |

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

The `manifest` command also computes pairwise interaction statistics between columns. Numeric (Scalar, UInt8) and categorical (String, JsonValue, Date, DateTime) columns receive value-based measures appropriate to their pair type. Image, binary, and multidimensional columns participate in missingness correlation only.

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

## Manifest

The `manifest` command generates a structured JSON manifest describing every column in a query result with type-specific statistics.

### Usage

```bash
# Print to stdout
dq manifest "SELECT * FROM data" --source csv:data=measurements.csv

# Write to file
dq manifest "SELECT * FROM data" --source csv:data=measurements.csv --output manifest.json
```

### Feature types

Each column produces a polymorphic `FeatureManifest` subclass based on its `DataKind`:

| DataKind | Manifest Type | Extra Fields |
|----------|--------------|---------------|
| Scalar, UInt8 | `NumericFeatureManifest` | min, max, mean, variance, stdDev, histogram, quantiles, zeroCount, zeroRatio, outlierCount, outlierRatio |
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

### Example output

```json
{
  "rowCount": 5000,
  "generatedAtUtc": "2026-03-15T12:00:00Z",
  "features": [
    {
      "type": "numeric",
      "name": "image_id",
      "kind": "Scalar",
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
      "entropy": 11.2,
      "entropyApproximate": false,
      "isConstant": false,
      "isNearConstant": false,
      "dominantValueRatio": 0.0002,
      "topKValues": []
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
