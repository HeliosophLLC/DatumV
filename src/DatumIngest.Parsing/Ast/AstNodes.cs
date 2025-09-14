namespace DatumIngest.Parsing.Ast;

/// <summary>
/// A source-level position span attached to AST nodes that carry names
/// (tables, columns, functions) so that semantic diagnostics can report
/// accurate underline ranges in the editor.
/// </summary>
/// <param name="Line">1-based line number.</param>
/// <param name="Column">1-based column number.</param>
/// <param name="Length">Character length of the token or span.</param>
public sealed record SourceSpan(int Line, int Column, int Length);

/// <summary>
/// A complete SELECT statement, the top-level AST node for the query language.
/// </summary>
public sealed record SelectStatement(
    IReadOnlyList<SelectColumn> Columns,
    FromClause? From = null,
    IntoClause? Into = null,
    IReadOnlyList<JoinClause>? Joins = null,
    Expression? Where = null,
    GroupByClause? GroupBy = null,
    Expression? Having = null,
    Expression? Qualify = null,
    PivotClause? Pivot = null,
    UnpivotClause? Unpivot = null,
    OrderByClause? OrderBy = null,
    int? Limit = null,
    int? Offset = null,
    bool Distinct = false,
    IReadOnlyList<CommonTableExpression>? CommonTableExpressions = null,
    IReadOnlyList<LetBinding>? LetBindings = null);

/// <summary>
/// A named, memoized intermediate expression declared via <c>LET</c> in the SELECT list.
/// Evaluated once per row and cached for all references. Not included in the output
/// unless <see cref="OutputAlias"/> is non-null (set via <c>AS alias</c>).
/// </summary>
/// <param name="Name">The binding name used to reference this expression in subsequent LET bindings and SELECT columns.</param>
/// <param name="Expression">The expression to evaluate and cache once per row.</param>
/// <param name="OutputAlias">When non-null, the binding value is emitted as an output column with this name.</param>
/// <param name="Span">Source location of the binding name for diagnostic reporting.</param>
public sealed record LetBinding(
    string Name,
    Expression Expression,
    string? OutputAlias = null,
    SourceSpan? Span = null);

/// <summary>
/// A single Common Table Expression (CTE) definition within a WITH clause.
/// </summary>
/// <param name="Name">The name used to reference this CTE in subsequent FROM/JOIN clauses.</param>
/// <param name="Body">
/// The full query expression defining the CTE result set. For non-recursive CTEs this may be
/// a <see cref="CompoundQueryExpression"/> (e.g. UNION ALL). For recursive CTEs this contains
/// only the anchor member wrapped as a <see cref="SelectQueryExpression"/>.
/// </param>
/// <param name="RecursiveQuery">
/// The recursive member of a recursive CTE (the SELECT that references the CTE itself),
/// or <see langword="null"/> for non-recursive CTEs. The anchor and recursive member are
/// connected by UNION ALL in the source SQL.
/// </param>
/// <param name="ColumnNames">Optional explicit column list that renames the inner query's output columns.</param>
/// <param name="IsRecursive">Whether this CTE was declared under a WITH RECURSIVE clause.</param>
/// <param name="Hint">Materialization hint controlling whether the CTE result is buffered or re-evaluated.</param>
public sealed record CommonTableExpression(
    string Name,
    QueryExpression Body,
    SelectStatement? RecursiveQuery = null,
    IReadOnlyList<string>? ColumnNames = null,
    bool IsRecursive = false,
    MaterializationHint Hint = MaterializationHint.Default);

/// <summary>
/// Controls how a CTE's result set is materialized during execution.
/// </summary>
public enum MaterializationHint
{
    /// <summary>The planner decides based on reference count (>1 → materialized, 1 → inlined).</summary>
    Default,

    /// <summary>The CTE result is computed once and buffered for all references.</summary>
    Materialized,

    /// <summary>The CTE is inlined as a subquery at each reference site.</summary>
    NotMaterialized,
}

/// <summary>
/// A single column in the SELECT list, representing either a named expression
/// or a wildcard (* or table.*).
/// </summary>
public record SelectColumn(Expression Expression, string? Alias = null);

/// <summary>
/// A column replacement entry in a <c>REPLACE (expr AS name, ...)</c> clause.
/// The <paramref name="ColumnName"/> must match an existing column in the wildcard expansion;
/// the <paramref name="Expression"/> is evaluated in place of the original value.
/// </summary>
/// <param name="Expression">The replacement expression to evaluate.</param>
/// <param name="ColumnName">The column name to replace (must match an existing source column).</param>
public sealed record ColumnReplacement(Expression Expression, string ColumnName);

/// <summary>
/// Represents <c>SELECT *</c>, <c>SELECT * EXCEPT (col1, col2)</c>,
/// or <c>SELECT * REPLACE (expr AS col, ...)</c> as a column entry.
/// </summary>
/// <param name="ExcludedColumns">Column names to exclude from the wildcard expansion, or <see langword="null"/> for an unfiltered <c>SELECT *</c>.</param>
/// <param name="ReplacedColumns">Columns whose values are replaced by expressions, or <see langword="null"/> when no replacements are specified.</param>
public sealed record SelectAllColumns(
    IReadOnlyList<string>? ExcludedColumns = null,
    IReadOnlyList<ColumnReplacement>? ReplacedColumns = null) : SelectColumn(
    new LiteralExpression(null),
    null);

/// <summary>
/// Represents <c>SELECT table.*</c>, <c>SELECT table.* EXCEPT (col1, col2)</c>,
/// or <c>SELECT table.* REPLACE (expr AS col, ...)</c> as a column entry.
/// </summary>
/// <param name="TableName">The table alias or name preceding the dot-star.</param>
/// <param name="Span">Source location span for diagnostic reporting.</param>
/// <param name="ExcludedColumns">Column names to exclude from the table wildcard expansion, or <see langword="null"/> for an unfiltered <c>SELECT table.*</c>.</param>
/// <param name="ReplacedColumns">Columns whose values are replaced by expressions, or <see langword="null"/> when no replacements are specified.</param>
public sealed record SelectTableColumns(
    string TableName,
    SourceSpan? Span = null,
    IReadOnlyList<string>? ExcludedColumns = null,
    IReadOnlyList<ColumnReplacement>? ReplacedColumns = null) : SelectColumn(
    new ColumnReference(TableName, "*"),
    null);

/// <summary>
/// The FROM clause specifying the primary data source.
/// </summary>
public sealed record FromClause(TableSource Source);

/// <summary>
/// Base type for table sources used in FROM and JOIN clauses.
/// </summary>
public abstract record TableSource;

/// <summary>
/// A reference to a named table, optionally aliased, with optional row sampling.
/// </summary>
public sealed record TableReference(
    string Name,
    string? Alias = null,
    SourceSpan? Span = null,
    TablesampleClause? Tablesample = null) : TableSource;

/// <summary>
/// The sampling method used in a TABLESAMPLE clause.
/// </summary>
public enum TablesampleMethod
{
    /// <summary>Row-level Bernoulli sampling — each row is included independently with the given probability.</summary>
    Bernoulli,

    /// <summary>Block-level system sampling — entire chunks/pages are included or excluded.</summary>
    System,
}

/// <summary>
/// A TABLESAMPLE clause that limits the rows returned from a table source
/// to an approximate percentage of the total, using either Bernoulli (row-level)
/// or System (chunk-level) sampling.
/// </summary>
/// <param name="Method">The sampling strategy (Bernoulli or System).</param>
/// <param name="Percentage">An expression evaluating to the sampling percentage (0–100).</param>
/// <param name="Seed">An optional REPEATABLE seed expression for deterministic sampling.</param>
public sealed record TablesampleClause(
    TablesampleMethod Method,
    Expression Percentage,
    Expression? Seed = null);

/// <summary>
/// A subquery used as a table source (derived table), always aliased.
/// </summary>
public sealed record SubquerySource(SelectStatement Query, string Alias) : TableSource;

/// <summary>
/// A table-valued function call used as a table source in FROM or JOIN.
/// </summary>
public sealed record FunctionSource(string FunctionName, IReadOnlyList<Expression> Arguments, string? Alias = null, SourceSpan? Span = null) : TableSource;

/// <summary>
/// A single JOIN clause combining a source with a join condition.
/// When <see cref="IsLateral"/> is <see langword="true"/>, the right-hand source
/// is re-executed per outer row and may reference columns from the left side.
/// </summary>
public sealed record JoinClause(JoinType Type, TableSource Source, Expression? OnCondition, bool IsLateral = false);

/// <summary>
/// The type of join operation.
/// </summary>
public enum JoinType
{
    /// <summary>Inner join -- only matching rows from both sides.</summary>
    Inner,

    /// <summary>Left outer join -- all rows from the left side.</summary>
    Left,

    /// <summary>Right outer join -- all rows from the right side.</summary>
    Right,

    /// <summary>Full outer join -- all rows from both sides.</summary>
    FullOuter,

    /// <summary>Cross join -- cartesian product, no condition.</summary>
    Cross,

    /// <summary>Left semi-join -- rows from the left where a match exists on the right.</summary>
    LeftSemi,

    /// <summary>Left anti-semi-join -- rows from the left where no match exists on the right.</summary>
    LeftAntiSemi,
}

/// <summary>
/// The INTO clause specifying the output format, path, and optional sharding.
/// </summary>
public sealed record IntoClause(
    OutputFormat Format,
    string Path,
    ShardClause? Shard = null);

/// <summary>
/// The target output format for the INTO clause.
/// </summary>
public enum OutputFormat
{
    /// <summary>HDF5 file format.</summary>
    Hdf5,

    /// <summary>Apache Parquet file format.</summary>
    Parquet,

    /// <summary>Comma-separated values text format.</summary>
    Csv,

    /// <summary>DatumIngest native columnar format (.datum).</summary>
    Datum,
}

/// <summary>
/// The SHARD ON sub-clause specifying how output files are split.
/// </summary>
public sealed record ShardClause(ShardMode Mode, long Value);

/// <summary>
/// The sharding mode: split by sample count or byte size.
/// </summary>
public enum ShardMode
{
    /// <summary>Create a new shard every N rows.</summary>
    SampleCount,

    /// <summary>Create a new shard every N bytes.</summary>
    ByteSize,
}

/// <summary>
/// The GROUP BY clause with one or more grouping expressions, or
/// <c>GROUP BY ALL</c> which derives grouping keys from non-aggregate SELECT columns.
/// </summary>
/// <param name="Expressions">
/// The explicit grouping expressions, or an empty list when <paramref name="IsAll"/> is <see langword="true"/>.
/// </param>
/// <param name="IsAll">
/// When <see langword="true"/>, the planner infers grouping keys from the non-aggregate
/// columns in the SELECT list. <paramref name="Expressions"/> is empty in this case.
/// </param>
public sealed record GroupByClause(IReadOnlyList<Expression> Expressions, bool IsAll = false);

/// <summary>
/// The PIVOT clause that rotates distinct values of a column into output columns,
/// aggregating values for each cell. Grouping keys are inferred implicitly by the
/// query planner: all source columns not mentioned as the pivot column or as aggregate
/// arguments become grouping dimensions.
/// </summary>
/// <param name="Aggregates">One or more aggregate function calls (e.g. SUM(amount), COUNT(*)).</param>
/// <param name="PivotColumn">The column whose distinct values become output column names.</param>
/// <param name="ValueList">
/// Explicit list of pivot values for deterministic output schema, or <see langword="null"/>
/// for auto-discovery mode (distinct values determined at runtime, capped at 1000 by default).
/// </param>
/// <param name="Alias">Optional alias for the pivoted result.</param>
public sealed record PivotClause(
    IReadOnlyList<FunctionCallExpression> Aggregates,
    ColumnReference PivotColumn,
    IReadOnlyList<Expression>? ValueList = null,
    string? Alias = null);

/// <summary>
/// The UNPIVOT clause that rotates columns into rows, producing a name–value pair
/// for each source column per input row.
/// </summary>
/// <param name="ValueColumnName">The name of the output column that receives the unpivoted values.</param>
/// <param name="NameColumnName">The name of the output column that receives the source column names.</param>
/// <param name="SourceColumns">The columns to unpivot (listed in the IN clause).</param>
/// <param name="IncludeNulls">
/// When <see langword="true"/>, null values are included in the output.
/// Defaults to <see langword="false"/> (SQL standard: nulls are excluded).
/// </param>
/// <param name="Alias">Optional alias for the unpivoted result.</param>
public sealed record UnpivotClause(
    string ValueColumnName,
    string NameColumnName,
    IReadOnlyList<ColumnReference> SourceColumns,
    bool IncludeNulls = false,
    string? Alias = null);

/// <summary>
/// The ORDER BY clause with one or more sort items.
/// </summary>
public sealed record OrderByClause(IReadOnlyList<OrderByItem> Items);

/// <summary>
/// A single sort criterion within ORDER BY.
/// </summary>
public sealed record OrderByItem(Expression Expression, SortDirection Direction);

/// <summary>
/// Sort direction for ORDER BY items.
/// </summary>
public enum SortDirection
{
    /// <summary>Ascending order (smallest first).</summary>
    Ascending,

    /// <summary>Descending order (largest first).</summary>
    Descending,
}

// ──────────────────────── Expression hierarchy ────────────────────────

/// <summary>
/// Base type for all expression nodes in the AST.
/// </summary>
public abstract record Expression;

/// <summary>
/// A reference to a column, optionally qualified with a table name.
/// </summary>
public sealed record ColumnReference(string? TableName, string ColumnName, SourceSpan? Span = null) : Expression
{
    /// <summary>Pre-computed "TableName.ColumnName" string, built once and cached to avoid per-row interpolation.</summary>
    private string? _qualifiedName;

    /// <summary>Creates an unqualified column reference.</summary>
    public ColumnReference(string columnName) : this(null, columnName)
    {
    }

    /// <summary>
    /// Gets the fully qualified name (TableName.ColumnName) for table-qualified references.
    /// Computed once on first access and cached for the lifetime of this AST node.
    /// Returns <see langword="null"/> for unqualified references.
    /// </summary>
    public string? QualifiedName => TableName is not null
        ? (_qualifiedName ??= string.Concat(TableName, ".", ColumnName))
        : null;
}

/// <summary>
/// A literal value: number, string, null, or boolean.
/// </summary>
public sealed record LiteralExpression(object? Value) : Expression;

/// <summary>
/// A named parameter reference such as <c>$threshold</c>.
/// The <see cref="Name"/> property stores the identifier without the <c>$</c> prefix.
/// Parameter expressions are replaced with <see cref="LiteralExpression"/> nodes
/// by the parameter binder before query planning.
/// </summary>
public sealed record ParameterExpression(string Name, SourceSpan? Span = null) : Expression;

/// <summary>
/// A binary operation between two expressions (arithmetic or comparison).
/// </summary>
public sealed record BinaryExpression(
    Expression Left,
    BinaryOperator Operator,
    Expression Right) : Expression;

/// <summary>
/// Operators for binary expressions.
/// </summary>
public enum BinaryOperator
{
    /// <summary>Addition (+).</summary>
    Add,

    /// <summary>Subtraction (-).</summary>
    Subtract,

    /// <summary>Multiplication (*).</summary>
    Multiply,

    /// <summary>Division (/).</summary>
    Divide,

    /// <summary>Modulo (%).</summary>
    Modulo,

    /// <summary>Exponentiation (^).</summary>
    Power,

    /// <summary>Equality (=).</summary>
    Equal,

    /// <summary>Inequality (!= or &lt;&gt;).</summary>
    NotEqual,

    /// <summary>Less than (&lt;).</summary>
    LessThan,

    /// <summary>Greater than (&gt;).</summary>
    GreaterThan,

    /// <summary>Less than or equal (&lt;=).</summary>
    LessThanOrEqual,

    /// <summary>Greater than or equal (&gt;=).</summary>
    GreaterThanOrEqual,

    /// <summary>Logical AND.</summary>
    And,

    /// <summary>Logical OR.</summary>
    Or,

    /// <summary>Case-sensitive pattern matching (LIKE).</summary>
    Like,

    /// <summary>Case-insensitive pattern matching (ILIKE).</summary>
    ILike,

    /// <summary>Regular expression matching (REGEXP).</summary>
    Regexp,
}

/// <summary>
/// A LIKE or ILIKE pattern match with an explicit ESCAPE character.
/// Produced only when the <c>ESCAPE</c> clause is present; the common case
/// without <c>ESCAPE</c> uses <see cref="BinaryExpression"/> instead.
/// </summary>
public sealed record LikeExpression(
    Expression Expression,
    Expression Pattern,
    Expression EscapeCharacter,
    bool CaseInsensitive) : Expression;

/// <summary>
/// Unary operation (e.g. NOT, unary minus).
/// </summary>
public sealed record UnaryExpression(UnaryOperator Operator, Expression Operand) : Expression;

/// <summary>
/// Operators for unary expressions.
/// </summary>
public enum UnaryOperator
{
    /// <summary>Logical negation (NOT).</summary>
    Not,

    /// <summary>Arithmetic negation (-).</summary>
    Negate,
}

/// <summary>
/// A function call expression with a name and ordered arguments.
/// </summary>
/// <param name="FunctionName">The name of the function being called.</param>
/// <param name="Arguments">The ordered argument expressions.</param>
/// <param name="OrderBy">
/// Optional intra-aggregate ORDER BY items, used by functions like
/// <c>STRING_AGG(expr, separator ORDER BY expr ASC)</c>.
/// </param>
/// <param name="Distinct">Whether the DISTINCT modifier is present.</param>
/// <param name="Span">The source location span of the function call.</param>
public sealed record FunctionCallExpression(
    string FunctionName,
    IReadOnlyList<Expression> Arguments,
    IReadOnlyList<OrderByItem>? OrderBy = null,
    bool Distinct = false,
    SourceSpan? Span = null) : Expression;

/// <summary>
/// The IN predicate: <c>expression IN (value1, value2, ...)</c>.
/// </summary>
public sealed record InExpression(
    Expression Expression,
    IReadOnlyList<Expression> Values,
    bool Negated = false) : Expression;

/// <summary>
/// The BETWEEN predicate: <c>expression BETWEEN low AND high</c>.
/// </summary>
public sealed record BetweenExpression(
    Expression Expression,
    Expression Low,
    Expression High,
    bool Negated = false) : Expression;

/// <summary>
/// The IS NULL / IS NOT NULL predicate.
/// </summary>
public sealed record IsNullExpression(
    Expression Expression,
    bool Negated = false) : Expression;

/// <summary>
/// A subquery used as an expression (correlated or uncorrelated scalar subquery).
/// </summary>
public sealed record SubqueryExpression(SelectStatement Query) : Expression;

/// <summary>
/// The IN subquery predicate: <c>expression [NOT] IN (SELECT ...)</c>.
/// </summary>
public sealed record InSubqueryExpression(
    Expression Expression,
    SelectStatement Query,
    bool Negated = false) : Expression;

/// <summary>
/// The EXISTS predicate: <c>[NOT] EXISTS (SELECT ...)</c>.
/// </summary>
public sealed record ExistsExpression(
    SelectStatement Query,
    bool Negated = false) : Expression;

/// <summary>
/// A CAST expression: <c>CAST(expression AS type)</c>.
/// </summary>
public sealed record CastExpression(Expression Expression, string TargetType, SourceSpan? Span = null) : Expression;

/// <summary>
/// Placeholder expression inserted by the error-recovering parser where
/// unparseable input was skipped. Downstream consumers (semantic analysis,
/// expression evaluation) should treat this as opaque and skip validation.
/// </summary>
public sealed record ErrorExpression(SourceSpan? Span = null) : Expression;

/// <summary>
/// A single WHEN ... THEN branch within a <see cref="CaseExpression"/>.
/// </summary>
/// <param name="Condition">
/// For a simple CASE (<c>CASE operand WHEN value ...</c>), the value to compare against the operand.
/// For a searched CASE (<c>CASE WHEN condition ...</c>), the boolean condition to evaluate.
/// </param>
/// <param name="Result">The expression to return when this branch matches.</param>
public sealed record WhenClause(Expression Condition, Expression Result);

/// <summary>
/// A SQL CASE expression supporting both simple and searched forms.
/// <para>
/// Simple form: <c>CASE operand WHEN value1 THEN result1 ... [ELSE default] END</c> —
/// <see cref="Operand"/> is present and each <see cref="WhenClause.Condition"/> is compared
/// against it for equality.
/// </para>
/// <para>
/// Searched form: <c>CASE WHEN condition1 THEN result1 ... [ELSE default] END</c> —
/// <see cref="Operand"/> is <see langword="null"/> and each <see cref="WhenClause.Condition"/>
/// is evaluated as a boolean predicate.
/// </para>
/// </summary>
public sealed record CaseExpression(
    Expression? Operand,
    IReadOnlyList<WhenClause> WhenClauses,
    Expression? ElseResult,
    SourceSpan? Span = null) : Expression;

// ──────────────────────── Window function expressions ────────────────────────

/// <summary>
/// A function call with an OVER clause, making it a window function invocation.
/// <para>
/// Examples: <c>ROW_NUMBER() OVER (PARTITION BY dept ORDER BY salary DESC)</c>,
/// <c>SUM(amount) OVER (ORDER BY date ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)</c>.
/// </para>
/// </summary>
public sealed record WindowFunctionCallExpression(
    string FunctionName,
    IReadOnlyList<Expression> Arguments,
    WindowSpecification Window,
    bool Distinct = false,
    NullHandling NullHandling = NullHandling.RespectNulls,
    bool FromLast = false,
    SourceSpan? Span = null) : Expression;

/// <summary>
/// The OVER clause specification containing optional partitioning, ordering, and frame.
/// </summary>
/// <param name="PartitionBy">
/// The PARTITION BY expressions that define window partitions, or <see langword="null"/>
/// if the entire result set is a single partition.
/// </param>
/// <param name="OrderBy">
/// The ORDER BY items that define the row ordering within each partition, or
/// <see langword="null"/> if no ordering is specified.
/// </param>
/// <param name="Frame">
/// The window frame specification (e.g. ROWS BETWEEN ...), or <see langword="null"/>
/// for the default frame semantics.
/// </param>
public sealed record WindowSpecification(
    IReadOnlyList<Expression>? PartitionBy = null,
    IReadOnlyList<OrderByItem>? OrderBy = null,
    WindowFrame? Frame = null);

/// <summary>
/// A window frame specification defining the subset of partition rows visible
/// to a window function for each row.
/// </summary>
/// <param name="FrameType">The frame unit type (currently only <see cref="WindowFrameType.Rows"/>).</param>
/// <param name="Start">The lower bound of the window frame.</param>
/// <param name="End">The upper bound of the window frame.</param>
public sealed record WindowFrame(
    WindowFrameType FrameType,
    FrameBound Start,
    FrameBound End);

/// <summary>
/// The unit type for a window frame specification.
/// </summary>
public enum WindowFrameType
{
    /// <summary>Frame boundaries are expressed as row offsets from the current row.</summary>
    Rows,
}

/// <summary>
/// Controls whether NULL values are skipped or included by value window
/// functions (FIRST_VALUE, LAST_VALUE, NTH_VALUE).
/// </summary>
public enum NullHandling
{
    /// <summary>Include NULL values (the default SQL behavior).</summary>
    RespectNulls,

    /// <summary>Skip NULL values when searching for the target row.</summary>
    IgnoreNulls,
}

/// <summary>
/// Base type for window frame boundary specifications.
/// </summary>
public abstract record FrameBound;

/// <summary>UNBOUNDED PRECEDING — the frame starts at the first row of the partition.</summary>
public sealed record UnboundedPrecedingBound() : FrameBound;

/// <summary>N PRECEDING — the frame boundary is N rows before the current row.</summary>
public sealed record PrecedingBound(int Offset) : FrameBound;

/// <summary>CURRENT ROW — the frame boundary is the current row.</summary>
public sealed record CurrentRowBound() : FrameBound;

/// <summary>N FOLLOWING — the frame boundary is N rows after the current row.</summary>
public sealed record FollowingBound(int Offset) : FrameBound;

/// <summary>UNBOUNDED FOLLOWING — the frame extends to the last row of the partition.</summary>
public sealed record UnboundedFollowingBound() : FrameBound;

// ──────────────────────── Set operations ────────────────────────

/// <summary>
/// The type of set operation combining two query results.
/// </summary>
public enum SetOperationType
{
    /// <summary>Combines results from both queries, optionally removing duplicates.</summary>
    Union,

    /// <summary>Returns only rows present in both query results.</summary>
    Intersect,

    /// <summary>Returns rows from the left query that are not in the right query.</summary>
    Except,
}

/// <summary>
/// Base type for top-level query expressions. A query expression is either
/// a single <see cref="SelectQueryExpression"/> or a compound
/// <see cref="CompoundQueryExpression"/> combining two query expressions
/// with a set operation (UNION, INTERSECT, EXCEPT).
/// </summary>
public abstract record QueryExpression;

/// <summary>
/// A query expression wrapping a single <see cref="SelectStatement"/>.
/// </summary>
public sealed record SelectQueryExpression(SelectStatement Statement) : QueryExpression;

/// <summary>
/// A compound query expression combining two sub-expressions with a set operation.
/// <para>
/// ORDER BY, LIMIT, OFFSET, and INTO apply to the final combined result
/// and live on the outermost compound node only.
/// </para>
/// </summary>
/// <param name="Left">The left-hand query expression.</param>
/// <param name="OperationType">The set operation (UNION, INTERSECT, EXCEPT).</param>
/// <param name="All">
/// When <see langword="true"/>, duplicates are preserved (e.g. UNION ALL).
/// When <see langword="false"/>, duplicates are removed (e.g. UNION DISTINCT).
/// </param>
/// <param name="Right">The right-hand query expression.</param>
/// <param name="OrderBy">Optional ORDER BY applied to the combined result.</param>
/// <param name="Limit">Optional LIMIT applied to the combined result.</param>
/// <param name="Offset">Optional OFFSET applied to the combined result.</param>
/// <param name="Into">Optional INTO clause for output of the combined result.</param>
public sealed record CompoundQueryExpression(
    QueryExpression Left,
    SetOperationType OperationType,
    bool All,
    QueryExpression Right,
    OrderByClause? OrderBy = null,
    int? Limit = null,
    int? Offset = null,
    IntoClause? Into = null) : QueryExpression;

// ───────────────────── Statement hierarchy ─────────────────────

/// <summary>
/// Base type for all executable statements. A statement is either a query
/// (<see cref="QueryStatement"/>) or a DDL/DML command.
/// </summary>
public abstract record Statement;

/// <summary>
/// A statement that executes a query expression and returns rows.
/// </summary>
/// <param name="Query">The query expression to execute.</param>
public sealed record QueryStatement(QueryExpression Query) : Statement;

/// <summary>
/// <c>CREATE TEMP TABLE name (col type, ...)</c> — creates a temporary table
/// <c>CREATE TEMP TABLE name (col type, ..., [PRIMARY KEY (col, ...)])</c> — creates a temporary table
/// with an explicit column definition list.
/// </summary>
/// <param name="TableName">The name of the temporary table to create.</param>
/// <param name="Columns">The column definitions (name and type pairs).</param>
/// <param name="IfNotExists">When <see langword="true"/>, suppresses errors if the table already exists.</param>
/// <param name="PrimaryKeyColumns">
/// Column names that form the primary key. Populated from either inline <c>PRIMARY KEY</c>
/// annotations on individual columns or a table-level <c>PRIMARY KEY (col, ...)</c> clause.
/// Empty when no primary key is declared.
/// </param>
public sealed record CreateTempTableStatement(
    string TableName,
    IReadOnlyList<ColumnDefinition> Columns,
    bool IfNotExists = false,
    IReadOnlyList<string>? PrimaryKeyColumns = null) : Statement;

/// <summary>
/// A single column definition within a <c>CREATE TABLE</c> statement.
/// </summary>
/// <param name="Name">The column name.</param>
/// <param name="TypeName">The SQL type name (resolved to a <c>DataKind</c> at execution time).</param>
/// <param name="Nullable">Whether the column accepts NULL values. Defaults to <see langword="true"/>.</param>
/// <param name="PrimaryKey">Whether this column is part of the primary key. Defaults to <see langword="false"/>.</param>
public sealed record ColumnDefinition(string Name, string TypeName, bool Nullable = true, bool PrimaryKey = false);

/// <summary>
/// <c>CREATE TEMP TABLE name AS SELECT ...</c> — creates a temporary table
/// populated from a query.
/// </summary>
/// <param name="TableName">The name of the temporary table to create.</param>
/// <param name="Query">The query whose results populate the table.</param>
/// <param name="IfNotExists">When <see langword="true"/>, suppresses errors if the table already exists.</param>
public sealed record CreateTempTableAsSelectStatement(
    string TableName,
    QueryExpression Query,
    bool IfNotExists = false) : Statement;

/// <summary>
/// <c>DROP TABLE [IF EXISTS] name</c> — removes a temporary table.
/// </summary>
/// <param name="TableName">The name of the table to drop.</param>
/// <param name="IfExists">When <see langword="true"/>, suppresses errors if the table does not exist.</param>
public sealed record DropTableStatement(
    string TableName,
    bool IfExists = false) : Statement;

/// <summary>
/// <c>INSERT INTO name SELECT ...</c> — inserts rows from a query into an existing table.
/// </summary>
/// <param name="TableName">The target table name.</param>
/// <param name="ColumnNames">Optional explicit column list. When <see langword="null"/>, all columns in declaration order.</param>
/// <param name="Source">The source of rows: either a <see cref="QueryExpression"/> or an <see cref="InsertValuesSource"/>.</param>
public sealed record InsertStatement(
    string TableName,
    IReadOnlyList<string>? ColumnNames,
    InsertSource Source) : Statement;

/// <summary>
/// Base type for the source of rows in an INSERT statement.
/// </summary>
public abstract record InsertSource;

/// <summary>
/// Rows sourced from a query expression (<c>INSERT INTO t SELECT ...</c>).
/// </summary>
/// <param name="Query">The query providing the rows.</param>
public sealed record InsertQuerySource(QueryExpression Query) : InsertSource;

/// <summary>
/// Rows sourced from literal VALUES clauses (<c>INSERT INTO t VALUES (1, 'a'), (2, 'b')</c>).
/// </summary>
/// <param name="Rows">The literal row values.</param>
public sealed record InsertValuesSource(IReadOnlyList<IReadOnlyList<Expression>> Rows) : InsertSource;

/// <summary>
/// <c>UPDATE name [alias] SET col = expr [, ...] [FROM source [JOIN ...]*] [WHERE ...]</c> — updates rows in a table.
/// Follows PostgreSQL semantics: SET column names are unqualified; the target table is not
/// repeated in the FROM clause; the WHERE clause contains both join conditions and filters.
/// </summary>
/// <param name="TableName">The target table name.</param>
/// <param name="Alias">
/// Optional alias for the target table. When set, the rest of the statement must use the alias
/// to reference the target's columns (e.g. <c>UPDATE features f SET score = s.v FROM scores s WHERE f.id = s.id</c>).
/// </param>
/// <param name="Assignments">The column assignment list. Column names are unqualified.</param>
/// <param name="From">
/// Optional FROM clause listing source tables used in SET expressions or join conditions.
/// The target table must not appear here.
/// </param>
/// <param name="Joins">Optional JOIN clauses between source tables listed in the FROM clause.</param>
/// <param name="Where">Optional filter and join-condition predicate.</param>
public sealed record UpdateStatement(
    string TableName,
    string? Alias,
    IReadOnlyList<ColumnAssignment> Assignments,
    FromClause? From = null,
    IReadOnlyList<JoinClause>? Joins = null,
    Expression? Where = null) : Statement;

/// <summary>
/// <c>DELETE FROM name [WHERE ...]</c> — deletes rows from a table using tombstone bitmaps.
/// </summary>
/// <param name="TableName">The target table name.</param>
/// <param name="Where">Optional filter predicate restricting which rows are deleted. When omitted, all rows are deleted.</param>
public sealed record DeleteStatement(
    string TableName,
    Expression? Where = null) : Statement;

/// <summary>
/// A single <c>column = expression</c> assignment in an UPDATE SET clause.
/// </summary>
/// <param name="ColumnName">The column being assigned.</param>
/// <param name="Value">The expression to evaluate for the new value.</param>
public sealed record ColumnAssignment(string ColumnName, Expression Value);

/// <summary>
/// <c>ALTER TABLE name ADD [COLUMN] col type [NOT NULL] [DEFAULT expr | AS expr]</c> — adds a column to a table.
/// When <see cref="ComputedExpression"/> is set, the column value is computed from existing columns
/// and persisted for every row. <see cref="DefaultValue"/> and <see cref="ComputedExpression"/> are mutually exclusive.
/// </summary>
/// <param name="TableName">The target table name.</param>
/// <param name="ColumnName">The name of the column to add.</param>
/// <param name="TypeName">The SQL type name of the new column.</param>
/// <param name="DefaultValue">Optional default value expression for existing rows.</param>
/// <param name="Nullable">Whether the new column accepts NULL values.</param>
/// <param name="ComputedExpression">Optional expression evaluated per row from existing columns.</param>
public sealed record AlterTableAddColumnStatement(
    string TableName,
    string ColumnName,
    string TypeName,
    Expression? DefaultValue = null,
    bool Nullable = true,
    Expression? ComputedExpression = null) : Statement;

/// <summary>
/// <c>ANALYZE table</c> — rebuilds statistics and indexes for the specified table.
/// </summary>
/// <param name="TableName">The target table name.</param>
public sealed record AnalyzeTableStatement(string TableName) : Statement;
