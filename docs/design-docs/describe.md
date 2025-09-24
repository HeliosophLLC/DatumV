# DESCRIBE Query Modifier — Design Plan

> **Status**: Proposed (complexity **S–M**). No prerequisites — all infrastructure exists.

---

## Motivation

DatumIngest already generates rich feature manifests via the CLI `manifest` command and the `ANALYZE` statement. But both operate on raw tables — there is no way to get statistics on *filtered*, *joined*, or *transformed* data without writing the result to a temp table first and then analyzing it.

This matters because ML dataset work is overwhelmingly about subsets:

```sql
-- "What does the training split look like after my feature transforms?"
-- Today: three commands
CREATE TEMP TABLE train AS SELECT ... FROM data WHERE hash_split(id, 42) < 0.8;
ANALYZE train;
-- then manually read the .datum-manifest sidecar

-- Proposed: one statement
DESCRIBE SELECT ... FROM data WHERE hash_split(id, 42) < 0.8
```

DuckDB has `DESCRIBE` and `SUMMARIZE`, but only on tables — not on arbitrary query results. BigQuery has `INFORMATION_SCHEMA.COLUMN_FIELD_PATHS` for schema inspection but no statistical profiling. pandas has `df.describe()` which works on any filtered/transformed DataFrame. DESCRIBE closes the gap with pandas while going further than any SQL engine by producing the same `QueryResultsManifest` (with per-kind statistics, interactions, and insights) that already powers DatumIngest's dataset intelligence.

---

## Syntax

### Basic form

```sql
DESCRIBE SELECT * FROM data WHERE score > 0.5
```

Returns a tabular summary: one row per output column with kind-appropriate statistics (count, null ratio, distinct count, min, max, mean, stddev, top values, etc.).

### Full manifest form

```sql
DESCRIBE JSON SELECT * FROM data WHERE score > 0.5
```

Returns the complete `QueryResultsManifest` as JSON — the same format produced by the CLI `manifest` command and stored in `.datum-manifest` sidecar files.

### Analyze variant (interactions + insights)

```sql
DESCRIBE ANALYZE SELECT * FROM data WHERE score > 0.5
```

Additionally computes column interactions (Pearson, Spearman, Cramér's V, mutual information, Theil's U, missingness correlation) and runs `InsightAnalyzer` to produce dataset-level insights and a recommended query. This is heavier — O(columns²) pairwise computation — and is gated behind the explicit `ANALYZE` keyword.

The two modifiers compose:

```sql
DESCRIBE ANALYZE JSON SELECT * FROM data WHERE score > 0.5
```

### Works on any query expression

DESCRIBE wraps any valid query expression, including CTEs, joins, subqueries, and set operations:

```sql
-- CTE
DESCRIBE WITH filtered AS (SELECT * FROM data WHERE valid = 1)
         SELECT * FROM filtered

-- Join
DESCRIBE SELECT a.*, b.category
         FROM features a JOIN labels b ON a.id = b.id

-- Set operation
DESCRIBE SELECT * FROM train UNION ALL SELECT * FROM augmented
```

### Shell and server meta-command

```
.describe SELECT * FROM data WHERE score > 0.5
.describe analyze SELECT * FROM data WHERE score > 0.5
.describe json SELECT * FROM data WHERE score > 0.5
.describe analyze json SELECT * FROM data WHERE score > 0.5
```

### CLI subcommand

```bash
datumingest describe "SELECT * FROM data WHERE score > 0.5" --source /path/to/data
datumingest describe "SELECT * FROM data" --source /path/to/data --analyze --json
datumingest describe "SELECT * FROM data" --source /path/to/data --output manifest.json
```

---

## Tabular Output Format

The default (non-JSON) output is a human-readable summary table. The columns vary by `DataKind` of each feature, but the table presents the union of all applicable statistics with empty cells for inapplicable ones:

```
┌─────────────┬──────────┬───────┬───────┬──────────┬─────────┬─────────┬─────────┬──────────┬──────────────┐
│ Column      │ Kind     │ Count │ Nulls │ Null %   │ Distinct│ Min     │ Max     │ Mean     │ StdDev       │
├─────────────┼──────────┼───────┼───────┼──────────┼─────────┼─────────┼─────────┼──────────┼──────────────┤
│ user_id     │ Int32    │ 10000 │     0 │ 0.0%     │    9847 │       1 │   10000 │  5001.23 │      2887.45 │
│ age         │ Float32  │  9500 │   500 │ 5.0%     │      78 │      18 │      95 │    34.67 │        12.31 │
│ category    │ String   │ 10000 │     0 │ 0.0%     │       5 │         │         │          │              │
│ created_at  │ DateTime │ 10000 │     0 │ 0.0%     │    8923 │ 2024-01 │ 2026-04 │          │              │
│ embedding   │ Vector   │ 10000 │     0 │ 0.0%     │   10000 │         │         │          │              │
└─────────────┴──────────┴───────┴───────┴──────────┴─────────┴─────────┴─────────┴──────────┴──────────────┘
```

Below the main table, kind-specific detail sections appear for columns that have richer statistics:

```
── Numeric Detail ──
┌─────────┬──────────┬──────────┬───────┬─────────┬──────────┬──────────┬─────────────┐
│ Column  │ Skewness │ Kurtosis │ Zeros │ Zero %  │ Outliers │ Outlier% │ IntegerVal? │
├─────────┼──────────┼──────────┼───────┼─────────┼──────────┼──────────┼─────────────┤
│ user_id │     0.00 │    -1.20 │     0 │   0.0%  │        0 │    0.0%  │ yes         │
│ age     │     0.87 │     0.32 │     0 │   0.0%  │       23 │    0.2%  │ yes         │
└─────────┴──────────┴──────────┴───────┴─────────┴──────────┴──────────┴─────────────┘

── String Detail ──
┌──────────┬────────────┬────────────┬────────────────┬────────────────────────────────────┐
│ Column   │ Min Length │ Max Length │ Char Class     │ Top Values                         │
├──────────┼────────────┼────────────┼────────────────┼────────────────────────────────────┤
│ category │          3 │         12 │ Alpha          │ electronics(3201), clothing(2845)…  │
└──────────┴────────────┴────────────┴────────────────┴────────────────────────────────────┘

── Temporal Detail ──
┌────────────┬─────────────────────┬─────────────────────┐
│ Column     │ Earliest            │ Latest              │
├────────────┼─────────────────────┼─────────────────────┤
│ created_at │ 2024-01-03T08:12:00 │ 2026-04-14T22:45:00 │
└────────────┴─────────────────────┴─────────────────────┘

── Vector Detail ──
┌───────────┬─────────┬─────────┬──────────────┬───────────┬──────────┬──────────┐
│ Column    │ Min Len │ Max Len │ Element Mean │ Norm Min  │ Norm Max │ Zero Vec │
├───────────┼─────────┼─────────┼──────────────┼───────────┼──────────┼──────────┤
│ embedding │     128 │     128 │       0.0012 │     0.891 │    1.124 │        0 │
└───────────┴─────────┴─────────┴──────────────┴───────────┴──────────┴──────────┘

10000 rows scanned (4.2s)
```

When `DESCRIBE ANALYZE` is used, an additional **Interactions** section and **Insights** section appear below the detail tables.

---

## Infrastructure Reuse

DESCRIBE reuses the full statistics → manifest → insights pipeline that already exists. No new accumulator types, no new manifest types, no new insight rules. The only new code is plumbing and formatting.

### Existing components consumed directly

| Component | Location | Role in DESCRIBE |
|---|---|---|
| `StatisticsCollector` | `src/DatumIngest/Statistics/StatisticsCollector.cs` | Accumulates per-column statistics from the query result stream. 15 kind-aware accumulators per column (count, cardinality/HLL, numeric moments, histogram, quantiles, entropy, top-K, string lengths, missing runs, vector stats, temporal range, etc.). |
| `ColumnInteractionCollector` | `src/DatumIngest/Statistics/ColumnInteractionCollector.cs` | Pairwise column interactions (Pearson, Spearman, Cramér's V, ANOVA F, mutual information, Theil's U, missingness correlation). Used only in `DESCRIBE ANALYZE`. |
| `ManifestBuilder.Build()` | `src/DatumIngest/Manifest/ManifestBuilder.cs` | Converts raw `ColumnStatistics` + `DataKind` map + interactions → `QueryResultsManifest` with polymorphic `FeatureManifest` per column. Internally calls `ColumnRoleClassifier`, `InsightAnalyzer`, and `QuerySynthesizer` when insight thresholds are provided. |
| `InsightAnalyzer.Analyze()` | `src/DatumIngest/Manifest/Insights/InsightAnalyzer.cs` | 16 insight rules detecting data quality issues (constant columns, high missingness, skewness, outliers, zero inflation, rare categories, etc.). Used only in `DESCRIBE ANALYZE`. |
| `QuerySynthesizer` | `src/DatumIngest/Manifest/Insights/QuerySynthesizer.cs` | Generates recommended and full suggested queries from insights. Used only in `DESCRIBE ANALYZE`. |
| `ManifestSerializer` | `src/DatumIngest/Manifest/ManifestSerializer.cs` | JSON serialization with polymorphic type discriminators. Used in `DESCRIBE JSON` and `DESCRIBE ANALYZE JSON`. |
| `FeatureManifest` hierarchy | `src/DatumIngest/Manifest/FeatureManifest.cs` | `NumericFeatureManifest`, `StringFeatureManifest`, `VectorFeatureManifest`, `TensorFeatureManifest`, `ImageFeatureManifest`, `BinaryFeatureManifest`, `TemporalFeatureManifest`, `BooleanFeatureManifest` — all produced by `ManifestBuilder` and consumed by the tabular formatter. |

### Execution flow

DESCRIBE follows the same pattern as `RunManifestAsync` in `Program.cs`:

```
User: DESCRIBE [ANALYZE] [JSON] <query>
  │
  ├─ Parse <query> as QueryExpression (standard SqlParser.Parse)
  ├─ Plan query via QueryPlanner.PlanWithSubqueriesAsync
  ├─ Create StatisticsCollector
  ├─ Create ColumnInteractionCollector (only if ANALYZE)
  │
  ├─ Execute plan, streaming rows through collectors:
  │    for each RowBatch in plan.ExecuteAsync(context):
  │      for each Row:
  │        collector.AddRow(row)
  │        interactionCollector?.AddRow(row)    // only if ANALYZE
  │        rowCount++
  │
  ├─ Build manifest:
  │    stats = collector.GetStatistics()
  │    interactions = interactionCollector?.GetInteractions()
  │    manifest = ManifestBuilder.Build(stats, columnKinds, rowCount,
  │                 interactions,
  │                 analyze ? InsightThresholds.Default : null)
  │
  └─ Output:
       if JSON:
         ManifestSerializer.Serialize(SourceManifest.Create("result", manifest))
       else:
         DescribeFormatter.Format(manifest)  // new: tabular summary
```

The only genuinely new code is `DescribeFormatter` — the tabular renderer that transforms `QueryResultsManifest` into the human-readable tables shown above.

---

## Implementation Plan

### Phase 1: Core execution path (S)

**New types:**

1. **`DescribeFormatter`** — `src/DatumIngest/Manifest/DescribeFormatter.cs`
   - Static method: `Format(QueryResultsManifest manifest) → string`
   - Reads `manifest.Features` and dispatches on `FeatureManifest` subtype to produce the main summary table plus kind-specific detail tables.
   - Uses existing `TableFormatter` column-alignment logic or plain string formatting (no Spectre.Console dependency in the core library).

**CLI surface:**

2. **`RunDescribeAsync`** — new method in `src/DatumIngest.Cli/Program.cs`
   - Pattern: identical to `RunManifestAsync` but with tabular output by default.
   - CLI dispatch: `"describe" => await RunDescribeAsync(query, catalog, analyze, json, outputPath)`
   - Flags: `--analyze` (enable interactions + insights), `--json` (JSON output instead of tabular), `--output` (write to file).

**Shell surface:**

3. **`.describe` meta-command** — `src/DatumIngest.Server/CommandDispatcher.cs`
   - Pattern: identical to `.explain` handler.
   - Parses optional `analyze` and `json` prefixes, then the SQL query.
   - Returns `CommandResult` with the formatted output.

4. **`InteractiveShell` prefix interception** — `src/DatumIngest.Cli/Shell/InteractiveShell.cs`
   - If input starts with `DESCRIBE ` (case-insensitive), convert to `.describe` meta-command.
   - Same pattern as the existing `EXPLAIN` → `.explain` conversion.

### Phase 2: gRPC surface (S)

5. **`Describe` RPC** — `src/DatumIngest.Compute/Services/ComputeService.cs`
   - New RPC: `Describe(DescribeRequest) → DescribeResponse`
   - `DescribeRequest`: `session_id`, `sql`, `analyze` (bool), `json` (bool)
   - `DescribeResponse`: either `manifest_json` (string) or `summary_text` (string)
   - Proto definition in `src/DatumIngest.Compute/Protos/compute.proto`

### Phase 3: Language server integration (S)

6. **Completions** — the language server should offer `DESCRIBE` as a statement-starting keyword.
7. **Diagnostics** — `DESCRIBE` followed by a non-query expression should produce a parse error.

---

## Interaction with Other Clauses

DESCRIBE wraps the entire query expression. The inner query is planned and executed normally — DESCRIBE only changes what happens to the output rows (they are fed to collectors instead of being streamed to the client).

| Inner clause | Behavior |
|---|---|
| `WHERE` | Statistics reflect only the filtered rows. This is the primary value proposition. |
| `GROUP BY` | Statistics describe the aggregated result, not the pre-aggregation rows. |
| `LET` | LET bindings that are aliased (visible in output) are profiled as columns. Hidden LET bindings are not profiled. |
| `HAVING` | Post-aggregation filter; statistics reflect the surviving groups. |
| `QUALIFY` | Post-window filter; statistics reflect the surviving rows. |
| `ORDER BY` | Honored during execution (may affect `FORWARD_FILL` in `IMPUTE`) but does not affect statistics. |
| `LIMIT` | Statistics are computed on the limited result set. `DESCRIBE SELECT * FROM data LIMIT 1000` profiles a 1000-row sample — intentionally useful for quick exploration. |
| `INTO` | **Mutually exclusive.** `DESCRIBE ... INTO` is a parse error: DESCRIBE consumes the output for profiling, INTO consumes it for writing. |
| `PIVOT` / `UNPIVOT` | Statistics describe the pivoted/unpivoted result. |
| `IMPUTE` | Statistics describe the imputed (filled) data. |
| `ASSERT` | Rows that fail ASSERT with `ON FAIL SKIP` are excluded from statistics. |

---

## Manifest Parity

The following table shows exactly which `FeatureManifest` fields appear in the tabular output versus the JSON output:

### Base fields (all column kinds)

| Field | Tabular | JSON | Source |
|---|---|---|---|
| `Name` | ✓ (Column) | ✓ | Column name from schema |
| `Kind` | ✓ | ✓ | `DataKind` from first non-null value |
| `Count` | ✓ | ✓ | `CountAccumulator.NonNull` |
| `NullCount` | ✓ (Nulls) | ✓ | `CountAccumulator.NullOrEmpty` |
| `NullRatio` | ✓ (Null %) | ✓ | Derived: `NullCount / (Count + NullCount)` |
| `EstimatedDistinctCount` | ✓ (Distinct) | ✓ | `CardinalityAccumulator` (HyperLogLog) |
| `TopKValues` | In detail | ✓ | `TopKAccumulator` (StreamSummary, K=10) |
| `DominantValueRatio` | In detail | ✓ | Derived: `TopKValues[0].Frequency / Count` |
| `MissingRuns` | — | ✓ | `MissingRunsAccumulator` |
| `Entropy` | In detail | ✓ | `EntropyAccumulator` (Shannon bits) |
| `Role` | — | ✓ | `ColumnRoleClassifier` |

### NumericFeatureManifest fields

| Field | Tabular | JSON | Source |
|---|---|---|---|
| `Min` | ✓ | ✓ | `NumericAccumulator` |
| `Max` | ✓ | ✓ | `NumericAccumulator` |
| `Mean` | ✓ | ✓ | `NumericAccumulator` (Welford) |
| `StandardDeviation` | ✓ (StdDev) | ✓ | `NumericAccumulator` (Welford) |
| `Variance` | — | ✓ | `NumericAccumulator` |
| `Skewness` | In detail | ✓ | `NumericAccumulator` (3rd moment) |
| `Kurtosis` | In detail | ✓ | `NumericAccumulator` (4th moment) |
| `Histogram` | — | ✓ | `HistogramAccumulator` (reservoir, 100K) |
| `Quantiles` | — | ✓ | `QuantileAccumulator` (reservoir, 10K) |
| `ZeroCount` | In detail | ✓ | `NumericAccumulator` |
| `ZeroRatio` | In detail | ✓ | Derived |
| `OutlierCount` | In detail | ✓ | `NumericAccumulator` (Z > 3) |
| `OutlierRatio` | In detail | ✓ | Derived |
| `IntegerValued` | In detail | ✓ | `HistogramAccumulator` |
| `NonzeroMean` | — | ✓ | `NumericAccumulator` (when ZeroRatio > 0.1) |

### StringFeatureManifest fields

| Field | Tabular | JSON | Source |
|---|---|---|---|
| `MinLength` | In detail | ✓ | `StringLengthAccumulator` |
| `MaxLength` | In detail | ✓ | `StringLengthAccumulator` |
| `CharacterClass` | In detail | ✓ | Derived from sampled values |

### VectorFeatureManifest fields

| Field | Tabular | JSON | Source |
|---|---|---|---|
| `MinLength` | In detail | ✓ | `VectorStatsAccumulator` |
| `MaxLength` | In detail | ✓ | `VectorStatsAccumulator` |
| `ElementStats` | In detail | ✓ | `VectorStatsAccumulator` (Welford) |
| `ZeroElementCount/Ratio` | — | ✓ | `VectorStatsAccumulator` |
| `ZeroVectorCount` | In detail | ✓ | `VectorStatsAccumulator` |
| `NormMin/Max/Mean` | In detail | ✓ | `VectorStatsAccumulator` (L2) |

### TemporalFeatureManifest fields

| Field | Tabular | JSON | Source |
|---|---|---|---|
| `Earliest` | In detail | ✓ | `TemporalRangeAccumulator` |
| `Latest` | In detail | ✓ | `TemporalRangeAccumulator` |

### ImageFeatureManifest fields

| Field | Tabular | JSON | Source |
|---|---|---|---|
| `MinWidth/MaxWidth` | In detail | ✓ | `ImageStatsAccumulator` |
| `MinHeight/MaxHeight` | In detail | ✓ | `ImageStatsAccumulator` |
| `ChannelCounts` | — | ✓ | `ImageStatsAccumulator` |
| `UndecodableCount` | In detail | ✓ | `ImageStatsAccumulator` |
| `TinyImageCount/HugeImageCount` | — | ✓ | `ImageStatsAccumulator` |
| `FileSizeStats` | — | ✓ | `ImageStatsAccumulator` |

### BooleanFeatureManifest fields

| Field | Tabular | JSON | Source |
|---|---|---|---|
| `TrueRatio` | In detail | ✓ | Derived from `TopKAccumulator` |

### BinaryFeatureManifest fields

| Field | Tabular | JSON | Source |
|---|---|---|---|
| `SizeStats` | — | ✓ | `BinarySizeAccumulator` |

---

## DESCRIBE ANALYZE: Interaction and Insight Output

When the `ANALYZE` modifier is present, two additional sections appear in both tabular and JSON output.

### Interactions (tabular)

```
── Column Interactions (top 10 by |correlation|) ──
┌──────────┬──────────┬─────────┬──────────┬──────────┬────────────┐
│ Column A │ Column B │ Pearson │ Spearman │ Cramér V │ Mutual Info│
├──────────┼──────────┼─────────┼──────────┼──────────┼────────────┤
│ age      │ income   │   0.723 │    0.698 │          │      0.412 │
│ category │ status   │         │          │    0.891 │      0.534 │
│ age      │ category │         │          │          │      0.087 │
└──────────┴──────────┴─────────┴──────────┴──────────┴────────────┘
```

Only interactions above a significance threshold are shown (default: |correlation| > 0.1 or mutual information > 0.05). All interactions appear in JSON regardless of threshold.

### Insights (tabular)

```
── Insights ──
⚠ [Warning] High Missingness — income: 23.4% null (threshold: 5.0%)
  Risk: Missing values may bias downstream aggregations and ML model training.
  Recommendation: Impute with MEDIAN or investigate data collection gaps.

ℹ [Info] Skewed Distribution — age: skewness = 1.87
  Risk: Right-skewed features may degrade linear model performance.
  Recommendation: Apply log transform: LET log_age = ln(age + 1)

── Recommended Query ──
SELECT
  user_id,
  COALESCE(income, 45230.0) AS income,   -- median imputation
  ln(age + 1) AS log_age                  -- log transform for skewness
FROM data
WHERE hash_split(id, 42) < 0.8
```

In JSON mode, the full `DatasetInsight` objects, `RecommendedQuery`, `FullSuggestedQuery`, and `QueryAnnotations` are included in the manifest — identical to what the CLI `manifest` command produces today.

---

## Comparison with Existing Commands

| Capability | `stats` CLI | `manifest` CLI | `ANALYZE` stmt | **`DESCRIBE`** |
|---|---|---|---|---|
| Works on arbitrary queries | ✓ | ✓ | ✗ (tables only) | **✓** |
| Tabular output | ✓ (raw) | ✗ | ✗ | **✓ (formatted)** |
| JSON manifest output | ✗ | ✓ | sidecar file | **✓ (optional)** |
| Column interactions | ✗ | ✓ | sidecar file | **✓ (with ANALYZE)** |
| Insights | ✗ | ✗ | ✗ | **✓ (with ANALYZE)** |
| Shell access | ✗ | ✗ | ✓ | **✓** |
| gRPC access | partial | ✓ | ✗ | **✓** |
| Works on filtered subsets | ✓ | ✓ | ✗ | **✓** |
| Formatted for humans | raw K/V | ✗ | ✗ | **✓** |

DESCRIBE subsumes the common use cases of `stats` and `manifest` while adding the crucial filtered-subset and human-readable dimensions. The existing commands remain useful for scripting and backward compatibility.

---

## Complexity Assessment

| Component | Effort | Notes |
|---|---|---|
| `DescribeFormatter` | S | New code. Template-driven rendering from `FeatureManifest` subtypes. Largest piece. |
| `RunDescribeAsync` (CLI) | S | Copy of `RunManifestAsync` with conditional JSON/tabular output. |
| `.describe` meta-command | S | Copy of `.explain` handler with statistics collection instead of explain tree. |
| `InteractiveShell` interception | Trivial | One `StartsWith("DESCRIBE ")` check. |
| `Describe` gRPC RPC | S | New message types + handler following existing `GetStats` pattern. |
| Language server completions | Trivial | Add `DESCRIBE` to keyword list. |
| Tests | S | Parsing tests, end-to-end execution tests, formatter tests. |

**Total: S–M** — approximately 1–3 days of implementation. No new accumulators, no new manifest types, no new insight rules, no parser grammar changes beyond recognizing the `DESCRIBE` prefix.
