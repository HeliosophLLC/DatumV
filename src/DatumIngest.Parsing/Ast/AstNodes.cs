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
/// <param name="Query">
/// The inner SELECT statement defining the CTE result set. For recursive CTEs, this is the
/// anchor member (the non-recursive seed query).
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
    SelectStatement Query,
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
/// Represents <c>SELECT *</c> as a column entry.
/// </summary>
public sealed record SelectAllColumns() : SelectColumn(
    new LiteralExpression(null),
    null);

/// <summary>
/// Represents <c>SELECT table.*</c> as a column entry.
/// </summary>
public sealed record SelectTableColumns(string TableName, SourceSpan? Span = null) : SelectColumn(
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
/// A reference to a named table, optionally aliased.
/// </summary>
public sealed record TableReference(string Name, string? Alias = null, SourceSpan? Span = null) : TableSource;

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
/// The GROUP BY clause with one or more grouping expressions.
/// </summary>
public sealed record GroupByClause(IReadOnlyList<Expression> Expressions);

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
