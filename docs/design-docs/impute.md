# IMPUTE Clause — Implementation Plan

## Overview

`IMPUTE` fills null values in post-`WHERE`, pre-aggregate rows. It decouples null-filling from the `SELECT` column list, so downstream aggregations (and `LET` bindings) operate on a clean, filled dataset rather than requiring every expression to carry `COALESCE` wrappers.

Five strategies cover the common ML ETL cases:

| Strategy | Description | Execution model |
|---|---|---|
| `CONSTANT value` | Replace nulls with a literal value | Pure streaming |
| `MEDIAN` | Replace nulls with the column's median | Two-phase (pre-scan + streaming) |
| `MODE` | Replace nulls with the column's most-frequent value | Two-phase (pre-scan + streaming) |
| `FORWARD_FILL` | Propagate the last non-null value within a partition | Stateful ordered streaming |
| `INTERPOLATE` | Linearly interpolate across null runs within a partition | Stateful ordered streaming with buffering |

The key design tension is that `MEDIAN` and `MODE` require a full pre-scan to compute fill values — you cannot compute the median in a single streaming pass over the same data you are filling. The solution is a **two-phase plan**: the planner detects these strategies, builds a second independent source tree from the same `FROM`/`WHERE`, drains it once via `ImputeStatisticsPass`, stores the computed fill values, then passes them into the main streaming `ImputeOperator`.

`FORWARD_FILL` and `INTERPOLATE` require ordered rows within a partition. If the `ORDER BY` sub-clause within the `IMPUTE` binding specifies an ordering that differs from the query's own `ORDER BY`, the planner injects a sort on the source before the impute layer.

---

## Syntax

`IMPUTE` clauses appear between `WHERE` and `GROUP BY`. Multiple `IMPUTE` clauses, one per column-binding, are written in sequence (the same way multiple `ASSERT` clauses are written after `QUALIFY`).

```sql
-- Single column, constant fill
SELECT user_id, age, salary
FROM users
WHERE cohort = 'Q1'
IMPUTE age WITH CONSTANT 0
IMPUTE salary WITH CONSTANT 0.0

-- Median and mode (trigger pre-scan)
SELECT user_id, age, category
FROM profiles
IMPUTE age WITH MEDIAN
IMPUTE category WITH MODE

-- Forward-fill within partitions, ordered by time
SELECT symbol, ts, close
FROM trades
IMPUTE close WITH FORWARD_FILL PARTITION BY symbol ORDER BY ts

-- Linear interpolation within partitions
SELECT symbol, ts, price
FROM ticks
IMPUTE price WITH INTERPOLATE PARTITION BY symbol ORDER BY ts ASC

-- Mixed strategies in a single query
SELECT user_id, age, income, status, event_time, value
FROM events
WHERE event_date = '2024-06-01'
IMPUTE age WITH MEDIAN
IMPUTE income WITH CONSTANT 0.0
IMPUTE status WITH MODE
IMPUTE value WITH INTERPOLATE PARTITION BY user_id ORDER BY event_time

-- Combined with GROUP BY (imputing feeds the aggregation)
SELECT category, AVG(price) AS average_price
FROM products
IMPUTE price WITH MEDIAN
GROUP BY category
```

### Grammar (informal EBNF)

```
impute_binding  ::= IMPUTE column_name WITH strategy [ORDER BY order_item [, order_item ...]] [PARTITION BY column_name [, column_name ...]]
                  | IMPUTE column_name WITH CONSTANT expression

strategy        ::= MEDIAN | MODE | FORWARD_FILL | INTERPOLATE
```

`PARTITION BY` and `ORDER BY` are optional sub-clauses on the `IMPUTE` binding itself — they are distinct from the query-level `ORDER BY`. `CONSTANT`, `MEDIAN`, `MODE`, `FORWARD_FILL`, and `INTERPOLATE` are all **contextual identifiers** (they are not reserved keywords), following the same pattern used for `SKIP`/`WARN`/`ABORT` in `ASSERT`.

---

## Strategy Details

### CONSTANT

The simplest strategy. The fill value is a literal expression evaluated at plan time (not per-row). The value must be type-compatible with the column. The language server should emit a diagnostic if the constant's type is incompatible with the column's declared type.

```sql
IMPUTE age WITH CONSTANT 0
IMPUTE label WITH CONSTANT 'unknown'
IMPUTE score WITH CONSTANT 0.0
```

Implementation: during `ExecuteAsync`, for each row in each batch, if `row[columnOrdinal]` is a null `DataValue`, replace it with the pre-evaluated constant `DataValue`.

### MEDIAN

Compute the column's median (P50) across the post-`WHERE` row set, then replace all nulls with that value.

`MEDIAN` is only well-defined for numeric columns. The language server should warn when `MEDIAN` is applied to a non-numeric column.

Implementation uses `QuantileAccumulator` from `src/DatumIngest/Statistics/Accumulators/QuantileAccumulator.cs`. After draining the pre-scan source, call `GetResult()` and extract the `P50` value. `QuantileAccumulator` uses reservoir sampling (max 100,000 samples) and linear interpolation, so it is approximate for very large datasets — acceptable for imputation.

### MODE

Compute the column's most-frequent non-null value across the post-`WHERE` row set, then replace all nulls with that value.

Implementation uses `CategoricalDiagnosticsAccumulator` (or `TopKAccumulator`) from `src/DatumIngest/Statistics/Accumulators/`. After the pre-scan, read the top entry. If the column is numeric (e.g., an integer enum), `MODE` is still valid — the mode is simply the most-frequent number.

A diagnostic warning is appropriate if `MODE` is applied to a continuous floating-point column (mode is rarely meaningful for floats).

### FORWARD_FILL

Propagate the last non-null value encountered in row order within a partition. Rows must be ordered by the `ORDER BY` sub-clause. The first row in a partition that is null has no fill value yet and remains null (no preceding non-null to propagate from).

```sql
IMPUTE close WITH FORWARD_FILL PARTITION BY symbol ORDER BY ts
```

Implementation: `ImputeOperator` maintains a `Dictionary<PartitionKey, DataValue?>` of the last-seen non-null value per partition. For each row:
1. Compute the partition key from the `PARTITION BY` columns (or treat the whole batch as one partition if none given).
2. If `row[columnOrdinal]` is non-null, update `_lastNonNull[key]` and emit the row unchanged.
3. If `row[columnOrdinal]` is null and `_lastNonNull[key]` has a value, replace the null with that value.
4. If `row[columnOrdinal]` is null and no prior non-null exists for that key, emit the row unchanged (null remains null).

The source must be ordered by the `PARTITION BY` columns (to group them) and then by the `ORDER BY` sub-clause within each partition before reaching `ImputeOperator`. If the query has no enclosing `ORDER BY` that satisfies this, the planner injects a `SortOperator` on the source.

### INTERPOLATE

Linearly interpolate across null runs within a partition. Requires an integer or timestamp ordering column so that "distance" between rows is meaningful.

```sql
IMPUTE price WITH INTERPOLATE PARTITION BY symbol ORDER BY ts
```

Implementation requires buffering a null run until a trailing non-null value is found:
1. When a non-null value is encountered, close any open null run by filling backward with linear interpolation between the preceding non-null and the current value.
2. Emit buffered rows.
3. At the end of a partition or the end of the result set, any open null run at the tail has no trailing value — fall back to `FORWARD_FILL` behavior (propagate the last seen non-null, or leave null if no prior value exists).

The interpolation factor for row `i` in a run from value `v_start` at index `i_start` to value `v_end` at index `i_end` is: `v_start + (v_end - v_start) * (i - i_start) / (i_end - i_start)`.

`INTERPOLATE` is only valid for numeric columns. The language server should emit an error for non-numeric columns.

---

## Phase 1: AST Changes

**File:** `src/DatumIngest.Parsing/Ast/AstNodes.cs`

### 1a. Add `ImputeStrategy` enum

Add this enum above (or near) the `AssertFailureMode` enum:

```csharp
/// <summary>The null-fill strategy for an <see cref="ImputeBinding"/>.</summary>
public enum ImputeStrategy
{
    /// <summary>Replace nulls with a fixed literal value.</summary>
    Constant,

    /// <summary>Replace nulls with the column's median value, computed via a pre-scan.</summary>
    Median,

    /// <summary>Replace nulls with the column's most-frequent value, computed via a pre-scan.</summary>
    Mode,

    /// <summary>Propagate the last non-null value forward within each ordered partition.</summary>
    ForwardFill,

    /// <summary>Linearly interpolate across null runs within each ordered partition.</summary>
    Interpolate,
}
```

### 1b. Add `ImputeBinding` record

Add this record near the `AssertClause` record:

```csharp
/// <summary>
/// A single column imputation binding: fills nulls in <paramref name="ColumnName"/>
/// using the specified <paramref name="Strategy"/>.
/// </summary>
/// <param name="ColumnName">The column whose nulls are filled.</param>
/// <param name="Strategy">The imputation strategy.</param>
/// <param name="ConstantValue">
/// The fill expression for <see cref="ImputeStrategy.Constant"/>. Null for all other strategies.
/// </param>
/// <param name="PartitionBy">
/// Optional list of column names that define partitions for
/// <see cref="ImputeStrategy.ForwardFill"/> and <see cref="ImputeStrategy.Interpolate"/>.
/// </param>
/// <param name="OrderBy">
/// The ordering within each partition for
/// <see cref="ImputeStrategy.ForwardFill"/> and <see cref="ImputeStrategy.Interpolate"/>.
/// </param>
/// <param name="Span">Source span for diagnostic reporting.</param>
public sealed record ImputeBinding(
    string ColumnName,
    ImputeStrategy Strategy,
    Expression? ConstantValue = null,
    IReadOnlyList<string>? PartitionBy = null,
    IReadOnlyList<OrderByItem>? OrderBy = null,
    SourceSpan? Span = null);
```

### 1c. Add `Impute` field to `SelectStatement`

The `Impute` field is placed between `Where` and `GroupBy` to reflect its execution position.

The current record (lines 16–33 of `AstNodes.cs`):
```csharp
public sealed record SelectStatement(
    IReadOnlyList<SelectColumn> Columns,
    FromClause? From = null,
    IntoClause? Into = null,
    IReadOnlyList<JoinClause>? Joins = null,
    Expression? Where = null,
    GroupByClause? GroupBy = null,
    ...
```

**Change**: add `IReadOnlyList<ImputeBinding>? Impute = null` after `Where`:

```csharp
public sealed record SelectStatement(
    IReadOnlyList<SelectColumn> Columns,
    FromClause? From = null,
    IntoClause? Into = null,
    IReadOnlyList<JoinClause>? Joins = null,
    Expression? Where = null,
    IReadOnlyList<ImputeBinding>? Impute = null,   // ← new
    GroupByClause? GroupBy = null,
    Expression? Having = null,
    Expression? Qualify = null,
    IReadOnlyList<AssertClause>? Assertions = null,
    PivotClause? Pivot = null,
    UnpivotClause? Unpivot = null,
    OrderByClause? OrderBy = null,
    int? Limit = null,
    int? Offset = null,
    bool Distinct = false,
    IReadOnlyList<CommonTableExpression>? CommonTableExpressions = null,
    IReadOnlyList<LetBinding>? LetBindings = null);
```

Because the new parameter has a `null` default and all existing call sites already use named arguments (verified from `SelectStatementParser` and `ParseWithRecovery` in `SqlParser.cs`), the only required change to each call site is adding `Impute: imputeBindings` (or omitting it to keep null). The existing positional args in `ParseWithRecovery` will need to be updated to use named arguments for all parameters after `Where`.

---

## Phase 2: Token Changes

**File:** `src/DatumIngest.Parsing/Tokens/SqlToken.cs`

Add one reserved keyword token for the `IMPUTE` keyword itself. The strategy names (`CONSTANT`, `MEDIAN`, `MODE`, `FORWARD_FILL`, `INTERPOLATE`) are parsed as contextual identifiers — no tokens needed for them.

```csharp
/// <summary>The <c>IMPUTE</c> keyword for null-value imputation.</summary>
Impute,
```

Add this enum member near `Assert` and `Define` (all three are domain-specific clause keywords).

In the tokenizer (in the same file, the `Span.EqualToIgnoreCase` match table), add:
```csharp
.Match(Span.EqualToIgnoreCase("impute"), SqlToken.Impute)
```

---

## Phase 3: Parser Changes

**File:** `src/DatumIngest.Parsing/SqlParser.cs`

### 3a. `ImputeStrategyParser`

A contextual-identifier parser that maps the strategy keyword to the `ImputeStrategy` enum:

```csharp
/// <summary>
/// Parses an imputation strategy name as a contextual identifier.
/// None of these words are reserved keywords; they are matched by exact
/// case-insensitive text comparison.
/// </summary>
private static readonly TokenListParser<SqlToken, ImputeStrategy> ImputeStrategyParser =
    Token.EqualTo(SqlToken.Identifier)
        .Where(token =>
        {
            string text = token.ToStringValue();
            return text.Equals("CONSTANT",     StringComparison.OrdinalIgnoreCase)
                || text.Equals("MEDIAN",       StringComparison.OrdinalIgnoreCase)
                || text.Equals("MODE",         StringComparison.OrdinalIgnoreCase)
                || text.Equals("FORWARD_FILL", StringComparison.OrdinalIgnoreCase)
                || text.Equals("INTERPOLATE",  StringComparison.OrdinalIgnoreCase);
        }, "CONSTANT, MEDIAN, MODE, FORWARD_FILL, or INTERPOLATE")
        .Select(token => token.ToStringValue().ToUpperInvariant() switch
        {
            "CONSTANT"     => ImputeStrategy.Constant,
            "MEDIAN"       => ImputeStrategy.Median,
            "MODE"         => ImputeStrategy.Mode,
            "FORWARD_FILL" => ImputeStrategy.ForwardFill,
            _              => ImputeStrategy.Interpolate,
        });
```

### 3b. `ImputePartitionByParser`

Reusable sub-parser for the `PARTITION BY col, ...` sub-clause within an `IMPUTE` binding:

```csharp
/// <summary>
/// PARTITION BY column_name [, column_name ...] sub-clause within an IMPUTE binding.
/// PARTITION and BY are existing reserved tokens.
/// </summary>
private static readonly TokenListParser<SqlToken, string[]> ImputePartitionByParser =
    from partitionKw in Token.EqualTo(SqlToken.Partition)
    from byKw in Token.EqualTo(SqlToken.By)
    from columns in Token.EqualTo(SqlToken.Identifier)
        .Select(GetTokenText)
        .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
    select columns;
```

### 3c. `ImputeBindingParser`

Parses one complete `IMPUTE col WITH strategy [constant_value] [PARTITION BY ...] [ORDER BY ...]` binding:

```csharp
/// <summary>
/// A single IMPUTE binding:
/// <c>IMPUTE column_name WITH strategy [constant_expr] [PARTITION BY col [, col ...]] [ORDER BY item [, item ...]]</c>.
/// </summary>
private static readonly TokenListParser<SqlToken, ImputeBinding> ImputeBindingParser =
    from imputeKw in Token.EqualTo(SqlToken.Impute)
    from columnName in Token.EqualTo(SqlToken.Identifier)
    from withKw in Token.EqualTo(SqlToken.With)
    from strategy in ImputeStrategyParser
    from constantValue in (strategy == ImputeStrategy.Constant
        ? ExpressionParser.AsNullable()
        : SP.Return((Expression?)null))
    from partitionBy in ImputePartitionByParser.AsNullable().Try().OptionalOrDefault()
    from orderBy in (
        from orderKw in Token.EqualTo(SqlToken.Order)
        from byKw in Token.EqualTo(SqlToken.By)
        from items in OrderByItemParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        select (IReadOnlyList<OrderByItem>?)items
    ).Try().OptionalOrDefault()
    select new ImputeBinding(
        GetTokenText(columnName),
        strategy,
        ConstantValue: constantValue,
        PartitionBy: partitionBy,
        OrderBy: orderBy,
        Span: ToSpan(imputeKw));
```

> **Note on the conditional `constantValue` branch**: Superpower's parser combinators are static, so the conditional expression above is evaluated per-parse at runtime. The `strategy == ImputeStrategy.Constant` check works because `strategy` is bound before `constantValue` in the monadic chain. If a simpler approach is preferred, an always-try variant works too: try `ExpressionParser.AsNullable()` unconditionally and keep the value only when the strategy is `Constant`.

For robustness, a simpler always-optional approach can replace the conditional:

```csharp
from constantValue in strategy == ImputeStrategy.Constant
    ? ExpressionParser.Select(e => (Expression?)e)
    : SP.Return((Expression?)null)
```

### 3d. `ImputeBindingsParser`

Zero or more `IMPUTE` bindings (analogous to `AssertClausesParser`):

```csharp
/// <summary>Zero or more IMPUTE bindings following the WHERE clause.</summary>
private static readonly TokenListParser<SqlToken, ImputeBinding[]> ImputeBindingsParser =
    ImputeBindingParser.Many();
```

### 3e. Update `SelectStatementParser`

In `SelectStatementParser` (line ~2640 in `SqlParser.cs`), add the `IMPUTE` clause between `WHERE` and `GROUP BY`. Change all remaining positional parameters to named arguments to accommodate the new `Impute` field in `SelectStatement`.

**Before** (the relevant slice):
```csharp
from whereClause in WhereClauseParser.OptionalOrDefault()
from groupByClause in GroupByClauseParser.OptionalOrDefault()
```

**After**:
```csharp
from whereClause in WhereClauseParser.OptionalOrDefault()
from imputeBindings in ImputeBindingsParser
from groupByClause in GroupByClauseParser.OptionalOrDefault()
```

And in the `select new SelectStatement(...)` block, add:
```csharp
Impute: imputeBindings.Length > 0 ? imputeBindings : null,
```

Apply the same change to `BareSelectStatementParser` (which is used for set-operation branches).

### 3f. Update `ParseWithRecovery`

In `ParseWithRecovery`, add an `IMPUTE` recovery block between the `WHERE` block and the `GROUP BY` block:

```csharp
// ── IMPUTE bindings ──
List<ImputeBinding> imputeBindings = new();
while (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Impute)
{
    TokenList<SqlToken> remaining = new(tokenArray[position..]);
    TokenListParserResult<SqlToken, ImputeBinding> imputeResult =
        ImputeBindingParser.TryParse(remaining);

    if (!imputeResult.HasValue)
    {
        AddErrorFromToken(errors, tokenArray, position, "Invalid IMPUTE binding.");
        position = SkipToNextClauseIndex(tokenArray, position + 1);
        break;
    }
    else
    {
        imputeBindings.Add(imputeResult.Value);
        position += CountConsumed(tokenArray, position, imputeResult.Remainder);
    }
}
```

Then add `Impute: imputeBindings.Count > 0 ? imputeBindings.ToArray() : null` to the final `new SelectStatement(...)` constructor call.

### 3g. Update `ClauseStartTokens`

Add `SqlToken.Impute` to the `ClauseStartTokens` set so that the error-recovery parser can synchronize at `IMPUTE` keywords:

```csharp
SqlToken.Impute,
```

---

## Phase 4: Execution Model

### 4a. `ResolvedImputeBinding` — planner-internal type

The planner resolves `ImputeBinding` → `ResolvedImputeBinding` before constructing `ImputeOperator`. This separates AST concerns from execution concerns.

**File:** `src/DatumIngest/Execution/Operators/ResolvedImputeBinding.cs` (new)

```csharp
/// <summary>
/// A fully resolved imputation binding produced by the query planner.
/// The <see cref="ColumnOrdinal"/> avoids repeated name lookups per row.
/// For <see cref="ImputeStrategy.Constant"/>, <see cref="ImputeStrategy.Median"/>,
/// and <see cref="ImputeStrategy.Mode"/>, <see cref="FillValue"/> holds the
/// pre-computed fill value. For <see cref="ImputeStrategy.ForwardFill"/> and
/// <see cref="ImputeStrategy.Interpolate"/>, it is <see cref="DataValue.Null"/>.
/// </summary>
internal sealed class ResolvedImputeBinding
{
    internal int ColumnOrdinal { get; init; }
    internal ImputeStrategy Strategy { get; init; }
    internal DataValue FillValue { get; init; }
    internal IReadOnlyList<int>? PartitionByOrdinals { get; init; }
    internal IReadOnlyList<OrderByItem>? OrderBy { get; init; }
}
```

### 4b. `ImputeStatisticsPass` — pre-scan for MEDIAN and MODE

**File:** `src/DatumIngest/Execution/Operators/ImputeStatisticsPass.cs` (new)

Drains the source operator once, accumulating statistics for only the columns that need `MEDIAN` or `MODE`. Returns a mapping from column name to computed fill value.

```csharp
/// <summary>
/// Computes fill values for <see cref="ImputeStrategy.Median"/> and
/// <see cref="ImputeStrategy.Mode"/> columns by draining a source operator
/// in a single pre-scan pass.
/// </summary>
internal static class ImputeStatisticsPass
{
    /// <summary>
    /// Drains <paramref name="source"/> and returns the computed fill value
    /// for each binding that requires a pre-scan (Median or Mode).
    /// </summary>
    /// <param name="source">
    /// A freshly-planned operator tree covering only the FROM and WHERE portions
    /// of the query. Must be independent of the main execution source.
    /// </param>
    /// <param name="bindings">
    /// Only the Median and Mode bindings. Constant, ForwardFill, and Interpolate
    /// bindings are not passed here.
    /// </param>
    /// <param name="schema">Column schema used to resolve column ordinals.</param>
    /// <param name="context">Execution context for the pre-scan operator tree.</param>
    /// <returns>
    /// A dictionary mapping column name to the computed fill value.
    /// </returns>
    internal static async Task<IReadOnlyDictionary<string, DataValue>> RunAsync(
        IQueryOperator source,
        IEnumerable<ImputeBinding> bindings,
        ResultSchema schema,
        ExecutionContext context)
    {
        // Build per-column accumulators.
        Dictionary<string, IStatisticAccumulator> accumulators = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> ordinals = new(StringComparer.OrdinalIgnoreCase);

        foreach (ImputeBinding binding in bindings)
        {
            int ordinal = schema.GetOrdinal(binding.ColumnName);
            ordinals[binding.ColumnName] = ordinal;

            accumulators[binding.ColumnName] = binding.Strategy switch
            {
                ImputeStrategy.Median => new QuantileAccumulator(),
                ImputeStrategy.Mode   => new CategoricalDiagnosticsAccumulator(),
                _ => throw new InvalidOperationException(
                    $"Strategy {binding.Strategy} does not require a pre-scan."),
            };
        }

        // Drain source.
        await foreach (RowBatch batch in source.ExecuteAsync(context))
        {
            foreach (Row row in batch.Rows)
            {
                foreach (KeyValuePair<string, IStatisticAccumulator> entry in accumulators)
                {
                    DataValue value = row[ordinals[entry.Key]];
                    if (!value.IsNull)
                    {
                        entry.Value.Add(value);
                    }
                }
            }
        }

        // Extract fill values.
        Dictionary<string, DataValue> fillValues = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IStatisticAccumulator> entry in accumulators)
        {
            StatisticResult result = entry.Value.GetResult();
            ImputeStrategy strategy = bindings
                .First(b => string.Equals(b.ColumnName, entry.Key, StringComparison.OrdinalIgnoreCase))
                .Strategy;

            fillValues[entry.Key] = strategy == ImputeStrategy.Median
                ? ExtractMedian(result)
                : ExtractMode(result);
        }

        return fillValues;
    }

    private static DataValue ExtractMedian(StatisticResult result)
    {
        // QuantileAccumulator.GetResult() returns a StatisticResult with a
        // Percentiles dictionary keyed by int percentile (50 = P50 = median).
        return result.Percentiles.TryGetValue(50, out double median)
            ? new DataValue(median)
            : DataValue.Null;
    }

    private static DataValue ExtractMode(StatisticResult result)
    {
        // CategoricalDiagnosticsAccumulator returns TopValues as an ordered list
        // (most-frequent first).
        return result.TopValues is { Count: > 0 }
            ? result.TopValues[0].Value
            : DataValue.Null;
    }
}
```

> The exact `StatisticResult` API (field names for `Percentiles` and `TopValues`) must be verified against the actual type in `src/DatumIngest/Statistics/`. Adjust field access accordingly.

### 4c. `ImputeOperator`

**File:** `src/DatumIngest/Execution/Operators/ImputeOperator.cs` (new)

Mirrors `FilterOperator` in structure. It wraps a source operator and rewrites null cells in each batch.

```csharp
/// <summary>
/// Fills null values in specified columns using pre-resolved imputation strategies.
/// Sits between the WHERE filter and GROUP BY in the operator pipeline.
/// </summary>
internal sealed class ImputeOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<ResolvedImputeBinding> _bindings;

    /// <summary>
    /// Initialises the operator with the source and the resolved bindings whose
    /// fill values have already been computed by the planner.
    /// </summary>
    internal ImputeOperator(
        IQueryOperator source,
        IReadOnlyList<ResolvedImputeBinding> bindings)
    {
        _source = source;
        _bindings = bindings;
    }

    /// <inheritdoc/>
    public string DescribeForExplain() =>
        $"Impute ({string.Join(", ", _bindings.Select(b => $"{b.Strategy}[{b.ColumnOrdinal}]"))})";

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Per-partition state for ForwardFill and Interpolate.
        Dictionary<string, DataValue> lastNonNull = new();
        // Buffered null runs per column for Interpolate (column ordinal → buffered rows).
        // Populated lazily; the full buffering logic lives in a helper to keep this method readable.

        await foreach (RowBatch batch in _source.ExecuteAsync(context, cancellationToken))
        {
            RowBatch filled = ApplyBindings(batch, lastNonNull);
            yield return filled;
        }
    }

    private RowBatch ApplyBindings(RowBatch batch, Dictionary<string, DataValue> lastNonNull)
    {
        // For Constant/Median/Mode: purely functional — build a new batch with nulls replaced.
        // For ForwardFill: update lastNonNull state and replace nulls.
        // For Interpolate: buffer null runs (handled more explicitly in a full implementation).
        // ...
    }
}
```

> `FORWARD_FILL` and `INTERPOLATE` maintain `lastNonNull` state across batches. `INTERPOLATE` additionally needs to buffer null runs until a trailing non-null closes the gap. For the Phase 3 implementation (see Implementation Phases), a design that accumulates buffered rows for open interpolation runs within a single `List<Row>` scratch buffer is recommended.

**Partition key hashing**: For `PARTITION BY`, compute a composite key from the column ordinals. An efficient approach is `string.Join('\0', partitionByOrdinals.Select(o => row[o].ToString()))` for correctness during initial implementation. Replace with a value-tuple hash or `HashCode.Combine` in a subsequent performance pass.

---

## Phase 5: QueryPlanner Changes

**File:** `src/DatumIngest/Execution/QueryPlanner.cs`

### 5a. Guard in `TryPlanColumnar`

The columnar fast-path cannot handle `IMPUTE`. Add the guard alongside the existing `Qualify`, `Assertions`, and `LetBindings` guards:

```csharp
if (statement.Impute is { Count: > 0 }) return false;
```

### 5b. Two-phase setup in `PlanCore`

In `PlanCore`, between the `FilterOperator` (WHERE, step 3) and the late-materialization step (3b), add step 3a for IMPUTE:

```csharp
// ── Step 3a: IMPUTE — fill nulls in post-filter rows before aggregation ──
if (statement.Impute is { Count: > 0 })
{
    bool needsPreScan = statement.Impute.Any(b =>
        b.Strategy is ImputeStrategy.Median or ImputeStrategy.Mode);

    IReadOnlyDictionary<string, DataValue> preComputedFillValues =
        ImmutableDictionary<string, DataValue>.Empty;

    if (needsPreScan)
    {
        // Build an independent source tree covering only FROM + WHERE.
        // Re-plan the scan and filter without any impute/group/project operators
        // so the pre-scan sees the same row set as the main pipeline.
        IQueryOperator preScanSource = PlanPreScanSource(statement, context);

        IEnumerable<ImputeBinding> preScanBindings = statement.Impute
            .Where(b => b.Strategy is ImputeStrategy.Median or ImputeStrategy.Mode);

        preComputedFillValues = await ImputeStatisticsPass.RunAsync(
            preScanSource,
            preScanBindings,
            currentSchema,
            context);
    }

    IReadOnlyList<ResolvedImputeBinding> resolvedBindings =
        ResolveImputeBindings(statement.Impute, currentSchema, preComputedFillValues);

    source = new ImputeOperator(source, resolvedBindings);
}
```

### 5c. `PlanPreScanSource` helper

A private helper that re-plans `FROM` + `JOIN` + `WHERE` without any higher operators, to produce a clean source for the pre-scan. This avoids re-entering the full `PlanCore` recursion:

```csharp
/// <summary>
/// Plans an independent source operator covering only the FROM, JOIN, and WHERE
/// clauses of <paramref name="statement"/>. Used for imputation pre-scan passes
/// that must observe the same filtered row set as the main query.
/// </summary>
private IQueryOperator PlanPreScanSource(SelectStatement statement, ExecutionContext context)
{
    IQueryOperator scanSource = PlanFromClause(statement, context);
    scanSource = PlanJoins(statement, scanSource, context);

    if (statement.Where is not null)
    {
        scanSource = new FilterOperator(scanSource, statement.Where);
    }

    return scanSource;
}
```

### 5d. `ResolveImputeBindings` helper

Resolves column names to ordinals and merges pre-computed fill values into `ResolvedImputeBinding` instances:

```csharp
private static IReadOnlyList<ResolvedImputeBinding> ResolveImputeBindings(
    IReadOnlyList<ImputeBinding> bindings,
    ResultSchema schema,
    IReadOnlyDictionary<string, DataValue> preComputedFillValues)
{
    List<ResolvedImputeBinding> resolved = new(bindings.Count);

    foreach (ImputeBinding binding in bindings)
    {
        int ordinal = schema.GetOrdinal(binding.ColumnName);

        DataValue fillValue = binding.Strategy switch
        {
            ImputeStrategy.Constant   => EvaluateConstant(binding.ConstantValue!),
            ImputeStrategy.Median     => preComputedFillValues.GetValueOrDefault(binding.ColumnName, DataValue.Null),
            ImputeStrategy.Mode       => preComputedFillValues.GetValueOrDefault(binding.ColumnName, DataValue.Null),
            ImputeStrategy.ForwardFill => DataValue.Null,  // state maintained at runtime
            ImputeStrategy.Interpolate => DataValue.Null,  // state maintained at runtime
            _ => throw new InvalidOperationException($"Unknown ImputeStrategy: {binding.Strategy}"),
        };

        IReadOnlyList<int>? partitionByOrdinals = binding.PartitionBy?
            .Select(schema.GetOrdinal)
            .ToArray();

        resolved.Add(new ResolvedImputeBinding
        {
            ColumnOrdinal = ordinal,
            Strategy = binding.Strategy,
            FillValue = fillValue,
            PartitionByOrdinals = partitionByOrdinals,
            OrderBy = binding.OrderBy,
        });
    }

    return resolved;
}
```

---

## Phase 6: Language Server Diagnostics

**File:** `src/DatumIngest.LanguageServer/` (semantic analysis passes)

The following diagnostics should be implemented alongside Phase 1 (they do not block execution but improve authoring experience):

| Code | Severity | Condition |
|---|---|---|
| `IMPUTE001` | Error | `CONSTANT` value's inferred type is incompatible with the column's type (e.g., `IMPUTE age WITH CONSTANT 'hello'` where `age` is numeric) |
| `IMPUTE002` | Error | `INTERPOLATE` applied to a non-numeric column |
| `IMPUTE003` | Warning | `MEDIAN` applied to a non-numeric column |
| `IMPUTE004` | Warning | `MODE` applied to a continuous floating-point column (mode is usually meaningless for floats) |
| `IMPUTE005` | Warning | `FORWARD_FILL` or `INTERPOLATE` written without an `ORDER BY` sub-clause (results will be non-deterministic) |
| `IMPUTE006` | Error | `IMPUTE` column name does not exist in the schema visible after `FROM`/`JOIN`/`WHERE` |
| `IMPUTE007` | Warning | `CONSTANT` used without a value expression (parser would reject this, but the diagnostic adds clarity) |

---

## Test Plan

**File:** `tests/DatumIngest.Tests/Execution/ImputeTests.cs` (new)

All test classes follow project conventions: XML-documented, explicit types, no `var`, no DTO bags.

### Parser tests

```csharp
// Verifies that each strategy variant round-trips through the parser.
[Fact] void Parse_Constant_IntegerFill()
[Fact] void Parse_Constant_StringFill()
[Fact] void Parse_Median_NoArgs()
[Fact] void Parse_Mode_NoArgs()
[Fact] void Parse_ForwardFill_WithOrderBy()
[Fact] void Parse_ForwardFill_WithPartitionByAndOrderBy()
[Fact] void Parse_Interpolate_WithOrderBy()
[Fact] void Parse_MultipleBindings_InSequence()
[Fact] void Parse_IMPUTE_BeforeGroupBy_IsPositionedCorrectly()
[Fact] void Parse_MissingConstantValue_ThrowsParseException()
[Fact] void Parse_UnknownStrategy_ThrowsParseException()
```

### Operator unit tests — CONSTANT

```csharp
// Verifies pure-streaming behavior.
[Fact] void Constant_NullsBecomeConstantValue()
[Fact] void Constant_NonNullsPassThrough()
[Fact] void Constant_AllNulls_AllFilled()
[Fact] void Constant_NoNulls_Unchanged()
[Fact] void Constant_TypeMismatch_ThrowsExecutionException()
```

### Operator unit tests — MEDIAN / MODE

```csharp
// Verifies two-phase behavior.
[Fact] void Median_NullsBecomeMedianOfNonNullRows()
[Fact] void Median_OddCount_ExactMedian()
[Fact] void Median_EvenCount_InterpolatedMedian()
[Fact] void Median_AllNull_NullFillValue_NullsRemain()
[Fact] void Mode_NullsBecomeMostFrequentValue()
[Fact] void Mode_TiedFrequency_ReturnsOneOfTheTiedValues()
[Fact] void Mode_AllNull_NullFillValue_NullsRemain()
```

### Operator unit tests — FORWARD_FILL

```csharp
[Fact] void ForwardFill_NullAfterNonNull_Propagates()
[Fact] void ForwardFill_NullAtStart_RemainsNull()
[Fact] void ForwardFill_MultiplePartitions_PartitionsAreIndependent()
[Fact] void ForwardFill_AllNullPartition_AllRemainNull()
[Fact] void ForwardFill_NullRunAtEnd_AllPropagatedFromLastNonNull()
[Fact] void ForwardFill_StateCarriedAcrossBatches()
```

### Operator unit tests — INTERPOLATE

```csharp
[Fact] void Interpolate_SingleNullBetweenValues_LinearlyFilled()
[Fact] void Interpolate_MultipleNullsBetweenValues_LinearlyFilled()
[Fact] void Interpolate_NullRunAtStart_FallsBackToForwardFill_RemainsNull()
[Fact] void Interpolate_NullRunAtEnd_FallsBackToForwardFill()
[Fact] void Interpolate_MultiplePartitions_PartitionsAreIndependent()
```

### Integration tests

```csharp
// End-to-end tests that run full queries through the planner and executor.
[Fact] async Task Integration_Constant_ThenGroupBy_AggregatesUseFillValue()
[Fact] async Task Integration_Median_ThenGroupBy_AggregatesUseMedian()
[Fact] async Task Integration_ForwardFill_ThenOrderBy_OutputIsCorrect()
[Fact] async Task Integration_MultiStrategy_SameQuery_AllBindingsApplied()
[Fact] async Task Integration_IMPUTE_PlusLET_LetSeesFilledValues()
[Fact] async Task Integration_IMPUTE_PlusAssert_AssertRunsAfterFill()
[Fact] async Task Integration_ColumnarFastPath_IsDisabledWhenIMPUTEPresent()
```

---

## Implementation Phases

### Phase 1 — CONSTANT only

**Scope**: AST, tokens, parser, `ImputeOperator` (CONSTANT path only), `QueryPlanner` step 3a (no pre-scan), `TryPlanColumnar` guard, language server IMPUTE001/IMPUTE006 diagnostics, parser + CONSTANT unit tests.

This is the minimal vertical slice. It exercises the full integration path without the complexity of the pre-scan or stateful streaming. All subsequent phases bolt onto this foundation.

**Deliverables**:
- `ImputeStrategy` enum (full set of 5 values, even if only Constant is executed)
- `ImputeBinding` record
- `ResolvedImputeBinding` class (Constant path only)
- `ImputeOperator` (only handles `Constant`; throws `NotImplementedException` for others)
- `SelectStatement.Impute` field
- Parser changes (parser parses all 5 strategies; only CONSTANT flows through execution)
- Test coverage: all parser tests + CONSTANT operator + one integration test

**Estimated touch points**: 5 files changed, 2 new files.

### Phase 2 — MEDIAN and MODE

**Scope**: `ImputeStatisticsPass`, `ResolvedImputeBinding` fill value resolution, `QueryPlanner.PlanPreScanSource`, `QueryPlanner.ResolveImputeBindings`, language server IMPUTE002/IMPUTE003/IMPUTE004 diagnostics, MEDIAN/MODE unit + integration tests.

The key challenge is building `PlanPreScanSource` cleanly. The method should share the `PlanFromClause`/`PlanJoins` helpers already used by `PlanCore` — verify these are extracted as private helpers rather than inlined code before starting.

**Estimated touch points**: 4 files changed, 1 new file.

### Phase 3 — FORWARD_FILL and INTERPOLATE

**Scope**: Stateful streaming in `ImputeOperator`, partition key computation, `SortOperator` injection in `QueryPlanner` when `ORDER BY` on the binding specifies an ordering not already provided by the source, language server IMPUTE005 diagnostic, FORWARD_FILL + INTERPOLATE unit + integration tests.

The main risk here is the `INTERPOLATE` buffering logic. Key constraints:
- Null runs can span multiple `RowBatch` objects — the operator must carry open-run state across `yield return` points.
- At the point where a non-null value is received, the buffered null run must be filled and all buffered rows emitted before emitting the current row.
- At the end of input (outer `foreach` exhausted), any remaining buffered null run is closed by falling back to `FORWARD_FILL` from the last known non-null.

An `InterpolationRunBuffer` private nested class or record within `ImputeOperator` is a clean way to encapsulate this per-column, per-partition state without polluting the operator with low-level bookkeeping.

**Estimated touch points**: 2 files changed, 0 new files.

---

## Files Changed Summary

| File | Change |
|---|---|
| `src/DatumIngest.Parsing/Ast/AstNodes.cs` | Add `ImputeStrategy` enum, `ImputeBinding` record, `Impute` field on `SelectStatement` |
| `src/DatumIngest.Parsing/Tokens/SqlToken.cs` | Add `Impute` token enum member and tokenizer entry |
| `src/DatumIngest.Parsing/SqlParser.cs` | Add `ImputeStrategyParser`, `ImputePartitionByParser`, `ImputeBindingParser`, `ImputeBindingsParser`; update `SelectStatementParser`, `BareSelectStatementParser`, `ParseWithRecovery`, `ClauseStartTokens` |
| `src/DatumIngest/Execution/QueryPlanner.cs` | `TryPlanColumnar` guard; `PlanCore` step 3a; `PlanPreScanSource` helper; `ResolveImputeBindings` helper |
| `src/DatumIngest/Execution/Operators/ImputeOperator.cs` | **New** — streaming operator |
| `src/DatumIngest/Execution/Operators/ImputeStatisticsPass.cs` | **New** — pre-scan for MEDIAN/MODE |
| `src/DatumIngest/Execution/Operators/ResolvedImputeBinding.cs` | **New** — planner-internal resolved binding |
| `src/DatumIngest.LanguageServer/` | Semantic diagnostics IMPUTE001–IMPUTE007 |
| `tests/DatumIngest.Tests/Execution/ImputeTests.cs` | **New** — full test suite |
| `docs/sql.md` | Document the IMPUTE clause (syntax, strategies, examples) |
