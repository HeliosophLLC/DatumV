# TRANSFORM Expression — Design Plan

> **Status**: Proposed (complexity **M–L**). No hard prerequisites — desugars entirely to existing window functions and scalar expressions. Tuple destructuring (if landed first) unlocks multi-output strategies cleanly but is not strictly required.

---

## Motivation

Group-relative normalization is one of the most common operations in ML feature engineering. In pandas, it's a single call:

```python
df['z_salary'] = df.groupby('department')['salary'].transform(lambda x: (x - x.mean()) / x.std())
```

In every SQL dialect — DuckDB, BigQuery, Postgres, and DatumIngest today — the equivalent is verbose and error-prone:

```sql
SELECT *,
  (salary - AVG(salary) OVER (PARTITION BY department))
    / NULLIF(STDDEV(salary) OVER (PARTITION BY department), 0)
    AS z_salary
FROM employees
```

This has three problems:

1. **Verbosity**: the pattern requires writing the column name 3–4 times and the partition clause 2+ times per normalized column. Normalizing five columns across three groupings produces 30+ lines of window expressions.
2. **Error surface**: the `NULLIF(..., 0)` guard for zero-variance groups is easy to forget. Missing it produces division-by-zero NULLs or errors that only surface on specific data distributions.
3. **Discoverability**: new users looking for "normalize within group" or "z-score by category" have no search target — they need to know the window function decomposition pattern.

TRANSFORM solves all three by declaring the *intent* (normalize, standardize, rank) and letting the planner expand it into the correct window expressions with built-in safety guards.

No SQL engine offers this. DuckDB and BigQuery both require the manual window-function decomposition. TRANSFORM is a direct port of pandas' most powerful operation — `groupby().transform()` — into SQL, with the rigor of predefined, tested strategies rather than arbitrary lambda bodies.

---

## Syntax

### Single-column, single-strategy

```sql
SELECT *,
  salary TRANSFORM ZSCORE OVER (PARTITION BY department) AS z_salary
FROM employees
```

### Multiple columns — each gets its own TRANSFORM

```sql
SELECT *,
  salary  TRANSFORM ZSCORE     OVER (PARTITION BY department) AS z_salary,
  age     TRANSFORM NORMALIZE  OVER (PARTITION BY department) AS norm_age,
  score   TRANSFORM RANK_PERCENT OVER (PARTITION BY category) AS score_pct
FROM employees
```

### Without PARTITION BY — whole-table transform

```sql
SELECT *,
  price TRANSFORM ZSCORE OVER () AS z_price
FROM products
```

### Combined with LET — memoize for reuse

```sql
SELECT
  LET z = salary TRANSFORM ZSCORE OVER (PARTITION BY department),
  z AS z_salary,
  CASE WHEN z > 2.0 THEN 'outlier' ELSE 'normal' END AS flag
FROM employees
```

### Combined with QUALIFY — filter on transformed values

```sql
SELECT *,
  salary TRANSFORM ZSCORE OVER (PARTITION BY department) AS z_salary
FROM employees
QUALIFY z_salary BETWEEN -3.0 AND 3.0
```

### Grammar (informal EBNF)

```
transform_expression ::= column_ref TRANSFORM strategy OVER '(' [partition_clause] ')'

strategy             ::= ZSCORE
                       | NORMALIZE
                       | RANK_PERCENT
                       | CUMULATIVE_SUM
                       | CUMULATIVE_PRODUCT
                       | LOG_NORMALIZE
                       | ROBUST_ZSCORE
                       | CENTER

partition_clause     ::= PARTITION BY expression (',' expression)*
```

Strategy names are **contextual identifiers** — they are not reserved keywords. They are matched by exact case-insensitive comparison on identifier tokens after the `TRANSFORM` keyword, following the same pattern as `BERNOULLI`/`SYSTEM` in `TABLESAMPLE` and `CONSTANT`/`MEDIAN`/`MODE` in `IMPUTE`.

`TRANSFORM` itself must become a reserved keyword token because it appears in expression position and must be disambiguated from column aliases.

OVER uses the **existing window clause parser**, but frames (`ROWS BETWEEN ...`) are not permitted — TRANSFORM strategies define their own semantics over the full partition. ORDER BY within the OVER clause is not supported (strategies that need ordering like `CUMULATIVE_SUM` use the partition's natural order or require the query to have an enclosing `ORDER BY`).

---

## Strategies

Each strategy defines a mathematical transformation applied per-row relative to the partition's aggregate statistics. All strategies handle edge cases explicitly — zero variance, empty partitions, all-null columns — so users cannot produce silent errors.

### ZSCORE

Standard score (z-score): how many standard deviations a value is from the group mean.

$$z_i = \frac{x_i - \bar{x}}{\sigma}$$

**Desugars to**:

```sql
(column - AVG(column) OVER (PARTITION BY ...))
  / NULLIF(STDDEV(column) OVER (PARTITION BY ...), 0)
```

**Edge cases**:
- Zero variance (all values identical): denominator is 0 → `NULLIF` produces NULL → z-score is NULL. This is semantically correct — a z-score is undefined when there is no variation.
- Single row in partition: `STDDEV` returns NULL (population of 1) → z-score is NULL.
- NULL input values: propagate as NULL through AVG/STDDEV (both ignore NULLs); the subtraction produces NULL.

**Applicability**: numeric columns only (`Float32`, `Float64`, `Int8`–`Int64`). The language server should emit a diagnostic for non-numeric columns.

### NORMALIZE

Min-max scaling to [0, 1] within the partition.

$$n_i = \frac{x_i - x_{\min}}{x_{\max} - x_{\min}}$$

**Desugars to**:

```sql
(column - MIN(column) OVER (PARTITION BY ...))
  / NULLIF(MAX(column) OVER (PARTITION BY ...) - MIN(column) OVER (PARTITION BY ...), 0)
```

**Edge cases**:
- All values identical (max = min): denominator is 0 → `NULLIF` produces NULL → result is NULL. Alternative: could return 0.5 (midpoint). The NULL behavior is chosen for consistency with ZSCORE — constant data has no meaningful normalization.
- NULL input values: MIN/MAX ignore NULLs; NULL inputs produce NULL output.

**Applicability**: numeric columns only.

### RANK_PERCENT

Percentile rank within the partition: fraction of partition values less than or equal to the current value.

$$r_i = \frac{\text{RANK}(x_i)}{N}$$

where RANK is the standard 1-based rank with ties (SQL `RANK()`), and N is the partition count.

**Desugars to**:

```sql
CAST(RANK() OVER (PARTITION BY ... ORDER BY column ASC) AS Float32)
  / COUNT(*) OVER (PARTITION BY ...)
```

**Notes**:
- This strategy implicitly adds `ORDER BY column ASC` to the window specification. The column expression is the sort key.
- Ties receive the same rank, so the percentile correctly reflects the fraction of values ≤ the current value.
- Result range is (0, 1] — never exactly 0 because RANK starts at 1.

**Edge cases**:
- Single row in partition: 1/1 = 1.0.
- All values identical: all rows get rank 1, so 1/N for all rows.

**Applicability**: any orderable column (numeric, string, date, datetime, time, duration).

### CENTER

Mean-centering: subtract the group mean without scaling.

$$c_i = x_i - \bar{x}$$

**Desugars to**:

```sql
column - AVG(column) OVER (PARTITION BY ...)
```

**Edge cases**: none beyond NULL propagation. The simplest strategy.

**Applicability**: numeric columns only.

### CUMULATIVE_SUM

Running sum within the partition, ordered by the query's ORDER BY or the column's natural order.

**Desugars to**:

```sql
SUM(column) OVER (PARTITION BY ... ORDER BY column ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)
```

**Notes**: This is the one strategy that uses a window frame. The implicit ORDER BY is by the column itself unless the query already specifies an ORDER BY that the planner can propagate. This follows the principle that `CUMULATIVE_*` strategies produce deterministic results — without an ordering, a cumulative sum is arbitrary.

**Applicability**: numeric columns only.

### CUMULATIVE_PRODUCT

Running product within the partition.

**Desugars to**:

```sql
EXP(SUM(LN(column)) OVER (PARTITION BY ... ORDER BY column ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW))
```

**Edge cases**:
- Zero values: `LN(0)` is -∞ → `SUM` with -∞ → `EXP(-∞)` = 0. Correct.
- Negative values: `LN` of a negative number is NaN → propagates. The language server should warn when applying `CUMULATIVE_PRODUCT` to a column with known negative values (from manifest statistics if available).
- NULL values: `LN(NULL)` = NULL → excluded from SUM (window aggregates skip NULLs) → product is computed over non-null values only.

**Applicability**: numeric columns only.

### LOG_NORMALIZE

Log-transform followed by min-max normalization within the partition.

$$l_i = \frac{\ln(x_i + 1) - \ln(x_{\min} + 1)}{\ln(x_{\max} + 1) - \ln(x_{\min} + 1)}$$

The `+1` shift handles zero values. This is the standard `log1p` transformation used for right-skewed distributions (revenue, counts, distances).

**Desugars to**:

```sql
LET __log_col = LN(column + 1),
(  __log_col - MIN(LN(column + 1)) OVER (PARTITION BY ...))
/ NULLIF(MAX(LN(column + 1)) OVER (PARTITION BY ...) - MIN(LN(column + 1)) OVER (PARTITION BY ...), 0)
```

The planner should use an internal LET binding to avoid computing `LN(column + 1)` four times. Since TRANSFORM desugars before LET memoization, the planner emits the log-transformed column as a hidden LET binding and references it in the window expressions.

**Edge cases**:
- Negative values: `LN(x + 1)` is valid for x > -1. Values ≤ -1 produce NaN. The language server should warn when manifest statistics show negative values in the column.
- All values identical after log transform: same as NORMALIZE — returns NULL.

**Applicability**: numeric columns only. Best suited for non-negative columns; the language server should warn for columns with negative values.

### ROBUST_ZSCORE

Modified z-score using median and MAD (median absolute deviation), which is resistant to outliers.

$$r_i = \frac{x_i - \tilde{x}}{1.4826 \cdot \text{MAD}}$$

where $\tilde{x}$ is the partition median, MAD = median(|$x_i - \tilde{x}$|), and 1.4826 is the consistency constant for a normal distribution.

**Desugars to**:

```sql
(column - MEDIAN(column) OVER (PARTITION BY ...))
  / NULLIF(1.4826 * MEDIAN(ABS(column - MEDIAN(column) OVER (PARTITION BY ...))) OVER (PARTITION BY ...), 0)
```

**Implementation note**: This is the most complex desugaring because the inner MEDIAN reference appears inside the outer MEDIAN's argument. The planner must recognize this pattern and schedule two window passes:

1. First pass: compute `MEDIAN(column) OVER (PARTITION BY ...)` and bind it as a hidden column `__median_col`.
2. Second pass: compute `MEDIAN(ABS(column - __median_col)) OVER (PARTITION BY ...)` as `__mad_col`.
3. Final projection: `(column - __median_col) / NULLIF(1.4826 * __mad_col, 0)`.

This two-pass pattern is architecturally identical to windowized expressions that reference other window results — the WindowOperator already supports multiple window columns computed in sequence.

**Edge cases**:
- Zero MAD (more than half the values are identical to the median): returns NULL (via NULLIF).
- Single row: MEDIAN returns the value itself, MAD = 0 → NULL.

**Applicability**: numeric columns only.

---

## Strategy Summary

| Strategy | Formula | Window Functions Used | Passes | Applicability |
|---|---|---|:---:|---|
| `ZSCORE` | $(x - \bar{x}) / \sigma$ | AVG, STDDEV | 1 | Numeric |
| `NORMALIZE` | $(x - \min) / (\max - \min)$ | MIN, MAX | 1 | Numeric |
| `RANK_PERCENT` | $\text{RANK} / N$ | RANK, COUNT | 1 | Orderable |
| `CENTER` | $x - \bar{x}$ | AVG | 1 | Numeric |
| `CUMULATIVE_SUM` | $\sum_{j \leq i} x_j$ | SUM (framed) | 1 | Numeric |
| `CUMULATIVE_PRODUCT` | $\prod_{j \leq i} x_j$ | SUM of LN (framed), EXP | 1 | Numeric (non-negative) |
| `LOG_NORMALIZE` | $(\ln(x+1) - \min') / (\max' - \min')$ | MIN, MAX + LN | 1 | Numeric (non-negative) |
| `ROBUST_ZSCORE` | $(x - \tilde{x}) / (1.4826 \cdot \text{MAD})$ | MEDIAN (×2) | 2 | Numeric |

---

## Execution Model: Planner-Level Desugaring

TRANSFORM is implemented entirely as a **planner rewrite** — there is no `TransformOperator`. The parser produces a `TransformExpression` AST node, and the planner expands it into the equivalent window expressions before the standard window planning pass. This means:

1. The `WindowOperator` handles all actual computation — no new operator.
2. Window function coalescing applies: two TRANSFORM expressions sharing the same `PARTITION BY` reuse the same hash-partition and sort.
3. EXPLAIN shows the desugared window expressions, making the actual computation transparent.
4. Query unit costing follows existing window function costs with no special-case logic.

### Desugaring location in the planner

The expansion runs as a new pass after LET binding expansion and before aggregate/window rewriting. This is the same slot where tuple destructuring expansion lives (if implemented). The pass walks the SELECT column list and LET binding expressions, replacing each `TransformExpression` node with the equivalent scalar/window expression tree.

### Desugaring algorithm

```
For each TransformExpression(column, strategy, partitionBy):
  1. Build the partition OVER clause from partitionBy expressions
  2. Switch on strategy:
     - ZSCORE:
         Emit hidden LET: __avg_N = AVG(column) OVER (partition)
         Emit hidden LET: __std_N = STDDEV(column) OVER (partition)
         Replace expression with: (column - __avg_N) / NULLIF(__std_N, 0)
     - NORMALIZE:
         Emit hidden LET: __min_N = MIN(column) OVER (partition)
         Emit hidden LET: __max_N = MAX(column) OVER (partition)
         Replace expression with: (column - __min_N) / NULLIF(__max_N - __min_N, 0)
     - RANK_PERCENT:
         Emit hidden LET: __rank_N = RANK() OVER (partition ORDER BY column ASC)
         Emit hidden LET: __count_N = COUNT(*) OVER (partition)
         Replace expression with: CAST(__rank_N AS Float32) / __count_N
     - CENTER:
         Emit hidden LET: __avg_N = AVG(column) OVER (partition)
         Replace expression with: column - __avg_N
     - CUMULATIVE_SUM:
         Replace expression with: SUM(column) OVER (partition ORDER BY column ROWS UNBOUNDED PRECEDING)
     - CUMULATIVE_PRODUCT:
         Replace expression with: EXP(SUM(LN(column)) OVER (partition ORDER BY column ROWS UNBOUNDED PRECEDING))
     - LOG_NORMALIZE:
         Emit hidden LET: __log_N = LN(column + 1)
         Emit hidden LET: __log_min_N = MIN(__log_N) OVER (partition)
         Emit hidden LET: __log_max_N = MAX(__log_N) OVER (partition)
         Replace expression with: (__log_N - __log_min_N) / NULLIF(__log_max_N - __log_min_N, 0)
     - ROBUST_ZSCORE:
         Emit hidden LET: __median_N = MEDIAN(column) OVER (partition)
         Emit hidden LET: __mad_N = MEDIAN(ABS(column - __median_N)) OVER (partition)
         Replace expression with: (column - __median_N) / NULLIF(1.4826 * __mad_N, 0)
  3. Increment N (unique counter per query to avoid name collisions)
```

Hidden LET bindings use the `__transform_` prefix and are never visible in output. They participate in the standard LET memoization path — each intermediate window result is computed once and cached in the augmented row.

### Window function coalescing

Multiple TRANSFORM expressions sharing the same `PARTITION BY` clause naturally coalesce. Consider:

```sql
SELECT *,
  salary TRANSFORM ZSCORE     OVER (PARTITION BY department) AS z_salary,
  age    TRANSFORM NORMALIZE  OVER (PARTITION BY department) AS norm_age
FROM employees
```

After desugaring, the hidden bindings are:

```
__transform_avg_0  = AVG(salary) OVER (PARTITION BY department)
__transform_std_0  = STDDEV(salary) OVER (PARTITION BY department)
__transform_min_1  = MIN(age) OVER (PARTITION BY department)
__transform_max_1  = MAX(age) OVER (PARTITION BY department)
```

All four window functions share `PARTITION BY department` with no ORDER BY and the default frame. The existing `WindowOperator` groups window columns by their specification and processes all four in a single partition-sort pass. This is the same coalescing that already happens when users manually write multiple window functions with the same OVER clause.

---

## Phase 1: AST Changes

**File:** `src/DatumIngest.Parsing/Ast/AstNodes.cs`

### 1a. Add `TransformStrategy` enum

```csharp
/// <summary>Predefined group-relative transformation strategies for TRANSFORM expressions.</summary>
public enum TransformStrategy
{
    /// <summary>Standard score: (x - mean) / stddev.</summary>
    ZScore,

    /// <summary>Min-max scaling to [0, 1]: (x - min) / (max - min).</summary>
    Normalize,

    /// <summary>Percentile rank: RANK / COUNT within the partition.</summary>
    RankPercent,

    /// <summary>Mean-centering: x - mean.</summary>
    Center,

    /// <summary>Running sum within the partition.</summary>
    CumulativeSum,

    /// <summary>Running product within the partition.</summary>
    CumulativeProduct,

    /// <summary>Log-transform then min-max normalize: (ln(x+1) - min') / (max' - min').</summary>
    LogNormalize,

    /// <summary>Robust z-score using median and MAD: (x - median) / (1.4826 * MAD).</summary>
    RobustZScore,
}
```

### 1b. Add `TransformExpression` to the `Expression` hierarchy

```csharp
/// <summary>
/// A group-relative transformation: <c>column TRANSFORM strategy OVER (PARTITION BY ...)</c>.
/// The planner desugars this into equivalent window function expressions before execution.
/// </summary>
/// <param name="Column">The column expression to transform.</param>
/// <param name="Strategy">The predefined transformation strategy.</param>
/// <param name="PartitionBy">
/// Optional partition columns. When empty, the entire result set is a single partition.
/// </param>
/// <param name="Span">Source span for diagnostic reporting.</param>
public sealed record TransformExpression(
    Expression Column,
    TransformStrategy Strategy,
    IReadOnlyList<Expression>? PartitionBy = null,
    SourceSpan? Span = null) : Expression;
```

This record extends the existing `Expression` base type (which `ColumnReference`, `FunctionCall`, `BinaryExpression`, etc. already extend). It participates in the expression visitor pattern and is eligible for the same rewriting passes.

---

## Phase 2: Token Changes

**File:** `src/DatumIngest.Parsing/Tokens/SqlToken.cs`

Add one reserved keyword token:

```csharp
/// <summary>The <c>TRANSFORM</c> keyword for group-relative transformation expressions.</summary>
Transform,
```

In the tokenizer match table, add:

```csharp
.Match(Span.EqualToIgnoreCase("transform"), SqlToken.Transform)
```

The strategy names (`ZSCORE`, `NORMALIZE`, `RANK_PERCENT`, `CENTER`, `CUMULATIVE_SUM`, `CUMULATIVE_PRODUCT`, `LOG_NORMALIZE`, `ROBUST_ZSCORE`) are parsed as contextual identifiers — no tokens needed. This follows the established pattern for TABLESAMPLE methods and IMPUTE strategies.

---

## Phase 3: Parser Changes

**File:** `src/DatumIngest.Parsing/SqlParser.cs`

### 3a. `TransformStrategyParser`

```csharp
/// <summary>
/// Parses a TRANSFORM strategy name as a contextual identifier.
/// </summary>
private static readonly TokenListParser<SqlToken, TransformStrategy> TransformStrategyParser =
    Token.EqualTo(SqlToken.Identifier)
        .Where(token =>
        {
            string text = token.ToStringValue();
            return text.Equals("ZSCORE",             StringComparison.OrdinalIgnoreCase)
                || text.Equals("NORMALIZE",          StringComparison.OrdinalIgnoreCase)
                || text.Equals("RANK_PERCENT",       StringComparison.OrdinalIgnoreCase)
                || text.Equals("CENTER",             StringComparison.OrdinalIgnoreCase)
                || text.Equals("CUMULATIVE_SUM",     StringComparison.OrdinalIgnoreCase)
                || text.Equals("CUMULATIVE_PRODUCT", StringComparison.OrdinalIgnoreCase)
                || text.Equals("LOG_NORMALIZE",      StringComparison.OrdinalIgnoreCase)
                || text.Equals("ROBUST_ZSCORE",      StringComparison.OrdinalIgnoreCase);
        }, "ZSCORE, NORMALIZE, RANK_PERCENT, CENTER, CUMULATIVE_SUM, CUMULATIVE_PRODUCT, LOG_NORMALIZE, or ROBUST_ZSCORE")
        .Select(token => token.ToStringValue().ToUpperInvariant() switch
        {
            "ZSCORE"             => TransformStrategy.ZScore,
            "NORMALIZE"          => TransformStrategy.Normalize,
            "RANK_PERCENT"       => TransformStrategy.RankPercent,
            "CENTER"             => TransformStrategy.Center,
            "CUMULATIVE_SUM"     => TransformStrategy.CumulativeSum,
            "CUMULATIVE_PRODUCT" => TransformStrategy.CumulativeProduct,
            "LOG_NORMALIZE"      => TransformStrategy.LogNormalize,
            "ROBUST_ZSCORE"      => TransformStrategy.RobustZScore,
            _ => throw new InvalidOperationException(),
        });
```

### 3b. `TransformExpressionParser`

TRANSFORM is parsed as a **postfix operator** on an expression — it appears after a column reference or expression. This avoids ambiguity with the existing expression grammar:

```
expression TRANSFORM strategy OVER ( [PARTITION BY expression (, expression)*] )
```

The parser hooks into the existing `SelectColumnParser` path. When a column expression is followed by the `TRANSFORM` token, the parser consumes the transform suffix and wraps the left-hand expression in a `TransformExpression`:

```csharp
/// <summary>
/// Attempts to parse a TRANSFORM suffix on an expression.
/// Returns the original expression unchanged if no TRANSFORM keyword follows.
/// </summary>
private static readonly TokenListParser<SqlToken, Expression> TransformSuffixParser =
    from column in ExpressionParser
    from transform in Token.EqualTo(SqlToken.Transform).OptionalOrDefault()
    from result in transform.HasValue
        ? from strategy in TransformStrategyParser
          from over in Token.EqualTo(SqlToken.Over)
          from open in Token.EqualTo(SqlToken.LeftParen)
          from partitionBy in (
              from pb in Token.EqualTo(SqlToken.Partition)
              from by in Token.EqualTo(SqlToken.By)
              from columns in ExpressionParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
              select columns
          ).OptionalOrDefault()
          from close in Token.EqualTo(SqlToken.RightParen)
          select (Expression)new TransformExpression(column, strategy, partitionBy?.ToList(), /* span */)
        : Parse.Return(column)
    select result;
```

The exact integration point depends on how the SELECT column expression parser chains — the key constraint is that `TRANSFORM` must bind tighter than `AS` (alias) but looser than arithmetic operators. This is the same precedence level as `OVER` on window functions.

### 3c. Parsing ambiguity analysis

There is no ambiguity with existing syntax:

- `column TRANSFORM ZSCORE` — `TRANSFORM` is a reserved keyword, so it cannot be mistaken for a column alias (aliases are identifiers, not keywords).
- `transform(x)` as a function name — reserved keywords cannot be function names in the current grammar. If a function named `transform` existed, it would need to be quoted: `"transform"(x)`. No such function exists today.
- `TRANSFORM` in `CREATE TABLE` or DDL — DatumIngest has no DDL `ALTER TABLE ... TRANSFORM` syntax, so there is no conflict.

---

## Phase 4: Planner Expansion

**File:** `src/DatumIngest/Execution/QueryPlanner.cs`

### 4a. New expansion pass: `ExpandTransformExpressions`

Insert a new pass in the planner pipeline after LET binding expansion (and tuple destructuring expansion, if present) and **before** aggregate/window rewriting:

```
Plan pipeline:
  1. Expand tuple destructuring (if present)
  2. Expand TRANSFORM expressions  ← new
  3. Rewrite LET aggregates
  4. Rewrite LET window functions
  5. Build window operator
  ...
```

The pass walks all expressions in the SELECT column list and LET binding expressions. For each `TransformExpression` found, it:

1. Generates hidden LET bindings for the required window functions (with `__transform_` prefixed names and a unique counter suffix).
2. Replaces the `TransformExpression` node with the final scalar expression referencing the hidden bindings.
3. Prepends the hidden LET bindings to the query's `LetBindings` list.

### 4b. Expansion context

The expansion must track:

- A unique counter (`int transformIndex`) to generate non-colliding hidden binding names.
- The set of already-emitted hidden bindings, to enable deduplication when multiple TRANSFORM expressions share the same window function (e.g., two columns both using ZSCORE with the same PARTITION BY share the same AVG window computation? No — they compute AVG of different columns, so no deduplication across columns. But within a single ROBUST_ZSCORE, the two MEDIAN passes share the partition spec).

### 4c. Integration with LET

A TRANSFORM expression inside a LET binding:

```sql
LET z = salary TRANSFORM ZSCORE OVER (PARTITION BY department)
```

The expansion replaces the LET's expression with the desugared scalar expression and adds the hidden window-function LET bindings before it. The original LET binding `z` then references the hidden bindings via its rewritten expression. Memoization applies: `z` is computed once per row and cached.

### 4d. Integration with GROUP BY

TRANSFORM expressions in a `GROUP BY` context follow the same rules as window functions in GROUP BY: they are invalid because window functions cannot appear in GROUP BY or HAVING. The planner should emit a diagnostic:

```
Error: TRANSFORM expressions cannot be used in GROUP BY or HAVING clauses.
TRANSFORM computes group-relative values per row — it is incompatible with row aggregation.
```

TRANSFORM expressions in the SELECT list alongside GROUP BY are also invalid (same as window functions), unless the TRANSFORM appears in a subquery or CTE:

```sql
-- Invalid: TRANSFORM and GROUP BY in the same SELECT
SELECT category, AVG(price), price TRANSFORM ZSCORE OVER (PARTITION BY category)
FROM products
GROUP BY category

-- Valid: TRANSFORM in a CTE, then aggregate
WITH normalized AS (
  SELECT category, price TRANSFORM ZSCORE OVER (PARTITION BY category) AS z_price
  FROM products
)
SELECT category, AVG(z_price) FROM normalized GROUP BY category
```

---

## Phase 5: Type Resolution

**File:** `src/DatumIngest/Execution/ExpressionTypeResolver.cs`

Add a case for `TransformExpression`:

- For `ZSCORE`, `NORMALIZE`, `CENTER`, `LOG_NORMALIZE`, `ROBUST_ZSCORE`: return `DataKind.Float32` (regardless of input kind — integer columns produce float z-scores).
- For `RANK_PERCENT`: return `DataKind.Float32`.
- For `CUMULATIVE_SUM`: return the column's kind (INT → INT, FLOAT → FLOAT) — no type change.
- For `CUMULATIVE_PRODUCT`: return `DataKind.Float32` (due to EXP/LN roundtrip).

### Diagnostic validation

The type resolver should emit diagnostics for:

1. Non-numeric column used with a numeric-only strategy (ZSCORE, NORMALIZE, CENTER, LOG_NORMALIZE, ROBUST_ZSCORE, CUMULATIVE_SUM, CUMULATIVE_PRODUCT).
2. CUMULATIVE_PRODUCT on a column with known negative values (from manifest statistics, if available).
3. LOG_NORMALIZE on a column with values ≤ -1 (from manifest min, if available).

---

## Phase 6: Language Server Integration

**File:** `src/DatumIngest.LanguageServer/SemanticAnalyzer.cs` and related

### Completions

- After a column reference or expression, suggest `TRANSFORM` as a keyword.
- After `TRANSFORM`, suggest strategy names: `ZSCORE`, `NORMALIZE`, `RANK_PERCENT`, `CENTER`, `CUMULATIVE_SUM`, `CUMULATIVE_PRODUCT`, `LOG_NORMALIZE`, `ROBUST_ZSCORE`.
- After strategy name, suggest `OVER`.
- Inside `OVER (...)`, suggest `PARTITION BY` and column names.

### Hover

- On `TRANSFORM` keyword: brief description of the TRANSFORM expression syntax.
- On strategy names: description and formula (e.g., *"ZSCORE: Standard score — (x - mean) / stddev — within each partition"*).

### Diagnostics

Emit the type-mismatch diagnostics described in Phase 5 as language server warnings/errors.

---

## Phase 7: Documentation Updates

**File:** `docs/sql.md`

Add a new section after the Window Functions section:

```markdown
### TRANSFORM Expressions

TRANSFORM applies a predefined group-relative transformation to a column.
Each row's value is normalized, ranked, or accumulated relative to its partition.

    column TRANSFORM strategy OVER ([PARTITION BY expression [, ...]])

Available strategies:

| Strategy | Description |
|---|---|
| ZSCORE | (x - mean) / stddev |
| NORMALIZE | (x - min) / (max - min) |
| RANK_PERCENT | RANK / COUNT |
| CENTER | x - mean |
| CUMULATIVE_SUM | Running sum |
| CUMULATIVE_PRODUCT | Running product |
| LOG_NORMALIZE | (ln(x+1) - min') / (max' - min') |
| ROBUST_ZSCORE | (x - median) / (1.4826 × MAD) |
```

Include examples for the common cases (single column, multiple columns, combined with LET, combined with QUALIFY).

**File:** `docs/functions.md`

No changes — TRANSFORM is a syntactic construct, not a function.

---

## Test Plan

### Parsing tests

`tests/DatumIngest.Tests/Parsing/TransformParsingTests.cs`:

1. `ZScore_SingleColumn_ParsesCorrectly`
2. `Normalize_WithPartitionBy_ParsesCorrectly`
3. `RankPercent_WithMultiplePartitionColumns_ParsesCorrectly`
4. `Transform_WithoutPartitionBy_EmptyOverClause_ParsesCorrectly`
5. `Transform_CaseInsensitiveStrategy_ParsesCorrectly`
6. `Transform_InvalidStrategy_ProducesParseError`
7. `Transform_MissingOver_ProducesParseError`
8. `Transform_WithAlias_ParsesCorrectly`
9. `Transform_InsideLet_ParsesCorrectly`
10. `Transform_MultipleInSelectList_ParsesCorrectly`

### Planner expansion tests

`tests/DatumIngest.Tests/Execution/TransformExpansionTests.cs`:

1. `ZScore_ExpandsToAvgStddevWindow` — verify the desugared expression tree
2. `Normalize_ExpandsToMinMaxWindow`
3. `RankPercent_ExpandsToRankCountWindow`
4. `Center_ExpandsToAvgWindow`
5. `CumulativeSum_ExpandsToFramedSumWindow`
6. `LogNormalize_ExpandsWithHiddenLetForLog`
7. `RobustZScore_ExpandsToTwoMedianPasses`
8. `SharedPartition_CoalescesWindowSpecs` — verify two TRANSFORM expressions with the same PARTITION BY produce window functions grouped under one window spec
9. `InsideLet_ExpandsAndPreservesBinding`
10. `WithGroupBy_ProducesError`

### End-to-end execution tests

`tests/DatumIngest.Tests/Execution/TransformExecutionTests.cs`:

1. `ZScore_ProducesCorrectValues` — known dataset, verify z-scores against hand-computed expected values
2. `ZScore_ZeroVariance_ReturnsNull` — all identical values → NULL output
3. `ZScore_WithNulls_PropagatesCorrectly` — NULL inputs produce NULL; non-null values are z-scored using non-null statistics
4. `Normalize_ProducesZeroToOneRange` — verify all output values in [0, 1] or NULL
5. `Normalize_AllIdentical_ReturnsNull`
6. `RankPercent_ProducesCorrectPercentiles` — known dataset with ties
7. `RankPercent_SingleRowPartition_ReturnsOne`
8. `Center_SubtractsMean` — verify sum of centered values is ~0
9. `CumulativeSum_ProducesRunningTotal`
10. `CumulativeProduct_ProducesRunningProduct`
11. `LogNormalize_ProducesCorrectValues`
12. `LogNormalize_WithZeros_HandledCorrectly`
13. `RobustZScore_OutlierResistant` — verify that outliers do not dominate the scale
14. `RobustZScore_TwoPassMedian_ComputesCorrectly`
15. `MultipleTransforms_SamePartition_Coalesce` — performance/correctness test
16. `Transform_WithLet_MemoizesResult`
17. `Transform_WithQualify_FiltersOnTransformedValue`
18. `Transform_WholeTable_NoPartitionBy`
19. `Transform_LargeDataset_StreamsCorrectly` — regression test for memory behavior

### Semantic analysis tests

1. `ZScore_OnStringColumn_ProducesDiagnostic`
2. `RankPercent_OnDateColumn_NoDiagnostic`
3. `CumulativeProduct_OnNegativeColumn_ProducesWarning`

---

## Relevant Files

| File | Change | Notes |
|------|--------|-------|
| `src/DatumIngest.Parsing/Ast/AstNodes.cs` | Modify | Add `TransformStrategy` enum and `TransformExpression` record |
| `src/DatumIngest.Parsing/Tokens/SqlToken.cs` | Modify | Add `Transform` keyword token |
| `src/DatumIngest.Parsing/SqlParser.cs` | Modify | Add strategy parser, transform suffix parser, integrate into SELECT column path |
| `src/DatumIngest/Execution/QueryPlanner.cs` | Modify | Add `ExpandTransformExpressions` pass before window rewriting |
| `src/DatumIngest/Execution/ExpressionTypeResolver.cs` | Modify | Add `TransformExpression` case with kind-appropriate return types |
| `src/DatumIngest.LanguageServer/SemanticAnalyzer.cs` | Modify | Diagnostics for type mismatches on TRANSFORM strategies |
| `src/DatumIngest.LanguageServer/CompletionProvider.cs` | Modify | Suggest `TRANSFORM`, strategy names, `OVER` in context |
| `tests/DatumIngest.Tests/Parsing/TransformParsingTests.cs` | **New** | Parsing tests |
| `tests/DatumIngest.Tests/Execution/TransformExpansionTests.cs` | **New** | Planner expansion tests |
| `tests/DatumIngest.Tests/Execution/TransformExecutionTests.cs` | **New** | End-to-end execution tests |
| `docs/sql.md` | Modify | TRANSFORM expression documentation |

---

## Decisions

| Decision | Rationale |
|---|---|
| Planner desugaring, not a new operator | Reuses the battle-tested `WindowOperator` and `ProjectOperator` with LET memoization. No new runtime code paths to maintain. EXPLAIN transparency for free. |
| `TRANSFORM` as reserved keyword | Required to disambiguate from column aliases in expression position. The word "transform" is not a common column name in ML datasets, so the namespace cost is low. |
| Strategy names as contextual identifiers | Avoids reserving 8 additional keywords. Strategy names only have meaning after `TRANSFORM`, so no ambiguity. |
| Hidden LET bindings for intermediate window results | Enables memoization of shared subexpressions (e.g., AVG for both ZSCORE numerator and denominator). Follows the same pattern as tuple destructuring expansion. |
| NULLIF guard on all division denominators | Prevents division-by-zero NULLs or errors on constant-value partitions. Returns NULL instead — semantically correct (z-score is undefined for zero variance). |
| No ORDER BY in OVER clause (except cumulative strategies) | TRANSFORM strategies define their own semantics. ZSCORE/NORMALIZE/CENTER operate on the full partition regardless of order. RANK_PERCENT adds ORDER BY implicitly. Simplifies the grammar. |
| No custom lambda strategy | Lambdas are a separate feature (arrow functions). Keeping TRANSFORM to predefined strategies ensures correctness (safety guards, type checking, diagnostics) and discoverability. A future `TRANSFORM CUSTOM (x, stats) -> ...` could compose the two features. |
| ROBUST_ZSCORE uses two passes | The double-MEDIAN pattern is inherent — MAD requires the median first. The `WindowOperator` already supports multiple window columns, so this is a scheduling decision, not an architectural one. |
| Float32 output for all scaling strategies | Even when the input is an integer column, normalization/z-scoring produces fractional values. Returning Float32 avoids silent truncation. CUMULATIVE_SUM preserves the input kind (integer sums stay integer). |

---

## Future Extensions

These are explicitly out of scope for the initial implementation but are natural extensions:

1. **Custom strategy via lambda**: `salary TRANSFORM ((x, mean, std) -> (x - mean) / std) OVER (...)`. Requires the arrow-function feature.
2. **Multi-column strategies**: `(salary, bonus) TRANSFORM ZSCORE OVER (...)` applying the same normalization to multiple columns at once. Syntactic sugar for N independent TRANSFORM expressions.
3. **Named partition reuse**: `WINDOW w AS (PARTITION BY department)` then `salary TRANSFORM ZSCORE OVER w`. The WINDOW clause already exists in the SQL standard; integrating it with TRANSFORM is a parser change.
4. **Configurable edge-case behavior**: `salary TRANSFORM NORMALIZE OVER (...) ON_CONSTANT 0.5` to return 0.5 instead of NULL for constant partitions. Low priority — NULL is the correct default.
5. **TRANSFORM in IMPUTE**: `IMPUTE salary WITH FORWARD_FILL THEN TRANSFORM ZSCORE OVER (...)` — impute nulls first, then normalize. Requires defining clause ordering between IMPUTE and TRANSFORM.

---

## Verification

1. **Build** — `dotnet build DatumIngest.slnx` succeeds with zero warnings
2. **All existing tests pass** — no regressions from the new keyword or expression type
3. **Parsing tests** — all 10 parsing tests pass, covering valid syntax, error cases, and edge cases
4. **Expansion tests** — all 10 expansion tests verify correct desugaring to window expressions
5. **Execution tests** — all 19 execution tests verify numerical correctness, edge cases, and integration with LET/QUALIFY
6. **Semantic tests** — diagnostics fire for type mismatches
7. **EXPLAIN output** — `EXPLAIN SELECT salary TRANSFORM ZSCORE OVER (PARTITION BY dept) FROM employees` shows the desugared window functions, not a TRANSFORM operator
8. **Language server** — completions, hover, and diagnostics work in the editor (manual verification)
9. **Full suite** — `dotnet test` passes with zero failures
