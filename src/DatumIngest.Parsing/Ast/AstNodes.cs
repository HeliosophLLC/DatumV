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
    IReadOnlyList<AssertClause>? Assertions = null,
    PivotClause? Pivot = null,
    UnpivotClause? Unpivot = null,
    OrderByClause? OrderBy = null,
    Expression? Limit = null,
    Expression? Offset = null,
    bool Distinct = false,
    IReadOnlyList<CommonTableExpression>? CommonTableExpressions = null,
    IReadOnlyList<LetBinding>? LetBindings = null,
    CrossValidateClause? CrossValidate = null);

/// <summary>
/// Specifies how a destructured LET binding extracts values from its right-hand-side expression.
/// </summary>
public enum DestructureMode
{
    /// <summary>Extract by zero-based ordinal position. Applies to Vector, Array, and Struct.</summary>
    Positional,
    /// <summary>Extract by field name. Applies to Struct only.</summary>
    Named,
}

/// <summary>
/// Represents the left-hand side of a destructured LET binding: a list of names and the
/// extraction mode. Carried on the AST until the planner desugars it into individual plain
/// bindings backed by a single hidden memoizing binding.
/// </summary>
/// <param name="Names">The names to extract. Must contain at least two elements.</param>
/// <param name="Mode">Whether extraction is positional (index-based) or named (field-based).</param>
/// <param name="Span">Source location for diagnostic reporting.</param>
public sealed record DestructurePattern(
    IReadOnlyList<string> Names,
    DestructureMode Mode,
    SourceSpan? Span = null);

/// <summary>
/// A named, memoized intermediate expression declared via <c>LET</c> in the SELECT list.
/// Evaluated once per row and cached for all references. Not included in the output
/// unless <see cref="OutputAlias"/> is non-null (set via <c>AS alias</c>).
/// </summary>
/// <param name="Name">The binding name used to reference this expression in subsequent LET bindings and SELECT columns.</param>
/// <param name="Expression">The expression to evaluate and cache once per row.</param>
/// <param name="OutputAlias">When non-null, the binding value is emitted as an output column with this name.</param>
/// <param name="Span">Source location of the binding name for diagnostic reporting.</param>
/// <param name="Destructure">
/// When non-null, this binding is a destructuring pattern. <see cref="Name"/> is a placeholder;
/// the planner will expand this into one hidden memoizing binding plus one plain binding per
/// extracted name before any rewriting passes run.
/// </param>
public sealed record LetBinding(
    string Name,
    Expression Expression,
    string? OutputAlias = null,
    SourceSpan? Span = null,
    DestructurePattern? Destructure = null);

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
/// <para>
/// <see cref="AssignedVariableName"/> is non-<see langword="null"/> when this
/// column is a procedural-variable assignment of the form
/// <c>name := expression</c> at the top level of a SELECT list. The
/// <c>:=</c> operator is the PG-native PL/pgSQL assignment marker (PG's
/// SELECT lists don't carry assignments natively, but this is the
/// closest-to-PG syntax for the procedural batch executor's needs). The
/// RHS becomes <see cref="Expression"/>; the bare variable name lands on
/// <see cref="AssignedVariableName"/>. Booleans of the form <c>name = 1</c>
/// continue to be plain projections.
/// </para>
/// </summary>
public record SelectColumn(
    Expression Expression,
    string? Alias = null,
    string? AssignedVariableName = null);

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
/// <param name="QualifyOutput">
/// When <see langword="true"/>, the table alias qualifier is preserved in output column names
/// (e.g. <c>t.id</c> instead of <c>id</c>). Used by the query planner when expanding
/// <c>SELECT *</c> in multi-join contexts where qualified names are required for disambiguation.
/// User-written <c>SELECT t.*</c> leaves this <see langword="false"/> so output names are unqualified.
/// </param>
public sealed record SelectTableColumns(
    string TableName,
    SourceSpan? Span = null,
    IReadOnlyList<string>? ExcludedColumns = null,
    IReadOnlyList<ColumnReplacement>? ReplacedColumns = null,
    bool QualifyOutput = false) : SelectColumn(
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
/// A reference to a named table, optionally schema-qualified (e.g. <c>information_schema.tables</c>),
/// optionally aliased, with optional row sampling.
/// </summary>
public sealed record TableReference(
    string Name,
    string? Alias = null,
    SourceSpan? Span = null,
    TablesampleClause? Tablesample = null,
    string? SchemaName = null) : TableSource
{
    
}

/// <summary>
/// The sampling method used in a TABLESAMPLE clause.
/// </summary>
public enum TablesampleMethod
{
    /// <summary>Row-level Bernoulli sampling — each row is included independently with the given probability.</summary>
    Bernoulli,

    /// <summary>Block-level system sampling — entire chunks/pages are included or excluded.</summary>
    System,

    /// <summary>Stratified sampling — uniform Bernoulli at the given rate within each class, preserving class proportions.</summary>
    Stratified,

    /// <summary>Balanced sampling — fixed-count reservoir sampling per class, equalizing class representation.</summary>
    Balanced,
}

/// <summary>
/// A TABLESAMPLE clause that limits the rows returned from a table source
/// to an approximate percentage of the total. For Bernoulli/System, each row is
/// independently included with the given probability. For Stratified/Balanced,
/// rows are sampled per class defined by the <paramref name="StratifyColumns"/>.
/// </summary>
/// <param name="Method">The sampling strategy.</param>
/// <param name="Percentage">
/// An expression evaluating to the sampling percentage (0–100) for Bernoulli/System/Stratified,
/// or the per-class row count for Balanced.
/// </param>
/// <param name="Seed">An optional REPEATABLE seed expression for deterministic sampling.</param>
/// <param name="StratifyColumns">
/// The columns defining the stratification key, specified via the <c>ON</c> clause.
/// Required for Stratified/Balanced; must be null for Bernoulli/System.
/// </param>
public sealed record TablesampleClause(
    TablesampleMethod Method,
    Expression Percentage,
    Expression? Seed = null,
    IReadOnlyList<ColumnReference>? StratifyColumns = null);

/// <summary>
/// A subquery used as a table source (derived table), always aliased.
/// </summary>
public sealed record SubquerySource(SelectStatement Query, string Alias) : TableSource;

/// <summary>
/// A table-valued function call used as a table source in FROM or JOIN.
/// </summary>
public sealed record FunctionSource(
    string FunctionName,
    IReadOnlyList<Expression> Arguments,
    string? Alias = null,
    SourceSpan? Span = null,
    string? SchemaName = null) : TableSource
{
    /// <summary>Flat-string call name; see <see cref="FunctionCallExpression.CallName"/>.</summary>
    public string CallName => SchemaName is null ? FunctionName : $"{SchemaName}.{FunctionName}";
}

/// <summary>
/// A CROSS VALIDATE clause that assigns deterministic fold indices to rows
/// for k-fold cross-validation. Desugars to a synthetic LET binding at plan time:
/// <c>CAST(FLOOR(hash_split(key, seed) * k) AS Int32)</c>.
/// </summary>
/// <param name="FoldCount">The number of folds (k). Must be an integer >= 2.</param>
/// <param name="Seed">Optional seed for deterministic fold assignment. Defaults to 0.</param>
/// <param name="KeyColumns">The column(s) used as the hash key for fold assignment.</param>
/// <param name="StratifyColumns">Optional STRATIFY BY columns for class-balanced folds.</param>
/// <param name="GroupColumns">Optional GROUP BY columns — all rows with the same group key get the same fold.</param>
/// <param name="OutputAlias">The output column name for the fold index.</param>
public sealed record CrossValidateClause(
    Expression FoldCount,
    Expression? Seed,
    IReadOnlyList<Expression> KeyColumns,
    IReadOnlyList<Expression>? StratifyColumns,
    IReadOnlyList<Expression>? GroupColumns,
    string OutputAlias);

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
public abstract record Expression
{
    /// <summary>
    /// Returns the source span for this expression if available, either directly
    /// from the node itself or from the nearest child that carries one.
    /// </summary>
    public SourceSpan? TryGetSourceSpan()
    {
        return this switch
        {
            ColumnReference e => e.Span,
            FunctionCallExpression e => e.Span,
            CastExpression e => e.Span,
            CaseExpression e => e.Span,
            AtTimeZoneExpression e => e.Span,
            LambdaExpression e => e.Span,
            WindowFunctionCallExpression e => e.Span,
            ErrorExpression e => e.Span,
            TypeLiteralExpression e => e.Span,
            ParameterExpression e => e.Span,
            StructLiteralExpression e => e.Span,
            IndexAccessExpression e => e.Span,
            ScanExpression e => e.Span,
            // Nodes without their own span — try the nearest child.
            BinaryExpression e => e.Left.TryGetSourceSpan() ?? e.Right.TryGetSourceSpan(),
            UnaryExpression e => e.Operand.TryGetSourceSpan(),
            InExpression e => e.Expression.TryGetSourceSpan(),
            BetweenExpression e => e.Expression.TryGetSourceSpan(),
            IsNullExpression e => e.Expression.TryGetSourceSpan(),
            LikeExpression e => e.Expression.TryGetSourceSpan(),
            _ => null,
        };
    }
}

/// <summary>
/// A reference to a column, optionally qualified with a table name and
/// optionally further qualified with a schema name (PG-style
/// <c>schema.table.column</c>).
/// </summary>
/// <param name="TableName">Optional table qualifier (the table portion of <c>table.col</c> or <c>schema.table.col</c>).</param>
/// <param name="ColumnName">The column name (or <c>*</c> in <c>SELECT t.*</c>).</param>
/// <param name="Span">Source span for diagnostics.</param>
/// <param name="SchemaName">
/// Optional schema qualifier from <c>schema.table.column</c>. <see langword="null"/>
/// for unqualified or two-part references. Only meaningful when
/// <paramref name="TableName"/> is also non-null.
/// </param>
public sealed record ColumnReference(string? TableName, string ColumnName, SourceSpan? Span = null, string? SchemaName = null) : Expression
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
/// Sentinel for the <c>DEFAULT</c> keyword in an <c>INSERT … VALUES</c>
/// row, e.g. <c>INSERT INTO t (id, status) VALUES (1, DEFAULT)</c>. At
/// execute time the slot is resolved by the same path that handles an
/// omitted column: <c>IDENTITY</c> counter → column's <c>DEFAULT</c>
/// expression → <c>NULL</c> (if Nullable) → error.
/// </summary>
public sealed record DefaultValueExpression(SourceSpan? Span = null) : Expression;

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
/// <c>STRING_AGG(expr, separator ORDER BY expr ASC)</c> (the inline form
/// before the closing paren).
/// </param>
/// <param name="WithinGroupOrderBy">
/// Optional <c>WITHIN GROUP (ORDER BY …)</c> ORDER BY items. Mutually
/// exclusive with <see cref="OrderBy"/> — the parser populates one or
/// the other depending on which syntactic form was used. The planner
/// consults the aggregate's
/// <c>IAggregateFunction.WithinGroupSemantics</c> to decide whether
/// these expressions become aggregate <em>data</em> (ordered-set
/// aggregates) or just a row sort modifier (e.g. <c>STRING_AGG</c>).
/// </param>
/// <param name="Distinct">Whether the DISTINCT modifier is present.</param>
/// <param name="Span">The source location span of the function call.</param>
/// <param name="SchemaName">
/// Optional schema qualifier from <c>schema.fn(...)</c>;
/// <see langword="null"/> for an unqualified call. Resolution walks the
/// session search_path when null. See <see cref="CallName"/> for the flat
/// string form used by back-compat lookups.
/// </param>
public sealed record FunctionCallExpression(
    string FunctionName,
    IReadOnlyList<Expression> Arguments,
    IReadOnlyList<OrderByItem>? OrderBy = null,
    bool Distinct = false,
    SourceSpan? Span = null,
    IReadOnlyList<OrderByItem>? WithinGroupOrderBy = null,
    string? SchemaName = null) : Expression
{
    /// <summary>
    /// Flat-string form of the call name, <c>"schema.fn"</c> when
    /// <see cref="SchemaName"/> is set or just <c>FunctionName</c>
    /// otherwise. Bridges call sites still using the pre-S7b string
    /// dispatch.
    /// </summary>
    public string CallName => SchemaName is null ? FunctionName : $"{SchemaName}.{FunctionName}";
}

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
/// A type literal expression: a bare type name (e.g. <c>Int32</c>, <c>Float64</c>) used
/// in expression position. Produces a <c>DataKind.Type</c> value for comparison
/// with <c>typeof()</c> results: <c>typeof(x) == Int32</c>.
/// </summary>
public sealed record TypeLiteralExpression(string TypeName, SourceSpan? Span = null) : Expression;

/// <summary>
/// An <c>AT TIME ZONE</c> expression: <c>expr AT TIME ZONE timezone</c>.
/// Converts a DateTime value to the specified timezone, preserving the instant
/// but adjusting the UTC offset to match the target zone (including DST).
/// </summary>
public sealed record AtTimeZoneExpression(
    Expression Expression,
    Expression TimeZone,
    SourceSpan? Span = null) : Expression;

/// <summary>
/// The kind of temporal constant represented by a <see cref="CurrentTimestampExpression"/>.
/// </summary>
public enum CurrentTimestampKind
{
    /// <summary><c>CURRENT_DATE</c> — returns Date.</summary>
    CurrentDate,

    /// <summary><c>CURRENT_TIME</c> / <c>LOCALTIME</c> — returns Time.</summary>
    CurrentTime,

    /// <summary><c>CURRENT_TIMESTAMP</c> / <c>LOCALTIMESTAMP</c> — returns DateTime.</summary>
    CurrentTimestamp,
}

/// <summary>
/// A PostgreSQL-compatible temporal constant expression: <c>CURRENT_DATE</c>,
/// <c>CURRENT_TIME[(p)]</c>, <c>CURRENT_TIMESTAMP[(p)]</c>, <c>LOCALTIME[(p)]</c>,
/// or <c>LOCALTIMESTAMP[(p)]</c>. These resolve to a single constant value at plan time
/// (the batch/transaction start time), not at per-row evaluation time.
/// </summary>
/// <param name="Kind">Which temporal constant this represents.</param>
/// <param name="Precision">Optional fractional-second precision (0–6). Null means full precision.</param>
public sealed record CurrentTimestampExpression(
    CurrentTimestampKind Kind,
    int? Precision = null) : Expression;

/// <summary>
/// Placeholder expression inserted by the error-recovering parser where
/// unparseable input was skipped. Downstream consumers (semantic analysis,
/// expression evaluation) should treat this as opaque and skip validation.
/// </summary>
public sealed record ErrorExpression(SourceSpan? Span = null) : Expression;

/// <summary>
/// A lambda (arrow function) expression: <c>x -&gt; x * 2</c> or <c>(a, b) -&gt; a + b</c>.
/// Used as arguments to higher-order functions such as <c>array_transform</c> and <c>array_filter</c>.
/// Lambda expressions are not first-class values — they cannot appear in projections,
/// GROUP BY, ORDER BY, or any context that requires a concrete data kind.
/// </summary>
/// <param name="Parameters">The lambda parameter names (1 or 2).</param>
/// <param name="Body">The expression to evaluate for each invocation.</param>
/// <param name="Span">Source location of the arrow token for diagnostic reporting.</param>
public sealed record LambdaExpression(
    IReadOnlyList<string> Parameters,
    Expression Body,
    SourceSpan? Span = null) : Expression;

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
    SourceSpan? Span = null,
    string? SchemaName = null) : Expression
{
    /// <summary>Flat-string call name; see <see cref="FunctionCallExpression.CallName"/>.</summary>
    public string CallName => SchemaName is null ? FunctionName : $"{SchemaName}.{FunctionName}";
}

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

// ──────────────────────── SCAN (fold/prefix-scan) expressions ────────────────────────

/// <summary>
/// A SCAN fold/prefix-scan expression that computes a running accumulator
/// over ordered partitions. Each row's output feeds back as input to the
/// next row's computation: <c>output[i] = f(output[i-1], input[i])</c>.
/// <para>
/// Scalar form: <c>SCAN acc = expr INIT seed OVER (...) AS alias</c>.
/// Tuple form: <c>SCAN (a, b) = (e1, e2) INIT (v1, v2) OVER (...) AS (a1, a2)</c>.
/// </para>
/// </summary>
/// <param name="AccumulatorNames">
/// The accumulator variable names (one for scalar, multiple for tuple form).
/// These names are visible inside <paramref name="BodyExpressions"/> only.
/// </param>
/// <param name="BodyExpressions">
/// The fold body expressions, one per accumulator. May reference accumulator
/// names and <c>PREV(col)</c> pseudo-function calls.
/// </param>
/// <param name="InitExpressions">
/// The seed values for each accumulator at partition start.
/// </param>
/// <param name="Window">The OVER clause specifying partitioning and ordering.</param>
/// <param name="OutputAliases">
/// The output column aliases (one per accumulator). These are the only
/// names visible outside the SCAN expression.
/// </param>
/// <param name="Span">Source location for diagnostics.</param>
public sealed record ScanExpression(
    IReadOnlyList<string> AccumulatorNames,
    IReadOnlyList<Expression> BodyExpressions,
    IReadOnlyList<Expression> InitExpressions,
    WindowSpecification Window,
    IReadOnlyList<string> OutputAliases,
    SourceSpan? Span = null) : Expression;

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
/// A query expression that wraps a data-modifying <see cref="InsertStatement"/>
/// with a <c>RETURNING</c> clause, surfacing it as a row source for outer
/// constructs (<c>WITH cte AS (INSERT … RETURNING …) SELECT … FROM cte</c>).
/// Mirrors PostgreSQL's data-modifying CTEs.
/// </summary>
/// <remarks>
/// <para>
/// The wrapped <see cref="InsertStatement"/> must carry a non-null
/// <see cref="InsertStatement.Returning"/>; an INSERT without RETURNING
/// has no rows to project and is rejected at plan time. The INSERT's side
/// effect runs at plan time, exactly once per containing query plan,
/// regardless of how many times the CTE is referenced.
/// </para>
/// </remarks>
/// <param name="Insert">The wrapped INSERT statement.</param>
public sealed record InsertQueryExpression(InsertStatement Insert) : QueryExpression;

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
    Expression? Limit = null,
    Expression? Offset = null,
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
/// <c>CREATE [TEMP] TABLE name (col type, ..., [PRIMARY KEY (col, ...)]) [AT 'path']</c>
/// — creates a table with an explicit column definition list. The
/// <see cref="IsTemp"/> flag selects between an in-memory table (TEMP)
/// and a persistent <c>.datum</c>-file-backed one.
/// </summary>
/// <param name="TableName">The name of the table to create.</param>
/// <param name="Columns">The column definitions (name and type pairs).</param>
/// <param name="IsTemp">
/// When <see langword="true"/>, the table is created as an in-memory
/// temp table that lives only for the catalog's lifetime. When
/// <see langword="false"/>, the table is materialised as a new
/// <c>.datum</c> file on disk and persisted in the catalog file.
/// </param>
/// <param name="IfNotExists">When <see langword="true"/>, suppresses errors if the table already exists.</param>
/// <param name="PrimaryKeyColumns">
/// Column names that form the primary key. Populated from either inline <c>PRIMARY KEY</c>
/// annotations on individual columns or a table-level <c>PRIMARY KEY (col, ...)</c> clause.
/// Empty when no primary key is declared.
/// </param>
/// <param name="StoragePath">
/// Optional explicit path supplied via the <c>AT 'path'</c> clause. When
/// <see langword="null"/>, persistent tables land at
/// <c>{catalog_dir}/{TableName}.datum</c>. The catalog gates this clause
/// behind an <c>AllowExplicitTablePaths</c> flag so production hosts can
/// disable it; tests opt in. Always <see langword="null"/> for TEMP tables.
/// </param>
/// <param name="PrimaryKeyConstraintName">
/// Optional user-supplied PRIMARY KEY constraint name from a
/// <c>CONSTRAINT name PRIMARY KEY …</c> clause (column-level or
/// table-level). When <see langword="null"/>, the catalog derives the
/// PG-canonical default <c>&lt;table&gt;_pkey</c>. Persisted in
/// <c>.datum-catalog.json</c> for persistent tables; not stored on
/// disk for temp tables.
/// </param>
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>; <see langword="null"/> means the default schema.</param>
public sealed record CreateTableStatement(
    string TableName,
    IReadOnlyList<ColumnDefinition> Columns,
    bool IsTemp = false,
    bool IfNotExists = false,
    IReadOnlyList<string>? PrimaryKeyColumns = null,
    string? StoragePath = null,
    string? PrimaryKeyConstraintName = null,
    string? SchemaName = null) : Statement;

/// <summary>
/// A single column definition within a <c>CREATE TABLE</c> statement.
/// </summary>
/// <param name="Name">The column name.</param>
/// <param name="TypeName">The SQL type name (resolved to a <c>DataKind</c> at execution time).</param>
/// <param name="Nullable">Whether the column accepts NULL values. Defaults to <see langword="true"/>.</param>
/// <param name="PrimaryKey">Whether this column is part of the primary key. Defaults to <see langword="false"/>.</param>
/// <param name="DefaultValue">
/// Optional <c>DEFAULT</c> literal expression for the column. The catalog
/// rejects non-literal defaults at <c>CREATE TABLE</c> time; only
/// <see cref="LiteralExpression"/> is honored in PR10b.
/// </param>
/// <param name="Identity">
/// Optional <c>IDENTITY[(seed, step)]</c> spec. When non-<see langword="null"/>,
/// the column is auto-filled at INSERT time and explicit values for it
/// are rejected (PostgreSQL <c>GENERATED ALWAYS</c> semantics).
/// </param>
/// <param name="ComputedExpression">
/// Optional <c>GENERATED ALWAYS AS (expr)</c> computed-column expression.
/// When non-<see langword="null"/>, the column's value is materialised
/// per row from the expression instead of accepting an explicit INSERT /
/// UPDATE value. Mutually exclusive with <see cref="DefaultValue"/> and
/// <see cref="Identity"/>; the catalog enforces at <c>CREATE TABLE</c> time.
/// </param>
/// <param name="PrimaryKeyConstraintName">
/// User-supplied PRIMARY KEY constraint name from a column-level
/// <c>CONSTRAINT name PRIMARY KEY</c> clause. Internal transport only —
/// the CreateTable builder hoists it to
/// <see cref="CreateTableStatement.PrimaryKeyConstraintName"/>. Meaningful
/// only when <see cref="PrimaryKey"/> is <see langword="true"/>;
/// <see langword="null"/> means "derive the default name at catalog time".
/// </param>
public sealed record ColumnDefinition(
    string Name,
    string TypeName,
    bool Nullable = true,
    bool PrimaryKey = false,
    Expression? DefaultValue = null,
    IdentitySpec? Identity = null,
    Expression? ComputedExpression = null,
    string? PrimaryKeyConstraintName = null);

/// <summary>
/// Identity spec on a <see cref="ColumnDefinition"/> — produced by the
/// PG-canonical <c>GENERATED [ALWAYS|BY DEFAULT] AS IDENTITY[(seed, step)]</c>
/// syntax (and the legacy bare <c>IDENTITY[(seed, step)]</c> form, which
/// behaves like <c>GENERATED ALWAYS AS IDENTITY</c>). Also surfaced
/// through the model's <c>ColumnInfo.Identity</c> after resolution.
/// Validated at <c>CREATE TABLE</c> time (one IDENTITY per table, integer
/// column kind, non-zero step, seed/step in range for the column's kind).
/// The running counter (next value the next INSERT would hand out) lives
/// on the provider, not here.
/// </summary>
/// <param name="Seed">First value the counter produces.</param>
/// <param name="Step">Increment applied after each generated value. Must be non-zero.</param>
/// <param name="AcceptUserValues">
/// When <see langword="false"/> (<c>GENERATED ALWAYS</c> — the default and
/// the semantic of bare <c>IDENTITY</c>), INSERTs that supply a value for
/// the IDENTITY column are rejected; the counter always wins. When
/// <see langword="true"/> (<c>GENERATED BY DEFAULT</c>), user-supplied
/// values are accepted as-is, and the counter is only consulted when the
/// column is omitted from the INSERT.
/// </param>
public sealed record IdentitySpec(long Seed, long Step, bool AcceptUserValues = false);

/// <summary>
/// <c>CREATE [TEMP] TABLE name AS SELECT ...</c> — creates a table
/// populated from a query (CTAS). The <see cref="IsTemp"/> flag selects
/// between in-memory and persistent forms, mirroring
/// <see cref="CreateTableStatement"/>.
/// </summary>
/// <param name="TableName">The name of the table to create.</param>
/// <param name="Query">The query whose results populate the table.</param>
/// <param name="IsTemp">When <see langword="true"/>, creates an in-memory temp table.</param>
/// <param name="IfNotExists">When <see langword="true"/>, suppresses errors if the table already exists.</param>
/// <param name="StoragePath">Optional explicit path (see <see cref="CreateTableStatement.StoragePath"/>).</param>
public sealed record CreateTableAsSelectStatement(
    string TableName,
    QueryExpression Query,
    bool IsTemp = false,
    bool IfNotExists = false,
    string? StoragePath = null) : Statement;

/// <summary>
/// <c>DROP TABLE [IF EXISTS] name</c> — removes a temporary table.
/// </summary>
/// <param name="TableName">The name of the table to drop.</param>
/// <param name="IfExists">When <see langword="true"/>, suppresses errors if the table does not exist.</param>
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>; <see langword="null"/> resolves against the default schema.</param>
public sealed record DropTableStatement(
    string TableName,
    bool IfExists = false,
    string? SchemaName = null) : Statement;

/// <summary>
/// <c>CREATE SCHEMA [IF NOT EXISTS] name</c> — registers a new user
/// schema in the catalog. Tables created with <c>CREATE TABLE
/// name.table</c> land under that schema. Built-in schemas
/// (<c>public</c>, <c>system</c>, <c>information_schema</c>,
/// <c>datum_catalog</c>) are pre-mounted and can't be re-created.
/// </summary>
/// <param name="SchemaName">The schema name to register.</param>
/// <param name="IfNotExists">When <see langword="true"/>, suppresses errors if the schema already exists.</param>
public sealed record CreateSchemaStatement(
    string SchemaName,
    bool IfNotExists = false) : Statement;

/// <summary>
/// <c>DROP SCHEMA [IF EXISTS] name [CASCADE | RESTRICT]</c> — removes a
/// user schema. <c>RESTRICT</c> (default) errors if the schema still
/// contains tables; <c>CASCADE</c> drops every table in the schema
/// first.
/// </summary>
/// <param name="SchemaName">The schema name to remove.</param>
/// <param name="IfExists">When <see langword="true"/>, suppresses errors if the schema does not exist.</param>
/// <param name="Cascade">When <see langword="true"/>, drops every table in the schema before removing it.</param>
public sealed record DropSchemaStatement(
    string SchemaName,
    bool IfExists = false,
    bool Cascade = false) : Statement;

/// <summary>
/// <c>SET search_path = schema1, schema2, ...</c> — updates the session's
/// schema-resolution order for unqualified table references. PG-style:
/// the leftmost schema is consulted first, falling through until a
/// match is found.
/// </summary>
/// <param name="Schemas">The ordered list of schemas to search.</param>
public sealed record SetSearchPathStatement(
    IReadOnlyList<string> Schemas) : Statement;

/// <summary>
/// <c>CREATE [UNIQUE] INDEX [IF NOT EXISTS] name ON table (col1[, col2]*)
/// [USING method] [WITH (opt = 'value', ...)]</c> — creates a maintained
/// secondary index over one or more columns of a table. The <c>USING</c>
/// clause selects an index method (default: composite B+Tree;
/// <c>FTS</c> for the full-text inverted index). The <c>WITH</c> clause
/// supplies method-specific options. Expression indexes and partial
/// indexes are not supported in v1.
/// </summary>
/// <param name="IndexName">The name of the index (used in DROP INDEX and as the sidecar filename).</param>
/// <param name="TableName">The table the index is built on.</param>
/// <param name="Columns">
/// Ordered list of columns covered by the index. Leftmost-prefix matching
/// applies at query time (Postgres semantics). Full-text indexes require
/// exactly one column.
/// </param>
/// <param name="IfNotExists">When <see langword="true"/>, suppresses errors if the index already exists.</param>
/// <param name="IsUnique">
/// When <see langword="true"/> (<c>CREATE UNIQUE INDEX</c>), enforces that no
/// two rows can share the encoded composite key. Violations on INSERT /
/// UPDATE throw <c>UniqueIndexViolationException</c>; backfill on a populated
/// table that already contains duplicates fails the CREATE INDEX statement
/// before the index becomes visible. Unlike <c>PRIMARY KEY</c>, <c>NULL</c> in
/// any covered column makes the row exempt from the uniqueness check (PG
/// NULLS DISTINCT default). Not valid for full-text indexes.
/// </param>
/// <param name="Method">
/// The <c>USING method</c> identifier, lower-cased (e.g. <c>"fts"</c>).
/// <see langword="null"/> when the clause is omitted — the catalog
/// interprets that as a composite B+Tree.
/// </param>
/// <param name="Options">
/// Method-specific <c>WITH (key = 'value', ...)</c> options. Keys are
/// lower-cased; values are string literals as written. <see langword="null"/>
/// or empty when the clause is omitted. The catalog validates which keys
/// are recognised per <see cref="Method"/>.
/// </param>
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>; <see langword="null"/> means the default schema.</param>
public sealed record CreateIndexStatement(
    string IndexName,
    string TableName,
    IReadOnlyList<string> Columns,
    bool IfNotExists = false,
    bool IsUnique = false,
    string? Method = null,
    IReadOnlyDictionary<string, string>? Options = null,
    string? SchemaName = null) : Statement;

/// <summary>
/// <c>DROP INDEX [IF EXISTS] name</c> — removes a maintained secondary index.
/// The backing <c>.datum-cindex-{name}</c> sidecar is deleted and the
/// catalog descriptor is updated. Multi-name DROP and CASCADE are not
/// supported in v1.
/// </summary>
/// <param name="IndexName">The name of the index to drop.</param>
/// <param name="IfExists">When <see langword="true"/>, suppresses errors if the index does not exist.</param>
public sealed record DropIndexStatement(
    string IndexName,
    bool IfExists = false) : Statement;

/// <summary>
/// <c>INSERT INTO name [(cols)] {VALUES (...) | SELECT ...} [RETURNING expr [, expr]*]</c>
/// — inserts rows into an existing table, optionally surfacing a projection of the
/// resolved (post-DEFAULT, post-IDENTITY) inserted rows. Mirrors PostgreSQL's INSERT
/// statement.
/// </summary>
/// <param name="TableName">The target table name.</param>
/// <param name="ColumnNames">Optional explicit column list. When <see langword="null"/>, all columns in declaration order.</param>
/// <param name="Source">The source of rows: either a <see cref="QueryExpression"/> or an <see cref="InsertValuesSource"/>.</param>
/// <param name="Returning">
/// Optional RETURNING clause. When non-<see langword="null"/>, the INSERT yields the
/// projection of the resolved rows after the implicit commit completes (post-commit
/// batch semantics — partial rows from an aborted INSERT are never observable).
/// Expressions resolve against each inserted row, the same scope SELECT projections
/// have. <see langword="null"/> when the INSERT is a side-effect-only statement.
/// </param>
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>; <see langword="null"/> means the default schema.</param>
public sealed record InsertStatement(
    string TableName,
    IReadOnlyList<string>? ColumnNames,
    InsertSource Source,
    IReadOnlyList<SelectColumn>? Returning = null,
    string? SchemaName = null) : Statement;

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
/// PostgreSQL <c>INSERT INTO t DEFAULT VALUES</c> — inserts exactly one row
/// in which every column is treated as omitted. Each column resolves through
/// the standard omitted-slot path (IDENTITY counter → DEFAULT expression →
/// NULL → throw on NOT NULL with no default). A column list is not permitted
/// with this form.
/// </summary>
public sealed record InsertDefaultValuesSource : InsertSource;

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
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>; <see langword="null"/> means the default schema.</param>
/// <param name="Returning">
/// Optional RETURNING clause. When non-<see langword="null"/>, the UPDATE yields the
/// post-image of every row whose WHERE predicate matched (including rows where the
/// SET assignments were no-ops). The projection-list shape mirrors a SELECT — bare
/// column references, computed expressions, <c>*</c>, and table-qualified <c>t.*</c>
/// all expand against the target table's schema. PG semantics.
/// </param>
public sealed record UpdateStatement(
    string TableName,
    string? Alias,
    IReadOnlyList<ColumnAssignment> Assignments,
    FromClause? From = null,
    IReadOnlyList<JoinClause>? Joins = null,
    Expression? Where = null,
    string? SchemaName = null,
    IReadOnlyList<SelectColumn>? Returning = null) : Statement;

/// <summary>
/// <c>DELETE FROM name [WHERE ...] [RETURNING expr [, expr]*]</c> — deletes rows from a table using tombstone bitmaps.
/// </summary>
/// <param name="TableName">The target table name.</param>
/// <param name="Where">Optional filter predicate restricting which rows are deleted. When omitted, all rows are deleted.</param>
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>; <see langword="null"/> means the default schema.</param>
/// <param name="Returning">
/// Optional RETURNING clause. When non-<see langword="null"/>, the DELETE yields the
/// pre-image of every row it tombstoned. PG semantics.
/// </param>
public sealed record DeleteStatement(
    string TableName,
    Expression? Where = null,
    string? SchemaName = null,
    IReadOnlyList<SelectColumn>? Returning = null) : Statement;

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
/// <param name="Identity">
/// Optional IDENTITY spec from a <c>GENERATED [ALWAYS|BY DEFAULT] AS IDENTITY</c>
/// clause (or legacy bare <c>IDENTITY</c>). When non-null, the new column is
/// backfilled into existing rows with sequential counter values and the
/// table's prologue identity state is initialised.
/// </param>
/// <param name="PrimaryKey">
/// When <see langword="true"/>, the new column becomes the table's PRIMARY KEY.
/// Requires that the table doesn't already have a PK; on a non-empty table the
/// column must also carry a <c>GENERATED IDENTITY</c> so historical rows
/// receive non-null unique values.
/// </param>
/// <param name="TableIfExists">
/// PG-canonical table-level guard. When <see langword="true"/>, the catalog
/// short-circuits the statement to a no-op when the named table doesn't
/// exist (set by the <c>ALTER TABLE IF EXISTS name …</c> prefix).
/// </param>
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>; <see langword="null"/> means the default schema.</param>
public sealed record AlterTableAddColumnStatement(
    string TableName,
    string ColumnName,
    string TypeName,
    Expression? DefaultValue = null,
    bool Nullable = true,
    Expression? ComputedExpression = null,
    IdentitySpec? Identity = null,
    bool PrimaryKey = false,
    bool TableIfExists = false,
    string? SchemaName = null) : Statement;

/// <summary>
/// <c>ALTER TABLE name DROP [COLUMN] col [IF EXISTS]</c> — soft-drops a
/// column from a table. The column is hidden from subsequent
/// <c>GetSchema</c> / scan output; the underlying storage is reclaimed
/// at compaction time.
/// </summary>
/// <param name="TableName">The target table name.</param>
/// <param name="ColumnName">The name of the column to drop.</param>
/// <param name="IfExists">When <see langword="true"/>, suppresses errors if the column does not exist.</param>
/// <param name="TableIfExists">
/// PG-canonical table-level guard from <c>ALTER TABLE IF EXISTS name …</c>;
/// the catalog short-circuits to a no-op when the named table doesn't exist.
/// </param>
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>; <see langword="null"/> means the default schema.</param>
public sealed record AlterTableDropColumnStatement(
    string TableName,
    string ColumnName,
    bool IfExists = false,
    bool TableIfExists = false,
    string? SchemaName = null) : Statement;

/// <summary>
/// <c>ALTER TABLE name DROP CONSTRAINT constraint_name [IF EXISTS]</c> —
/// removes a named constraint from a table. In v1 only PRIMARY KEY
/// constraints can be dropped (their auto-derived name is
/// <c>&lt;table&gt;_pkey</c>); future PRs will extend this to UNIQUE / FK /
/// CHECK once they exist.
/// </summary>
/// <param name="TableName">The target table name.</param>
/// <param name="ConstraintName">The constraint to drop (e.g., <c>users_pkey</c>).</param>
/// <param name="IfExists">When <see langword="true"/>, suppresses errors if the constraint does not exist.</param>
/// <param name="TableIfExists">
/// PG-canonical table-level guard from <c>ALTER TABLE IF EXISTS name …</c>;
/// the catalog short-circuits to a no-op when the named table doesn't exist.
/// </param>
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>; <see langword="null"/> means the default schema.</param>
public sealed record AlterTableDropConstraintStatement(
    string TableName,
    string ConstraintName,
    bool IfExists = false,
    bool TableIfExists = false,
    string? SchemaName = null) : Statement;

/// <summary>
/// Which column attribute an <see cref="AlterTableAlterColumnDropStatement"/>
/// targets. PG-compatible set: <c>DROP DEFAULT</c>, <c>DROP IDENTITY</c>,
/// <c>DROP NOT NULL</c>.
/// </summary>
public enum AlterColumnDropTarget
{
    /// <summary>Removes the column's <c>GENERATED IDENTITY</c> spec from the footer's identity state.</summary>
    Identity,

    /// <summary>Removes the column's <c>DEFAULT</c> expression from the footer's defaults table.</summary>
    Default,

    /// <summary>
    /// Relaxes the column to allow NULL values. Pre-existing pages keep
    /// whatever bitmap state they were written with (recorded per-page in
    /// <c>PageDescriptorV2.HasNullBitmap</c>); only future INSERTs gain
    /// the ability to write NULL.
    /// </summary>
    NotNull,
}

/// <summary>
/// <c>ALTER TABLE name ALTER COLUMN col DROP { IDENTITY | DEFAULT } [IF EXISTS]</c>
/// — clears a column attribute (identity sequence or default expression).
/// The column itself stays on the table; only the attribute is removed.
/// </summary>
/// <param name="TableName">The target table name.</param>
/// <param name="ColumnName">The column whose attribute is being dropped.</param>
/// <param name="Target">Which attribute to drop — <c>IDENTITY</c> or <c>DEFAULT</c>.</param>
/// <param name="IfExists">
/// When <see langword="true"/>, suppresses errors if the targeted attribute
/// is not present. PG accepts this for <c>DROP IDENTITY</c> but not
/// <c>DROP DEFAULT</c> (which is idempotent); we accept it uniformly and
/// treat both as idempotent under <c>IF EXISTS</c>.
/// </param>
/// <param name="TableIfExists">
/// PG-canonical table-level guard from <c>ALTER TABLE IF EXISTS name …</c>;
/// the catalog short-circuits to a no-op when the named table doesn't exist.
/// </param>
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>; <see langword="null"/> means the default schema.</param>
public sealed record AlterTableAlterColumnDropStatement(
    string TableName,
    string ColumnName,
    AlterColumnDropTarget Target,
    bool IfExists = false,
    bool TableIfExists = false,
    string? SchemaName = null) : Statement;

/// <summary>
/// Which column attribute an <see cref="AlterTableAlterColumnSetStatement"/>
/// targets. PG-compatible set in v1: <c>SET NOT NULL</c>. Mirror of
/// <see cref="AlterColumnDropTarget"/> — future targets (<c>SET DEFAULT expr</c>,
/// <c>SET DATA TYPE …</c>) live here when they ship.
/// </summary>
public enum AlterColumnSetTarget
{
    /// <summary>
    /// Tightens the column to reject NULL values. Catalog scans the column
    /// and rejects with a PG-flavored "column X contains NULL values"
    /// error when any row violates the new constraint. Pre-existing
    /// pages keep their wire format; the decoder reads each page through
    /// its own <c>HasNullBitmap</c> flag, so bitmap-bearing pages from
    /// before the SET (now redundant) decode correctly through compaction.
    /// </summary>
    NotNull,
}

/// <summary>
/// <c>ALTER TABLE name ALTER COLUMN col SET { NOT NULL }</c> — tightens a
/// column attribute. Sibling to <see cref="AlterTableAlterColumnDropStatement"/>;
/// kept as a separate AST rather than unified so each direction's
/// validation semantics (drop = footer-only; set = scan-first) stay
/// obvious at the dispatch site.
/// </summary>
/// <param name="TableName">The target table name.</param>
/// <param name="ColumnName">The column whose attribute is being set.</param>
/// <param name="Target">Which attribute to set — currently only <c>NOT NULL</c>.</param>
/// <param name="TableIfExists">
/// PG-canonical table-level guard from <c>ALTER TABLE IF EXISTS name …</c>;
/// the catalog short-circuits to a no-op when the named table doesn't exist.
/// </param>
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>.</param>
public sealed record AlterTableAlterColumnSetStatement(
    string TableName,
    string ColumnName,
    AlterColumnSetTarget Target,
    bool TableIfExists = false,
    string? SchemaName = null) : Statement;

/// <summary>
/// <c>ANALYZE table</c> — rebuilds statistics and indexes for the specified table.
/// </summary>
/// <param name="TableName">The target table name.</param>
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>; <see langword="null"/> means the default schema.</param>
public sealed record AnalyzeTableStatement(string TableName, string? SchemaName = null) : Statement;

/// <summary>
/// <c>REINDEX table</c> — rebuilds the <c>.datum-index</c> sidecar for
/// the specified table from its current contents. Replaces the
/// passive-invalidation behaviour: after any mutation
/// (INSERT / UPDATE / DELETE / ALTER) the cached index is dropped and
/// indexed queries fall back to scan; running <c>REINDEX</c> rebuilds
/// the sidecar and restores acceleration.
/// </summary>
/// <param name="TableName">The target table name.</param>
/// <param name="SchemaName">Optional schema qualifier from <c>schema.table</c>; <see langword="null"/> means the default schema.</param>
public sealed record ReindexTableStatement(string TableName, string? SchemaName = null) : Statement;

/// <summary>
/// A single declared parameter of a user-defined function:
/// <c>name TYPE [IS NOT NULL]</c>. Bare PG-style identifiers — the
/// <c>Name</c> field is the identifier as parsed. Inside the body, a
/// bare reference to that name resolves against the procedural variable
/// scope before the row schema (variable-first precedence). The type is
/// referenced by SQL type name (e.g. <c>String</c>, <c>Int32</c>) and
/// resolved to a <c>DataKind</c> at registration time.
/// </summary>
/// <param name="Name">The parameter name, used inside the body.</param>
/// <param name="TypeName">The SQL type name for the parameter.</param>
/// <param name="IsNotNull">
/// When <see langword="true"/>, the inliner wraps the substituted
/// argument with a runtime null assertion — passing a NULL at the call
/// site throws an <see cref="InvalidOperationException"/> with the
/// parameter name in the message. Defaults to <see langword="false"/>
/// (NULL is allowed and propagates through the body's normal three-
/// valued logic).
/// </param>
/// <param name="Default">
/// Optional default-value expression. When non-<see langword="null"/>,
/// callers may omit this argument (and any subsequent ones that also
/// have defaults); the executor evaluates the default in the call site's
/// scope and binds it as if the caller had passed it. The default expression
/// must appear in source order — once a parameter has a default, every
/// later parameter must as well, so the call-site arity is unambiguous.
/// Source-level syntax is <c>@name TYPE [IS NOT NULL] [= default-expr]</c>;
/// <c>IS NOT NULL</c> precedes <c>= expr</c> because <c>expr IS NOT NULL</c>
/// is itself a valid scalar predicate.
/// </param>
public sealed record UdfParameter(
    string Name,
    string TypeName,
    bool IsNotNull = false,
    Expression? Default = null);

/// <summary>
/// <c>CREATE [OR REPLACE] [PURE] FUNCTION [IF NOT EXISTS] name(@p1 TYPE [IS NOT NULL], @p2 TYPE [IS NOT NULL]) [RETURNS TYPE [IS NOT NULL]] {AS expression | BEGIN ... RETURN expr; ... END}</c>
/// — registers a user-defined scalar function. Two body shapes:
/// <list type="bullet">
///   <item><description><b>Macro UDF</b> — body is a single <see cref="Expression"/>
///   (<see cref="ExpressionBody"/> is set, <see cref="StatementBody"/> is <see langword="null"/>).
///   The planner inlines the body at every call site, with parameter references
///   substituted; name resolution happens in the caller's scope.</description></item>
///   <item><description><b>Procedural UDF</b> — body is a sequence of procedural
///   statements (<see cref="StatementBody"/> is set, <see cref="ExpressionBody"/> is
///   <see langword="null"/>) that must terminate with a <see cref="ReturnStatement"/>.
///   The planner does NOT inline; the executor evaluates the body once per call
///   with the parameters bound in a fresh procedural frame.</description></item>
/// </list>
/// Exactly one of <see cref="ExpressionBody"/> / <see cref="StatementBody"/> is non-null.
/// </summary>
/// <param name="Name">The unqualified UDF name. Call sites use the <c>udf.</c> prefix.</param>
/// <param name="Parameters">The declared parameters in order.</param>
/// <param name="ReturnTypeName">
/// Optional return-type annotation (<c>RETURNS TYPE</c>). When non-
/// <see langword="null"/>, the inliner wraps the substituted body with
/// an implicit <c>CAST</c> to the declared type, so the call site sees
/// the declared kind regardless of the body's natural type. When
/// <see langword="null"/>, the body's natural return type wins. Required
/// for procedural UDFs (the type system needs the return shape up front
/// because the body is opaque to the planner).
/// </param>
/// <param name="ReturnIsNotNull">
/// When <see langword="true"/>, the inliner wraps the substituted body
/// with a runtime null assertion in addition to any declared CAST. A
/// NULL return value at the call site throws with the UDF name in the
/// message. Defaults to <see langword="false"/>.
/// </param>
/// <param name="ExpressionBody">
/// The macro-form expression evaluated at every call site with parameter
/// references substituted, or <see langword="null"/> for procedural UDFs.
/// Named to parallel <see cref="StatementBody"/> so call sites disambiguate
/// the two body shapes by reading the property name alone.
/// </param>
/// <param name="StatementBody">
/// The procedural-form statement sequence (the body of <c>BEGIN…END</c>),
/// or <see langword="null"/> for macro UDFs.
/// </param>
/// <param name="IsPure">
/// When <see langword="true"/>, asserts the body is referentially transparent
/// (same inputs always produce the same output, no observable side effects).
/// Allows the planner's CSE pass to dedupe call sites with structurally
/// identical arguments. Macro UDFs are inlined and their purity is inherited
/// from the body expression; the flag is meaningful primarily for procedural UDFs.
/// </param>
/// <param name="IfNotExists">When <see langword="true"/>, suppresses errors if the UDF already exists.</param>
/// <param name="OrReplace">When <see langword="true"/>, replaces an existing UDF with the same name.</param>
/// <param name="Span">Source location of the UDF name for diagnostic reporting.</param>
/// <param name="SchemaName">Optional schema qualifier from <c>CREATE FUNCTION schema.fn(...)</c>; <see langword="null"/> picks the first DDL-capable schema on search_path.</param>
public sealed record CreateFunctionStatement(
    string Name,
    IReadOnlyList<UdfParameter> Parameters,
    string? ReturnTypeName,
    Expression? ExpressionBody,
    IReadOnlyList<Statement>? StatementBody = null,
    bool IsPure = false,
    bool IfNotExists = false,
    bool OrReplace = false,
    SourceSpan? Span = null,
    bool ReturnIsNotNull = false,
    string? SchemaName = null) : Statement;

/// <summary>
/// <c>CREATE [OR REPLACE] MODEL [IF NOT EXISTS] name(arg TYPE, ...) RETURNS T USING 'path' AS BEGIN ... END</c>
/// — registers a SQL-bodied function bound to one or more ONNX sessions
/// loaded from the path specified by <c>USING</c>. The body is procedural
/// (always <c>BEGIN…END</c>; never an inlined expression), must end with
/// <c>RETURN</c>, and may call the contextual <c>infer()</c> scalar to
/// dispatch tensors through the bound sessions.
/// </summary>
/// <remarks>
/// <para>
/// MODEL is structurally a procedural UDF with two extra concerns:
/// <list type="bullet">
///   <item><description>
///     The USING clause names a filesystem path (relative to the host's
///     <c>modelDirectory</c>, or absolute when the path is
///     <c>file://</c>-prefixed). At registration time the engine resolves
///     the path, asks the dispatcher to load the bundle, and binds the
///     resulting <c>IInferenceSession</c>(s) to this descriptor.
///   </description></item>
///   <item><description>
///     Registration lands in <c>ModelRegistry</c> (surfaced via
///     <c>system.models</c>), not <c>UdfRegistry</c>. The two have the
///     same shape under the hood but are deliberately kept separate so
///     queries like <c>SELECT * FROM system.udfs</c> stay focused on
///     SQL-only routines.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Unlike <see cref="CreateFunctionStatement"/>, the body shape is
/// always procedural and <see cref="ReturnTypeName"/> is required — a
/// model body without a return type has no scalar shape the planner can
/// validate against.
/// </para>
/// </remarks>
/// <param name="Name">Unqualified model name; combined with <see cref="SchemaName"/>.</param>
/// <param name="Parameters">Declared call-site parameters.</param>
/// <param name="ReturnTypeName">Required return-type annotation.</param>
/// <param name="UsingPath">
/// Path to the ONNX file or bundle directory. Relative paths resolve
/// against the host's models directory; <c>file://</c>-prefixed paths
/// are treated as absolute (useful for testing).
/// </param>
/// <param name="StatementBody">Procedural body. Always non-null on a valid model.</param>
/// <param name="IfNotExists">When <see langword="true"/>, no-op on conflict.</param>
/// <param name="OrReplace">When <see langword="true"/>, replaces an existing descriptor; previous bound sessions are disposed.</param>
/// <param name="Span">Source location for diagnostics.</param>
/// <param name="ReturnIsNotNull">Adds a runtime null-assertion to <c>RETURN</c>ed values when true.</param>
/// <param name="SchemaName">Optional schema qualifier; <see langword="null"/> walks search_path.</param>
public sealed record CreateModelStatement(
    string Name,
    IReadOnlyList<UdfParameter> Parameters,
    string ReturnTypeName,
    string UsingPath,
    IReadOnlyList<Statement> StatementBody,
    bool IfNotExists = false,
    bool OrReplace = false,
    SourceSpan? Span = null,
    bool ReturnIsNotNull = false,
    string? SchemaName = null) : Statement;

/// <summary>
/// <c>DROP MODEL [IF EXISTS] name</c> — removes a previously registered
/// model. Disposing the descriptor releases any bound ONNX sessions.
/// </summary>
/// <param name="Name">The model name to remove.</param>
/// <param name="IfExists">When <see langword="true"/>, suppresses errors if no such model exists.</param>
/// <param name="Span">Source location for diagnostic reporting.</param>
/// <param name="SchemaName">Optional schema qualifier; <see langword="null"/> walks search_path.</param>
public sealed record DropModelStatement(
    string Name,
    bool IfExists = false,
    SourceSpan? Span = null,
    string? SchemaName = null) : Statement;

/// <summary>
/// <c>DROP FUNCTION [IF EXISTS] name</c> — removes a previously registered UDF.
/// </summary>
/// <param name="Name">The UDF name to remove.</param>
/// <param name="IfExists">When <see langword="true"/>, suppresses errors if the UDF does not exist.</param>
/// <param name="Span">Source location of the UDF name for diagnostic reporting.</param>
/// <param name="SchemaName">Optional schema qualifier from <c>DROP FUNCTION schema.fn</c>; <see langword="null"/> walks search_path.</param>
public sealed record DropFunctionStatement(
    string Name,
    bool IfExists = false,
    SourceSpan? Span = null,
    string? SchemaName = null) : Statement;

/// <summary>
/// <c>CREATE [OR REPLACE] PROCEDURE [IF NOT EXISTS] name(@p1 TYPE [IS NOT NULL], ...) AS BEGIN ... END</c>
/// — registers a named procedural block. Procedures are not inlined; the
/// catalog stores the body verbatim and <c>CALL proc.name(...)</c> resolves
/// the descriptor at runtime, pushes a fresh batch context with the
/// parameters declared in its root frame, and runs the body's statements.
/// </summary>
/// <remarks>
/// Reuses <see cref="UdfParameter"/> for the parameter shape: same
/// <c>@</c>-prefix declaration, same optional <c>IS NOT NULL</c>
/// runtime null check applied to each argument before the parameter is
/// declared. Procedures don't return scalar values — output is whatever
/// rows the body's <c>SELECT</c> statements produce, surfaced to the
/// caller through the same <see cref="Statement"/> stream the body would
/// produce if inlined.
/// </remarks>
/// <param name="Name">The unqualified procedure name. Call sites use the <c>proc.</c> prefix.</param>
/// <param name="Parameters">The declared parameters in order.</param>
/// <param name="Body">The procedural batch the procedure runs on invocation.</param>
/// <param name="IfNotExists">When <see langword="true"/>, suppresses errors if the procedure already exists.</param>
/// <param name="OrReplace">When <see langword="true"/>, replaces an existing procedure with the same name.</param>
/// <param name="Span">Source location of the procedure name for diagnostic reporting.</param>
/// <param name="SchemaName">Optional schema qualifier from <c>CREATE PROCEDURE schema.proc(...)</c>; <see langword="null"/> picks the first DDL-capable schema on search_path.</param>
public sealed record CreateProcedureStatement(
    string Name,
    IReadOnlyList<UdfParameter> Parameters,
    BlockStatement Body,
    bool IfNotExists = false,
    bool OrReplace = false,
    SourceSpan? Span = null,
    string? SchemaName = null) : Statement;

/// <summary>
/// <c>DROP PROCEDURE [IF EXISTS] name</c> — removes a previously
/// registered procedure.
/// </summary>
/// <param name="Name">The procedure name to remove.</param>
/// <param name="IfExists">When <see langword="true"/>, suppresses errors if the procedure does not exist.</param>
/// <param name="Span">Source location of the procedure name for diagnostic reporting.</param>
/// <param name="SchemaName">Optional schema qualifier from <c>DROP PROCEDURE schema.proc</c>; <see langword="null"/> walks search_path.</param>
public sealed record DropProcedureStatement(
    string Name,
    bool IfExists = false,
    SourceSpan? Span = null,
    string? SchemaName = null) : Statement;

/// <summary>
/// <c>CALL namespace.functionname(arg1, arg2, ...)</c> — directly invokes a function
/// (typically a UDF via <c>udf.name(...)</c>) or procedure (via <c>proc.name(...)</c>)
/// as a top-level statement rather than inside a SELECT. The engine evaluates the call
/// expression and returns its result as a single-row, single-column result set.
/// </summary>
/// <param name="Call">The function call expression to invoke.</param>
/// <param name="Span">Source location of the CALL keyword for diagnostic reporting.</param>
public sealed record CallStatement(Expression Call, SourceSpan? Span = null) : Statement;

// ───────────────────── Procedural statements ─────────────────────

/// <summary>
/// A <c>BEGIN ... END</c> block — a sequence of statements executed in order
/// within a single lexical scope. Variables declared inside the block (via
/// <see cref="DeclareStatement"/>) are visible only until the matching
/// <c>END</c>; the scope is pushed on entry and popped on exit by the
/// procedural executor.
/// </summary>
/// <param name="Statements">The statements to execute in order. Empty blocks are valid.</param>
/// <param name="Span">Source location of the <c>BEGIN</c> keyword.</param>
public sealed record BlockStatement(
    IReadOnlyList<Statement> Statements,
    SourceSpan? Span = null) : Statement;

/// <summary>
/// <c>IF predicate then-stmt [ELSE else-stmt]</c> — conditional branch. The
/// predicate is evaluated against the current variable scope; whichever
/// branch matches runs as a single nested statement (typically a
/// <see cref="BlockStatement"/>). <c>ELSE IF</c> falls out naturally when
/// <see cref="Else"/> is itself an <see cref="IfStatement"/> — no special
/// syntactic form is needed.
/// </summary>
/// <param name="Predicate">Boolean-valued expression. NULL is treated as false.</param>
/// <param name="Then">Statement executed when the predicate is true.</param>
/// <param name="Else">Statement executed when the predicate is false; <see langword="null"/> for an IF without an ELSE.</param>
/// <param name="Span">Source location of the <c>IF</c> keyword.</param>
public sealed record IfStatement(
    Expression Predicate,
    Statement Then,
    Statement? Else = null,
    SourceSpan? Span = null) : Statement;

/// <summary>
/// <c>WHILE predicate body</c> — repeats <see cref="Body"/> while the
/// predicate evaluates to true. The predicate is re-evaluated against
/// the current scope before each iteration.
/// </summary>
/// <param name="Predicate">Boolean-valued expression checked once per iteration. NULL terminates the loop.</param>
/// <param name="Body">Statement executed each iteration (typically a <see cref="BlockStatement"/>).</param>
/// <param name="Span">Source location of the <c>WHILE</c> keyword.</param>
public sealed record WhileStatement(
    Expression Predicate,
    Statement Body,
    SourceSpan? Span = null) : Statement;

/// <summary>
/// Counter-FOR loop: <c>FOR @i = start TO end [STEP step] body</c>. The loop
/// variable is auto-declared at the loop scope, initialised to
/// <see cref="Start"/>, incremented by <see cref="Step"/> (defaults to
/// <c>1</c>) each iteration, and the loop runs while it remains
/// <c>&lt;= </c><see cref="End"/>. Inclusive on both ends, matching
/// Pascal/Ada conventions.
/// </summary>
/// <param name="VariableName">Loop variable name without the <c>@</c> prefix.</param>
/// <param name="Start">Inclusive starting value.</param>
/// <param name="End">Inclusive ending value.</param>
/// <param name="Step">Increment per iteration; <see langword="null"/> means <c>1</c>.</param>
/// <param name="Body">Statement executed each iteration.</param>
/// <param name="Span">Source location of the <c>FOR</c> keyword.</param>
public sealed record ForCounterStatement(
    string VariableName,
    Expression Start,
    Expression End,
    Expression? Step,
    Statement Body,
    SourceSpan? Span = null) : Statement;

/// <summary>
/// Cursor-FOR loop: <c>FOR @row IN (SELECT ...) body</c>. Iterates the rows
/// produced by <see cref="Source"/>, binding each row to the loop variable
/// (a struct of the source's columns) and running <see cref="Body"/> per
/// iteration.
/// </summary>
/// <param name="VariableName">Loop variable name without the <c>@</c> prefix.</param>
/// <param name="Source">Query whose rows the loop iterates over.</param>
/// <param name="Body">Statement executed each iteration.</param>
/// <param name="Span">Source location of the <c>FOR</c> keyword.</param>
public sealed record ForInStatement(
    string VariableName,
    QueryExpression Source,
    Statement Body,
    SourceSpan? Span = null) : Statement;

/// <summary>
/// <c>DECLARE @name TypeName [= initializer]</c> — introduces a new variable
/// in the current scope. <see cref="TypeName"/> is the textual SQL type
/// (matching <see cref="ColumnDefinition.TypeName"/>) resolved at execution
/// time. <see cref="Initializer"/> is evaluated once at declaration; absent
/// initializers leave the variable NULL.
/// </summary>
/// <remarks>
/// Either <see cref="TypeName"/> or <see cref="Initializer"/> must be
/// present — when both are absent the declaration is ill-formed (the
/// executor would have no type to bind). When <see cref="TypeName"/> is
/// <see langword="null"/>, the type is inferred from <see cref="Initializer"/>.
/// </remarks>
/// <param name="VariableName">Variable name without the <c>@</c> prefix.</param>
/// <param name="TypeName">Declared SQL type, or <see langword="null"/> to infer from the initializer.</param>
/// <param name="Initializer">Initial value expression, or <see langword="null"/> to default to NULL.</param>
/// <param name="Span">Source location of the <c>DECLARE</c> keyword.</param>
public sealed record DeclareStatement(
    string VariableName,
    string? TypeName,
    Expression? Initializer = null,
    SourceSpan? Span = null) : Statement;

/// <summary>
/// <c>SET @name = expression</c> — mutates an existing variable in the
/// current scope. The variable must have been previously declared via
/// <see cref="DeclareStatement"/> (in the same scope or an enclosing one);
/// the executor raises an error if the name is unbound.
/// </summary>
/// <param name="VariableName">Variable name without the <c>@</c> prefix.</param>
/// <param name="Value">New value expression.</param>
/// <param name="Span">Source location of the <c>SET</c> keyword.</param>
public sealed record SetStatement(
    string VariableName,
    Expression Value,
    SourceSpan? Span = null) : Statement;

/// <summary>
/// <c>RETURN expression</c> — terminates the enclosing procedural UDF body
/// and yields <paramref name="Value"/> as the function's scalar result. Only
/// valid inside the body of a procedural UDF (a <see cref="CreateFunctionStatement"/>
/// whose <see cref="CreateFunctionStatement.StatementBody"/> is non-null);
/// the executor raises an error if encountered elsewhere.
/// </summary>
/// <param name="Value">Expression whose value becomes the UDF's return value.</param>
/// <param name="Span">Source location of the <c>RETURN</c> keyword.</param>
public sealed record ReturnStatement(
    Expression Value,
    SourceSpan? Span = null) : Statement;

/// <summary>
/// <c>BREAK</c> — terminates the innermost enclosing <see cref="WhileStatement"/>,
/// <see cref="ForCounterStatement"/>, or <see cref="ForInStatement"/> immediately.
/// Outside of any loop the executor raises an error.
/// </summary>
/// <param name="Span">Source location of the <c>BREAK</c> keyword.</param>
public sealed record BreakStatement(
    SourceSpan? Span = null) : Statement;

/// <summary>
/// <c>CONTINUE</c> — skips the rest of the current iteration of the innermost
/// enclosing <see cref="WhileStatement"/>, <see cref="ForCounterStatement"/>, or
/// <see cref="ForInStatement"/> and proceeds to the next iteration.
/// Outside of any loop the executor raises an error.
/// </summary>
/// <param name="Span">Source location of the <c>CONTINUE</c> keyword.</param>
public sealed record ContinueStatement(
    SourceSpan? Span = null) : Statement;

/// <summary>
/// <c>PRINT expression</c> — evaluates <see cref="Value"/>, coerces the result
/// to a string, and emits it to the batch's event stream as a dedicated print
/// event. Intended for procedural diagnostics: progress markers, intermediate
/// values, and "what's happening" tracing during long procedures. Distinct
/// from a <c>SELECT</c> so callers can separate user-facing output rows from
/// diagnostic chatter.
/// </summary>
/// <param name="Value">Expression whose value is rendered as a string.</param>
/// <param name="Span">Source location of the <c>PRINT</c> keyword.</param>
public sealed record PrintStatement(
    Expression Value,
    SourceSpan? Span = null) : Statement;

/// <summary>
/// <c>ASSERT predicate [MESSAGE message-expr]</c> — procedural invariant
/// check. Evaluates the predicate; if false or NULL, throws with the
/// rendered message. When <see cref="Message"/> is omitted, the message
/// defaults to <c>"Assertion failed: &lt;predicate&gt;"</c> using the
/// formatted source of the predicate. Catchable by enclosing
/// <see cref="TryStatement"/>.
/// </summary>
/// <remarks>
/// Distinct from the SELECT-clause <c>ASSERT</c> (<see cref="AssertClause"/>),
/// which checks a per-row invariant during query execution and supports
/// SKIP semantics. The procedural form always aborts: in a sequential
/// statement stream the alternative ("skip the next statement") is
/// meaningless.
/// </remarks>
/// <param name="Predicate">Boolean-valued expression. NULL is treated as false.</param>
/// <param name="Message">Optional message expression; rendered to a string when the assertion fires.</param>
/// <param name="Span">Source location of the <c>ASSERT</c> keyword.</param>
public sealed record AssertStatement(
    Expression Predicate,
    Expression? Message = null,
    SourceSpan? Span = null) : Statement;

/// <summary>
/// <c>RAISE expression</c> — explicitly throws an error from procedural
/// code. The expression is evaluated and rendered to a string for the
/// exception message; an enclosing <see cref="TryStatement"/> catches it
/// the same way as any other thrown error. Inside a catch block,
/// <c>RAISE @err</c> rethrows the caught error.
/// </summary>
/// <param name="Message">Expression whose rendered value becomes the exception message.</param>
/// <param name="Span">Source location of the <c>RAISE</c> keyword.</param>
public sealed record RaiseStatement(
    Expression Message,
    SourceSpan? Span = null) : Statement;

/// <summary>
/// <c>TRY try-stmt CATCH @err catch-stmt [FINALLY finally-stmt]</c> —
/// procedural exception handling. The <c>TRY</c> body runs first; any
/// thrown exception (other than control-flow signals or cancellation)
/// causes the <c>CATCH</c> body to run with <see cref="ErrorVariableName"/>
/// auto-declared in a fresh scope frame and bound to the exception's
/// message. The optional <c>FINALLY</c> body runs unconditionally after
/// the try (and catch, if any), with standard try/finally semantics:
/// it executes on success, on caught error, and even when an exception
/// or control-flow signal is bubbling up — and a throw inside <c>FINALLY</c>
/// supersedes any pending exception.
/// </summary>
/// <remarks>
/// <c>BREAK</c> / <c>CONTINUE</c> / cancellation signals are not caught
/// by <c>CATCH</c> — they pass through (running <c>FINALLY</c> on the way)
/// to their respective handlers. Recursion-depth and other procedural
/// runtime errors <em>are</em> catchable; downstream code can surface
/// them as fallback paths.
/// </remarks>
/// <param name="TryBody">Statement executed first; typically a <see cref="BlockStatement"/>.</param>
/// <param name="ErrorVariableName">Variable name (without <c>@</c>) bound to the exception message in the catch frame.</param>
/// <param name="CatchBody">Statement executed when the try throws.</param>
/// <param name="FinallyBody">Optional cleanup statement; runs unconditionally after try/catch.</param>
/// <param name="Span">Source location of the <c>TRY</c> keyword.</param>
public sealed record TryStatement(
    Statement TryBody,
    string ErrorVariableName,
    Statement CatchBody,
    Statement? FinallyBody = null,
    SourceSpan? Span = null) : Statement;

// ───────────────────── ASSERT clause ─────────────────────

/// <summary>
/// Controls what happens when an <see cref="AssertClause"/> predicate evaluates to false or null.
/// </summary>
public enum AssertFailureMode
{
    /// <summary>Abort execution immediately and throw an <c>AssertionAbortException</c>.</summary>
    Abort,

    /// <summary>Silently discard the failing row; continue producing output for other rows.</summary>
    Skip,

    /// <summary>Record the failure in <c>AssertionDiagnostics</c> but still emit the row.</summary>
    Warn,
}

/// <summary>
/// A single <c>ASSERT</c> clause attached to a SELECT statement.
/// Evaluated per row against the augmented row (source columns plus memoized LET values)
/// after QUALIFY but before output projection.
/// </summary>
/// <param name="Predicate">The boolean expression that must hold for every row.</param>
/// <param name="Message">
/// Optional expression evaluated when the assertion fails; its string representation
/// is included in the <c>AssertionDiagnostics</c> sample or the abort exception.
/// When <see langword="null"/>, a generic "Assertion failed" message is used.
/// </param>
/// <param name="FailureMode">What to do when the predicate is false or null. Defaults to <see cref="AssertFailureMode.Abort"/>.</param>
/// <param name="Span">Source location of the <c>ASSERT</c> keyword for diagnostic reporting.</param>
public sealed record AssertClause(
    Expression Predicate,
    Expression? Message = null,
    AssertFailureMode FailureMode = AssertFailureMode.Abort,
    SourceSpan? Span = null);

// ───────────────────── Struct expressions ─────────────────────

/// <summary>
/// A single named field within a struct literal: <c>field_name: expression</c>.
/// </summary>
/// <param name="Name">The field name as written in the query.</param>
/// <param name="Value">The value expression for this field.</param>
public sealed record StructField(string Name, Expression Value);

/// <summary>
/// A struct literal expression: <c>{ field1: expr1, field2: expr2, ... }</c>.
/// Field names become the keys of the resulting struct value; their types are
/// resolved at plan time and stored in a <c>ColumnInfo</c>
/// with a <c>Fields</c> list so that no schema is allocated per row.
/// </summary>
/// <param name="Fields">The ordered list of named field/value pairs.</param>
/// <param name="Span">Source location spanning the opening brace to the closing brace.</param>
public sealed record StructLiteralExpression(
    IReadOnlyList<StructField> Fields,
    SourceSpan? Span = null) : Expression;

/// <summary>
/// A postfix index-access expression: <c>expr[index]</c>.
/// Used for array element access (<c>arr[0]</c>) and struct field access
/// by name (<c>row['field']</c>).
/// The <c>ExpressionTypeResolver</c> resolves the return type at plan time
/// using the element kind of the base expression's <c>ColumnInfo</c>.
/// </summary>
/// <param name="Source">The expression whose result is subscripted.</param>
/// <param name="Index">The index expression (integer for arrays, string for structs).</param>
/// <param name="Span">Source location of the opening bracket token.</param>
public sealed record IndexAccessExpression(
    Expression Source,
    Expression Index,
    SourceSpan? Span = null) : Expression;
