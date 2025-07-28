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
    FromClause From,
    IntoClause? Into = null,
    IReadOnlyList<JoinClause>? Joins = null,
    Expression? Where = null,
    GroupByClause? GroupBy = null,
    Expression? Having = null,
    OrderByClause? OrderBy = null,
    int? Limit = null,
    int? Offset = null);

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
/// </summary>
public sealed record JoinClause(JoinType Type, TableSource Source, Expression? OnCondition);

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

    /// <summary>Pattern matching (LIKE).</summary>
    Like,
}

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
public sealed record FunctionCallExpression(
    string FunctionName,
    IReadOnlyList<Expression> Arguments,
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
