using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

public class SqlParserTests : ServiceTestBase
{
    private static SelectStatement Parse(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }

    // ───────────────────── Simple SELECT ─────────────────────

    [Fact]
    public void SimpleSelectSingleColumn()
    {
        SelectStatement result = Parse("SELECT name FROM users");

        Assert.Single(result.Columns);
        ColumnReference column = Assert.IsType<ColumnReference>(result.Columns[0].Expression);
        Assert.Equal("name", column.ColumnName);
        Assert.Null(column.TableName);

        Assert.NotNull(result.From);
        TableReference table = Assert.IsType<TableReference>(result.From.Source);
        Assert.Equal("users", table.Name);
    }

    [Fact]
    public void SelectMultipleColumns()
    {
        SelectStatement result = Parse("SELECT a, b, c FROM t");

        Assert.Equal(3, result.Columns.Count);

        ColumnReference colA = Assert.IsType<ColumnReference>(result.Columns[0].Expression);
        Assert.Equal("a", colA.ColumnName);

        ColumnReference colC = Assert.IsType<ColumnReference>(result.Columns[2].Expression);
        Assert.Equal("c", colC.ColumnName);
    }

    [Fact]
    public void SelectStar()
    {
        SelectStatement result = Parse("SELECT * FROM t");

        Assert.Single(result.Columns);
        Assert.IsType<SelectAllColumns>(result.Columns[0]);
    }

    [Fact]
    public void SelectStarExcept()
    {
        SelectStatement result = Parse("SELECT * EXCEPT (a, b) FROM t");

        Assert.Single(result.Columns);
        SelectAllColumns allColumns = Assert.IsType<SelectAllColumns>(result.Columns[0]);
        Assert.NotNull(allColumns.ExcludedColumns);
        Assert.Equal(new[] { "a", "b" }, allColumns.ExcludedColumns);
    }

    [Fact]
    public void SelectStarExcept_SingleColumn()
    {
        SelectStatement result = Parse("SELECT * EXCEPT (id) FROM t");

        SelectAllColumns allColumns = Assert.IsType<SelectAllColumns>(result.Columns[0]);
        Assert.NotNull(allColumns.ExcludedColumns);
        Assert.Equal(new[] { "id" }, allColumns.ExcludedColumns);
    }

    [Fact]
    public void SelectStar_HasNullExcludedColumns()
    {
        SelectStatement result = Parse("SELECT * FROM t");

        SelectAllColumns allColumns = Assert.IsType<SelectAllColumns>(result.Columns[0]);
        Assert.Null(allColumns.ExcludedColumns);
    }

    [Fact]
    public void SelectTableStar()
    {
        SelectStatement result = Parse("SELECT t.* FROM t");

        Assert.Single(result.Columns);
        SelectTableColumns tableColumns = Assert.IsType<SelectTableColumns>(result.Columns[0]);
        Assert.Equal("t", tableColumns.TableName);
    }

    [Fact]
    public void SelectTableStarExcept()
    {
        SelectStatement result = Parse("SELECT t.* EXCEPT (a) FROM t");

        Assert.Single(result.Columns);
        SelectTableColumns tableColumns = Assert.IsType<SelectTableColumns>(result.Columns[0]);
        Assert.Equal("t", tableColumns.TableName);
        Assert.NotNull(tableColumns.ExcludedColumns);
        Assert.Equal(new[] { "a" }, tableColumns.ExcludedColumns);
    }

    [Fact]
    public void SelectTableStarExcept_MultipleColumns()
    {
        SelectStatement result = Parse("SELECT t.* EXCEPT (x, y, z) FROM t");

        SelectTableColumns tableColumns = Assert.IsType<SelectTableColumns>(result.Columns[0]);
        Assert.Equal("t", tableColumns.TableName);
        Assert.NotNull(tableColumns.ExcludedColumns);
        Assert.Equal(new[] { "x", "y", "z" }, tableColumns.ExcludedColumns);
    }

    [Fact]
    public void SelectTableStar_HasNullExcludedColumns()
    {
        SelectStatement result = Parse("SELECT t.* FROM t");

        SelectTableColumns tableColumns = Assert.IsType<SelectTableColumns>(result.Columns[0]);
        Assert.Null(tableColumns.ExcludedColumns);
    }

    // ───────────────────── SELECT * REPLACE ─────────────────────

    [Fact]
    public void SelectStarReplace()
    {
        SelectStatement result = Parse("SELECT * REPLACE (x * 2 AS x) FROM t");

        SelectAllColumns allColumns = Assert.IsType<SelectAllColumns>(result.Columns[0]);
        Assert.Null(allColumns.ExcludedColumns);
        Assert.NotNull(allColumns.ReplacedColumns);
        Assert.Single(allColumns.ReplacedColumns);
        Assert.Equal("x", allColumns.ReplacedColumns[0].ColumnName);
        Assert.IsType<BinaryExpression>(allColumns.ReplacedColumns[0].Expression);
    }

    [Fact]
    public void SelectStarReplace_MultipleReplacements()
    {
        SelectStatement result = Parse("SELECT * REPLACE (a + 1 AS a, UPPER(b) AS b) FROM t");

        SelectAllColumns allColumns = Assert.IsType<SelectAllColumns>(result.Columns[0]);
        Assert.NotNull(allColumns.ReplacedColumns);
        Assert.Equal(2, allColumns.ReplacedColumns.Count);
        Assert.Equal("a", allColumns.ReplacedColumns[0].ColumnName);
        Assert.Equal("b", allColumns.ReplacedColumns[1].ColumnName);
    }

    [Fact]
    public void SelectStarExceptAndReplace()
    {
        SelectStatement result = Parse("SELECT * EXCEPT (id) REPLACE (x / 100.0 AS x) FROM t");

        SelectAllColumns allColumns = Assert.IsType<SelectAllColumns>(result.Columns[0]);
        Assert.NotNull(allColumns.ExcludedColumns);
        Assert.Equal(new[] { "id" }, allColumns.ExcludedColumns);
        Assert.NotNull(allColumns.ReplacedColumns);
        Assert.Single(allColumns.ReplacedColumns);
        Assert.Equal("x", allColumns.ReplacedColumns[0].ColumnName);
    }

    [Fact]
    public void SelectTableStarReplace()
    {
        SelectStatement result = Parse("SELECT t.* REPLACE (price / 100.0 AS price) FROM t");

        SelectTableColumns tableColumns = Assert.IsType<SelectTableColumns>(result.Columns[0]);
        Assert.Equal("t", tableColumns.TableName);
        Assert.NotNull(tableColumns.ReplacedColumns);
        Assert.Single(tableColumns.ReplacedColumns);
        Assert.Equal("price", tableColumns.ReplacedColumns[0].ColumnName);
    }

    [Fact]
    public void SelectStar_HasNullReplacedColumns()
    {
        SelectStatement result = Parse("SELECT * FROM t");

        SelectAllColumns allColumns = Assert.IsType<SelectAllColumns>(result.Columns[0]);
        Assert.Null(allColumns.ReplacedColumns);
    }

    [Fact]
    public void SelectWithAlias()
    {
        SelectStatement result = Parse("SELECT name AS n FROM users");

        Assert.Single(result.Columns);
        Assert.Equal("n", result.Columns[0].Alias);
    }

    /// <summary>Column alias without the AS keyword.</summary>
    [Fact]
    public void SelectWithAlias_WithoutAs()
    {
        SelectStatement result = Parse("SELECT r.Value e FROM RANGE(0, 100) AS r");

        Assert.Single(result.Columns);
        Assert.Equal("e", result.Columns[0].Alias);
    }

    [Fact]
    public void SelectQualifiedColumn()
    {
        SelectStatement result = Parse("SELECT t.column_name FROM t");

        Assert.Single(result.Columns);
        ColumnReference column = Assert.IsType<ColumnReference>(result.Columns[0].Expression);
        Assert.Equal("t", column.TableName);
        Assert.Equal("column_name", column.ColumnName);
    }

    // ───────────────────── Literals ─────────────────────

    [Fact]
    public void SelectNumberLiteral()
    {
        SelectStatement result = Parse("SELECT 42 FROM t");

        LiteralExpression literal = Assert.IsType<LiteralExpression>(result.Columns[0].Expression);
        Assert.Equal((sbyte)42, literal.Value);
    }

    // ───── Numeric literal narrowing: integers ─────

    // ───── Numeric literal narrowing: positive integers ─────

    [Theory]
    [InlineData("0", (sbyte)0)]
    [InlineData("1", (sbyte)1)]
    [InlineData("127", (sbyte)127)]       // sbyte.MaxValue
    public void NumericLiteral_SByteRange(string sql, object expected)
    {
        LiteralExpression literal = Assert.IsType<LiteralExpression>(
            Parse($"SELECT {sql} FROM t").Columns[0].Expression);
        Assert.IsType<sbyte>(literal.Value);
        Assert.Equal(expected, literal.Value);
    }

    [Theory]
    [InlineData("128", (short)128)]        // sbyte.MaxValue + 1
    [InlineData("32767", (short)32767)]    // short.MaxValue
    public void NumericLiteral_Int16Range(string sql, object expected)
    {
        LiteralExpression literal = Assert.IsType<LiteralExpression>(
            Parse($"SELECT {sql} FROM t").Columns[0].Expression);
        Assert.IsType<short>(literal.Value);
        Assert.Equal(expected, literal.Value);
    }

    [Theory]
    [InlineData("32768", 32768)]           // short.MaxValue + 1
    [InlineData("2147483647", 2147483647)] // int.MaxValue
    public void NumericLiteral_Int32Range(string sql, object expected)
    {
        LiteralExpression literal = Assert.IsType<LiteralExpression>(
            Parse($"SELECT {sql} FROM t").Columns[0].Expression);
        Assert.IsType<int>(literal.Value);
        Assert.Equal(expected, literal.Value);
    }

    [Theory]
    [InlineData("2147483648", 2147483648L)]   // int.MaxValue + 1
    [InlineData("9223372036854775807", 9223372036854775807L)] // long.MaxValue
    public void NumericLiteral_Int64Range(string sql, object expected)
    {
        LiteralExpression literal = Assert.IsType<LiteralExpression>(
            Parse($"SELECT {sql} FROM t").Columns[0].Expression);
        Assert.IsType<long>(literal.Value);
        Assert.Equal(expected, literal.Value);
    }

    // ───── Numeric literal narrowing: negative integers ─────
    // Note: negative literals are parsed as UnaryExpression(Negate, positive_literal),
    // so the literal value itself is always positive. We test negation indirectly.

    [Fact]
    public void NumericLiteral_NegativeNumber_ParsedAsUnaryNegate()
    {
        SelectStatement result = Parse("SELECT -128 FROM t");
        UnaryExpression unary = Assert.IsType<UnaryExpression>(result.Columns[0].Expression);
        Assert.Equal(UnaryOperator.Negate, unary.Operator);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(unary.Operand);
        Assert.IsType<short>(literal.Value); // 128 > sbyte.MaxValue(127), so short
        Assert.Equal((short)128, literal.Value);
    }

    // ───── Numeric literal narrowing: floating-point ─────
    // Whole-number decimals like 1.0 narrow to integer (Truncate(1.0)==1.0).
    // Fractional decimals stay as double unless float roundtrip is exact.

    [Fact]
    public void NumericLiteral_WholeDecimal_NarrowsToInteger()
    {
        // 1.0 → Truncate(1.0)==1.0 → sbyte(1)
        LiteralExpression literal = Assert.IsType<LiteralExpression>(
            Parse("SELECT 1.0 FROM t").Columns[0].Expression);
        Assert.IsType<sbyte>(literal.Value);
        Assert.Equal((sbyte)1, literal.Value);
    }

    [Fact]
    public void NumericLiteral_HalfValue_NarrowsToFloat()
    {
        // 0.5 is exactly representable as float: (double)(float)0.5 == 0.5.
        LiteralExpression literal = Assert.IsType<LiteralExpression>(
            Parse("SELECT 0.5 FROM t").Columns[0].Expression);
        Assert.IsType<float>(literal.Value);
        Assert.Equal(0.5f, literal.Value);
    }

    [Fact]
    public void NumericLiteral_Pi_StaysDouble()
    {
        // 3.14: (double)(float)3.14 != 3.14 due to precision loss → stays double
        LiteralExpression literal = Assert.IsType<LiteralExpression>(
            Parse("SELECT 3.14 FROM t").Columns[0].Expression);
        Assert.IsType<double>(literal.Value);
    }

    // Note: scientific notation (1e5, 1.23e1) is not supported by the SQL tokenizer.
    // The NumberToken recognizer only handles digits[.digits]. This is a pre-existing
    // limitation, not related to the narrowing change.

    [Fact]
    public void SelectStringLiteral()
    {
        SelectStatement result = Parse("SELECT 'hello' FROM t");

        LiteralExpression literal = Assert.IsType<LiteralExpression>(result.Columns[0].Expression);
        Assert.Equal("hello", literal.Value);
    }

    [Fact]
    public void SelectNullLiteral()
    {
        SelectStatement result = Parse("SELECT NULL FROM t");

        LiteralExpression literal = Assert.IsType<LiteralExpression>(result.Columns[0].Expression);
        Assert.Null(literal.Value);
    }

    // ───────────────────── Function calls ─────────────────────

    [Fact]
    public void SelectFunctionCall()
    {
        SelectStatement result = Parse("SELECT min_max_normalize(x, 0, 255) AS norm FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(
            result.Columns[0].Expression);
        Assert.Equal("min_max_normalize", function.FunctionName);
        Assert.Equal(3, function.Arguments.Count);
        Assert.Equal("norm", result.Columns[0].Alias);
    }

    [Fact]
    public void NestedFunctionCalls()
    {
        SelectStatement result = Parse("SELECT resize(load_image(data), 224, 224) FROM t");

        FunctionCallExpression outer = Assert.IsType<FunctionCallExpression>(
            result.Columns[0].Expression);
        Assert.Equal("resize", outer.FunctionName);
        Assert.Equal(3, outer.Arguments.Count);

        FunctionCallExpression inner = Assert.IsType<FunctionCallExpression>(outer.Arguments[0]);
        Assert.Equal("load_image", inner.FunctionName);
    }

    [Fact]
    public void FunctionCallWithNoArguments()
    {
        SelectStatement result = Parse("SELECT my_func() FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(
            result.Columns[0].Expression);
        Assert.Equal("my_func", function.FunctionName);
        Assert.Empty(function.Arguments);
    }

    // ───────────────────── WHERE clause ─────────────────────

    [Fact]
    public void WhereSimpleComparison()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE x > 5");

        Assert.NotNull(result.Where);
        BinaryExpression binary = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.GreaterThan, binary.Operator);

        ColumnReference left = Assert.IsType<ColumnReference>(binary.Left);
        Assert.Equal("x", left.ColumnName);

        LiteralExpression right = Assert.IsType<LiteralExpression>(binary.Right);
        Assert.Equal((sbyte)5, right.Value);
    }

    [Fact]
    public void WhereAndOr()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE x > 1 AND y < 2 OR z = 3");

        Assert.NotNull(result.Where);
        // OR has lower precedence than AND, so this is (x>1 AND y<2) OR (z=3)
        BinaryExpression orExpr = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.Or, orExpr.Operator);

        BinaryExpression andExpr = Assert.IsType<BinaryExpression>(orExpr.Left);
        Assert.Equal(BinaryOperator.And, andExpr.Operator);
    }

    [Fact]
    public void WhereNot()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE NOT x = 1");

        Assert.NotNull(result.Where);
        UnaryExpression unary = Assert.IsType<UnaryExpression>(result.Where);
        Assert.Equal(UnaryOperator.Not, unary.Operator);
    }

    [Fact]
    public void WhereIsNull()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE x IS NULL");

        Assert.NotNull(result.Where);
        IsNullExpression isNull = Assert.IsType<IsNullExpression>(result.Where);
        Assert.False(isNull.Negated);
    }

    [Fact]
    public void WhereIsNotNull()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE x IS NOT NULL");

        Assert.NotNull(result.Where);
        IsNullExpression isNotNull = Assert.IsType<IsNullExpression>(result.Where);
        Assert.True(isNotNull.Negated);
    }

    [Fact]
    public void WhereLike()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE name LIKE '%test%'");

        Assert.NotNull(result.Where);
        BinaryExpression like = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.Like, like.Operator);
    }

    [Fact]
    public void WhereILike()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE name ILIKE '%test%'");

        Assert.NotNull(result.Where);
        BinaryExpression ilike = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.ILike, ilike.Operator);
    }

    [Fact]
    public void WhereRegexp()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE phone REGEXP '^\\d{3}-\\d{4}$'");

        Assert.NotNull(result.Where);
        BinaryExpression regexp = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.Regexp, regexp.Operator);
    }

    [Fact]
    public void WhereNotILike()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE NOT name ILIKE '%test%'");

        Assert.NotNull(result.Where);
        UnaryExpression notExpr = Assert.IsType<UnaryExpression>(result.Where);
        Assert.Equal(UnaryOperator.Not, notExpr.Operator);
        BinaryExpression ilike = Assert.IsType<BinaryExpression>(notExpr.Operand);
        Assert.Equal(BinaryOperator.ILike, ilike.Operator);
    }

    [Fact]
    public void WhereNotRegexp()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE NOT phone REGEXP '^\\d+$'");

        Assert.NotNull(result.Where);
        UnaryExpression notExpr = Assert.IsType<UnaryExpression>(result.Where);
        Assert.Equal(UnaryOperator.Not, notExpr.Operator);
        BinaryExpression regexp = Assert.IsType<BinaryExpression>(notExpr.Operand);
        Assert.Equal(BinaryOperator.Regexp, regexp.Operator);
    }

    [Fact]
    public void WhereLikeEscape()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE value LIKE '100\\%' ESCAPE '\\'");

        Assert.NotNull(result.Where);
        LikeExpression like = Assert.IsType<LikeExpression>(result.Where);
        Assert.False(like.CaseInsensitive);
        LiteralExpression pattern = Assert.IsType<LiteralExpression>(like.Pattern);
        Assert.Equal("100\\%", pattern.Value);
        LiteralExpression escape = Assert.IsType<LiteralExpression>(like.EscapeCharacter);
        Assert.Equal("\\", escape.Value);
    }

    [Fact]
    public void WhereILikeEscape()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE name ILIKE '\\_test%' ESCAPE '\\'");

        Assert.NotNull(result.Where);
        LikeExpression like = Assert.IsType<LikeExpression>(result.Where);
        Assert.True(like.CaseInsensitive);
    }

    [Fact]
    public void WhereLikeWithoutEscapeProducesBinaryExpression()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE name LIKE '%test%'");

        Assert.NotNull(result.Where);
        BinaryExpression like = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.Like, like.Operator);
    }

    [Fact]
    public void WhereIn()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE x IN (1, 2, 3)");

        Assert.NotNull(result.Where);
        InExpression inExpr = Assert.IsType<InExpression>(result.Where);
        Assert.Equal(3, inExpr.Values.Count);
        Assert.False(inExpr.Negated);
    }

    [Fact]
    public void WhereNotIn()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE x NOT IN (1, 2)");

        Assert.NotNull(result.Where);
        InExpression inExpr = Assert.IsType<InExpression>(result.Where);
        Assert.True(inExpr.Negated);
    }

    [Fact]
    public void WhereBetween()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE x BETWEEN 1 AND 10");

        Assert.NotNull(result.Where);
        BetweenExpression between = Assert.IsType<BetweenExpression>(result.Where);
        Assert.False(between.Negated);
    }

    [Fact]
    public void WhereNotBetween()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE x NOT BETWEEN 1 AND 10");

        Assert.NotNull(result.Where);
        BetweenExpression between = Assert.IsType<BetweenExpression>(result.Where);
        Assert.True(between.Negated);
    }

    // ───────────────────── JOIN clauses ─────────────────────

    [Fact]
    public void InnerJoin()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t1 INNER JOIN t2 ON t1.id = t2.id");

        Assert.NotNull(result.Joins);
        Assert.Single(result.Joins);
        Assert.Equal(JoinType.Inner, result.Joins[0].Type);

        TableReference joined = Assert.IsType<TableReference>(result.Joins[0].Source);
        Assert.Equal("t2", joined.Name);
    }

    [Fact]
    public void ImplicitInnerJoin()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t1 JOIN t2 ON t1.id = t2.id");

        Assert.NotNull(result.Joins);
        Assert.Single(result.Joins);
        Assert.Equal(JoinType.Inner, result.Joins[0].Type);
    }

    [Fact]
    public void LeftJoin()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t1 LEFT JOIN t2 ON t1.id = t2.id");

        Assert.NotNull(result.Joins);
        Assert.Equal(JoinType.Left, result.Joins[0].Type);
    }

    [Fact]
    public void RightJoin()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t1 RIGHT JOIN t2 ON t1.id = t2.id");

        Assert.NotNull(result.Joins);
        Assert.Equal(JoinType.Right, result.Joins[0].Type);
    }

    [Fact]
    public void FullOuterJoin()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t1 FULL OUTER JOIN t2 ON t1.id = t2.id");

        Assert.NotNull(result.Joins);
        Assert.Equal(JoinType.FullOuter, result.Joins[0].Type);
    }

    [Fact]
    public void CrossJoin()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t1 CROSS JOIN t2");

        Assert.NotNull(result.Joins);
        Assert.Equal(JoinType.Cross, result.Joins[0].Type);
        // Cross join has no ON condition
        Assert.Null(result.Joins[0].OnCondition);
    }

    [Fact]
    public void MultipleJoins()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t1 JOIN t2 ON t1.id = t2.id LEFT JOIN t3 ON t2.fk = t3.id");

        Assert.NotNull(result.Joins);
        Assert.Equal(2, result.Joins.Count);
        Assert.Equal(JoinType.Inner, result.Joins[0].Type);
        Assert.Equal(JoinType.Left, result.Joins[1].Type);
    }

    [Fact]
    public void JoinWithTableAlias()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t1 AS x JOIN t2 AS y ON x.id = y.id");

        Assert.NotNull(result.From);
        TableReference from = Assert.IsType<TableReference>(result.From.Source);
        Assert.Equal("t1", from.Name);
        Assert.Equal("x", from.Alias);

        Assert.NotNull(result.Joins);
        TableReference joined = Assert.IsType<TableReference>(result.Joins[0].Source);
        Assert.Equal("t2", joined.Name);
        Assert.Equal("y", joined.Alias);
    }

    /// <summary>
    /// Table aliases without the AS keyword must be supported in both FROM
    /// and JOIN clauses — SQL standard bare alias syntax.
    /// </summary>
    [Fact]
    public void BareTableAlias_FromClause()
    {
        SelectStatement result = Parse("SELECT o.id FROM orders o WHERE o.id > 1");

        TableReference from = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Equal("orders", from.Name);
        Assert.Equal("o", from.Alias);
    }

    /// <summary>
    /// Bare table alias in a JOIN clause — no AS keyword.
    /// </summary>
    [Fact]
    public void BareTableAlias_JoinClause()
    {
        SelectStatement result = Parse(
            "SELECT o.id FROM orders o JOIN items i ON o.id = i.order_id");

        TableReference from = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Equal("orders", from.Name);
        Assert.Equal("o", from.Alias);

        Assert.NotNull(result.Joins);
        TableReference joined = Assert.IsType<TableReference>(result.Joins![0].Source);
        Assert.Equal("items", joined.Name);
        Assert.Equal("i", joined.Alias);
    }

    // ───────────────────── INTO clause ─────────────────────

    [Fact]
    public void IntoParquet()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t INTO 'output.parquet'");

        Assert.NotNull(result.Into);
        Assert.Equal(OutputFormat.Parquet, result.Into.Format);
        Assert.Equal("output.parquet", result.Into.Path);
        Assert.Null(result.Into.Shard);
    }

    [Fact]
    public void IntoHdf5()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t INTO 'output.h5'");

        Assert.NotNull(result.Into);
        Assert.Equal(OutputFormat.Hdf5, result.Into.Format);
    }

    [Fact]
    public void IntoCsv()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t INTO 'output.csv'");

        Assert.NotNull(result.Into);
        Assert.Equal(OutputFormat.Csv, result.Into.Format);
    }

    [Fact]
    public void IntoWithShardOnSampleCount()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t INTO 'out.parquet' SHARD ON sample_count 1000");

        Assert.NotNull(result.Into);
        Assert.NotNull(result.Into.Shard);
        Assert.Equal(ShardMode.SampleCount, result.Into.Shard.Mode);
        Assert.Equal(1000L, result.Into.Shard.Value);
    }

    [Fact]
    public void IntoWithShardOnByteSize()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t INTO 'out.h5' SHARD ON byte_size 104857600");

        Assert.NotNull(result.Into);
        Assert.NotNull(result.Into.Shard);
        Assert.Equal(ShardMode.ByteSize, result.Into.Shard.Mode);
        Assert.Equal(104857600L, result.Into.Shard.Value);
    }

    // ───────────────────── ORDER BY ─────────────────────

    [Fact]
    public void OrderByAscending()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t ORDER BY x ASC");

        Assert.NotNull(result.OrderBy);
        Assert.Single(result.OrderBy.Items);
        Assert.Equal(SortDirection.Ascending, result.OrderBy.Items[0].Direction);
    }

    [Fact]
    public void OrderByDescending()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t ORDER BY x DESC");

        Assert.NotNull(result.OrderBy);
        Assert.Equal(SortDirection.Descending, result.OrderBy.Items[0].Direction);
    }

    [Fact]
    public void OrderByDefaultIsAscending()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t ORDER BY x");

        Assert.NotNull(result.OrderBy);
        Assert.Equal(SortDirection.Ascending, result.OrderBy.Items[0].Direction);
    }

    [Fact]
    public void OrderByMultipleColumns()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t ORDER BY x ASC, y DESC");

        Assert.NotNull(result.OrderBy);
        Assert.Equal(2, result.OrderBy.Items.Count);
        Assert.Equal(SortDirection.Ascending, result.OrderBy.Items[0].Direction);
        Assert.Equal(SortDirection.Descending, result.OrderBy.Items[1].Direction);
    }

    // ───────────────────── LIMIT / OFFSET ─────────────────────

    [Fact]
    public void LimitClause()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t LIMIT 10");

        Assert.Equal(10, Convert.ToInt32(((LiteralExpression)result.Limit!).Value));
        Assert.Null(result.Offset);
    }

    [Fact]
    public void LimitWithOffset()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t LIMIT 10 OFFSET 20");

        Assert.Equal(10, Convert.ToInt32(((LiteralExpression)result.Limit!).Value));
        Assert.Equal(20, Convert.ToInt32(((LiteralExpression)result.Offset!).Value));
    }

    // ───────────────────── Subqueries ─────────────────────

    [Fact]
    public void SubqueryInFrom()
    {
        SelectStatement result = Parse(
            "SELECT name FROM (SELECT name, age FROM users) AS sub");

        Assert.NotNull(result.From);
        SubquerySource subquery = Assert.IsType<SubquerySource>(result.From.Source);
        Assert.Equal("sub", subquery.Alias);
        Assert.Equal(2, subquery.Query.Columns.Count);
    }

    /// <summary>Derived table alias without the AS keyword.</summary>
    [Fact]
    public void SubqueryInFrom_AliasWithoutAs()
    {
        SelectStatement result = Parse(
            "SELECT T.Value FROM (SELECT r.Value FROM RANGE(0, 100) AS r WHERE r.Value > 0) T");

        Assert.NotNull(result.From);
        SubquerySource subquery = Assert.IsType<SubquerySource>(result.From.Source);
        Assert.Equal("T", subquery.Alias);
    }

    [Fact]
    public void NestedSubqueries()
    {
        SelectStatement result = Parse(
            "SELECT x FROM (SELECT y FROM (SELECT z FROM t) AS inner_q) AS outer_q");

        Assert.NotNull(result.From);
        SubquerySource outer = Assert.IsType<SubquerySource>(result.From.Source);
        Assert.Equal("outer_q", outer.Alias);

        Assert.NotNull(outer.Query.From);
        SubquerySource inner = Assert.IsType<SubquerySource>(outer.Query.From.Source);
        Assert.Equal("inner_q", inner.Alias);
    }

    // ───────────────────── Function source in FROM ─────────────────────

    [Fact]
    public void FunctionSourceInFrom()
    {
        SelectStatement result = Parse(
            "SELECT x FROM RANGE(0, 360) AS r");

        Assert.NotNull(result.From);
        FunctionSource source = Assert.IsType<FunctionSource>(result.From.Source);
        Assert.Equal("RANGE", source.FunctionName);
        Assert.Equal(2, source.Arguments.Count);
        Assert.Equal("r", source.Alias);
    }

    [Fact]
    public void FunctionSourceWithoutAlias()
    {
        SelectStatement result = Parse(
            "SELECT x FROM RANGE(0, 10)");

        Assert.NotNull(result.From);
        FunctionSource source = Assert.IsType<FunctionSource>(result.From.Source);
        Assert.Equal("RANGE", source.FunctionName);
        Assert.Null(source.Alias);
    }

    /// <summary>Function source alias without the AS keyword.</summary>
    [Fact]
    public void FunctionSourceInFrom_AliasWithoutAs()
    {
        SelectStatement result = Parse(
            "SELECT r.Value FROM RANGE(0, 100) r WHERE r.Value > 0");

        Assert.NotNull(result.From);
        FunctionSource source = Assert.IsType<FunctionSource>(result.From.Source);
        Assert.Equal("RANGE", source.FunctionName);
        Assert.Equal("r", source.Alias);
    }

    [Fact]
    public void FunctionSourceWithThreeArguments()
    {
        SelectStatement result = Parse(
            "SELECT x FROM RANGE(0, 360, 0.5) AS r");

        Assert.NotNull(result.From);
        FunctionSource source = Assert.IsType<FunctionSource>(result.From.Source);
        Assert.Equal("RANGE", source.FunctionName);
        Assert.Equal(3, source.Arguments.Count);
        Assert.Equal("r", source.Alias);
    }

    [Fact]
    public void FunctionSourceInCrossJoin()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t1 CROSS JOIN RANGE(1, 10) AS r");

        Assert.NotNull(result.From);
        Assert.IsType<TableReference>(result.From.Source);
        Assert.NotNull(result.Joins);
        Assert.Single(result.Joins);
        Assert.Equal(JoinType.Cross, result.Joins[0].Type);
        FunctionSource joined = Assert.IsType<FunctionSource>(result.Joins[0].Source);
        Assert.Equal("RANGE", joined.FunctionName);
        Assert.Equal("r", joined.Alias);
    }

    // ───────────────────── LATERAL / APPLY joins ─────────────────────

    [Fact]
    public void CrossJoinLateral_FunctionSource()
    {
        SelectStatement result = Parse(
            "SELECT t.a, r.value FROM t CROSS JOIN LATERAL UNNEST(t.arr) AS r");

        Assert.NotNull(result.Joins);
        Assert.Single(result.Joins);
        Assert.Equal(JoinType.Cross, result.Joins[0].Type);
        Assert.True(result.Joins[0].IsLateral);
        FunctionSource source = Assert.IsType<FunctionSource>(result.Joins[0].Source);
        Assert.Equal("UNNEST", source.FunctionName);
        Assert.Equal("r", source.Alias);
    }

    [Fact]
    public void LeftJoinLateral_SubquerySource()
    {
        SelectStatement result = Parse(
            "SELECT t.a, sub.x FROM t LEFT JOIN LATERAL (SELECT a AS x FROM u WHERE u.id = t.id) AS sub ON 1 = 1");

        Assert.NotNull(result.Joins);
        Assert.Single(result.Joins);
        Assert.Equal(JoinType.Left, result.Joins[0].Type);
        Assert.True(result.Joins[0].IsLateral);
        SubquerySource source = Assert.IsType<SubquerySource>(result.Joins[0].Source);
        Assert.Equal("sub", source.Alias);
        Assert.NotNull(result.Joins[0].OnCondition);
    }

    [Fact]
    public void LeftOuterJoinLateral()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t LEFT OUTER JOIN LATERAL UNNEST(t.arr) AS r");

        Assert.NotNull(result.Joins);
        Assert.Equal(JoinType.Left, result.Joins[0].Type);
        Assert.True(result.Joins[0].IsLateral);
    }

    [Fact]
    public void CrossApply()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t CROSS APPLY UNNEST(t.arr) AS r");

        Assert.NotNull(result.Joins);
        Assert.Equal(JoinType.Cross, result.Joins[0].Type);
        Assert.True(result.Joins[0].IsLateral);
        FunctionSource source = Assert.IsType<FunctionSource>(result.Joins[0].Source);
        Assert.Equal("UNNEST", source.FunctionName);
    }

    [Fact]
    public void OuterApply()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t OUTER APPLY (SELECT x FROM u WHERE u.id = t.id) AS sub");

        Assert.NotNull(result.Joins);
        Assert.Equal(JoinType.Left, result.Joins[0].Type);
        Assert.True(result.Joins[0].IsLateral);
        SubquerySource source = Assert.IsType<SubquerySource>(result.Joins[0].Source);
        Assert.Equal("sub", source.Alias);
    }

    [Fact]
    public void CrossJoinWithoutLateral_IsNotLateral()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t1 CROSS JOIN t2");

        Assert.NotNull(result.Joins);
        Assert.Equal(JoinType.Cross, result.Joins[0].Type);
        Assert.False(result.Joins[0].IsLateral);
    }

    [Fact]
    public void LeftJoinWithoutLateral_IsNotLateral()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t1 LEFT JOIN t2 ON t1.id = t2.id");

        Assert.NotNull(result.Joins);
        Assert.Equal(JoinType.Left, result.Joins[0].Type);
        Assert.False(result.Joins[0].IsLateral);
    }

    // ───────────────────── Arithmetic expressions ─────────────────────

    [Fact]
    public void ArithmeticPrecedence()
    {
        SelectStatement result = Parse("SELECT a + b * c FROM t");

        // * has higher precedence than +, so it should be: a + (b * c)
        BinaryExpression add = Assert.IsType<BinaryExpression>(result.Columns[0].Expression);
        Assert.Equal(BinaryOperator.Add, add.Operator);

        BinaryExpression multiply = Assert.IsType<BinaryExpression>(add.Right);
        Assert.Equal(BinaryOperator.Multiply, multiply.Operator);
    }

    [Fact]
    public void ParenthesizedExpression()
    {
        SelectStatement result = Parse("SELECT (a + b) * c FROM t");

        // Parentheses override precedence: (a + b) * c
        BinaryExpression multiply = Assert.IsType<BinaryExpression>(result.Columns[0].Expression);
        Assert.Equal(BinaryOperator.Multiply, multiply.Operator);

        BinaryExpression add = Assert.IsType<BinaryExpression>(multiply.Left);
        Assert.Equal(BinaryOperator.Add, add.Operator);
    }

    [Fact]
    public void UnaryMinus()
    {
        SelectStatement result = Parse("SELECT -x FROM t");

        UnaryExpression unary = Assert.IsType<UnaryExpression>(result.Columns[0].Expression);
        Assert.Equal(UnaryOperator.Negate, unary.Operator);
    }

    [Fact]
    public void ModuloOperator()
    {
        SelectStatement result = Parse("SELECT a % b FROM t");

        BinaryExpression modulo = Assert.IsType<BinaryExpression>(result.Columns[0].Expression);
        Assert.Equal(BinaryOperator.Modulo, modulo.Operator);
    }

    [Fact]
    public void PowerOperator()
    {
        SelectStatement result = Parse("SELECT a ^ b FROM t");

        BinaryExpression power = Assert.IsType<BinaryExpression>(result.Columns[0].Expression);
        Assert.Equal(BinaryOperator.Power, power.Operator);
    }

    [Fact]
    public void PowerPrecedenceHigherThanMultiply()
    {
        SelectStatement result = Parse("SELECT a * b ^ c FROM t");

        // ^ has higher precedence than *, so: a * (b ^ c)
        BinaryExpression multiply = Assert.IsType<BinaryExpression>(result.Columns[0].Expression);
        Assert.Equal(BinaryOperator.Multiply, multiply.Operator);

        BinaryExpression power = Assert.IsType<BinaryExpression>(multiply.Right);
        Assert.Equal(BinaryOperator.Power, power.Operator);
    }

    [Fact]
    public void ModuloPrecedenceSameAsMultiply()
    {
        SelectStatement result = Parse("SELECT a + b % c FROM t");

        // % has same precedence as *, higher than +: a + (b % c)
        BinaryExpression add = Assert.IsType<BinaryExpression>(result.Columns[0].Expression);
        Assert.Equal(BinaryOperator.Add, add.Operator);

        BinaryExpression modulo = Assert.IsType<BinaryExpression>(add.Right);
        Assert.Equal(BinaryOperator.Modulo, modulo.Operator);
    }

    // ───────────────────── CAST ─────────────────────

    [Fact]
    public void CastExpression()
    {
        SelectStatement result = Parse("SELECT CAST(x AS Float32) FROM t");

        CastExpression cast = Assert.IsType<CastExpression>(result.Columns[0].Expression);
        Assert.Equal("Float32", cast.TargetType);
    }

    // ───────────────────── Temporal constants ─────────────────────

    [Fact]
    public void CurrentDate_ParsesAsTemporalConstant()
    {
        SelectStatement result = Parse("SELECT CURRENT_DATE FROM t");
        CurrentTimestampExpression expr = Assert.IsType<CurrentTimestampExpression>(result.Columns[0].Expression);
        Assert.Equal(CurrentTimestampKind.CurrentDate, expr.Kind);
        Assert.Null(expr.Precision);
    }

    [Fact]
    public void CurrentTimestamp_ParsesAsTemporalConstant()
    {
        SelectStatement result = Parse("SELECT CURRENT_TIMESTAMP FROM t");
        CurrentTimestampExpression expr = Assert.IsType<CurrentTimestampExpression>(result.Columns[0].Expression);
        Assert.Equal(CurrentTimestampKind.CurrentTimestamp, expr.Kind);
        Assert.Null(expr.Precision);
    }

    [Fact]
    public void CurrentTimestamp_WithPrecision()
    {
        SelectStatement result = Parse("SELECT CURRENT_TIMESTAMP(3) FROM t");
        CurrentTimestampExpression expr = Assert.IsType<CurrentTimestampExpression>(result.Columns[0].Expression);
        Assert.Equal(CurrentTimestampKind.CurrentTimestamp, expr.Kind);
        Assert.Equal(3, expr.Precision);
    }

    [Fact]
    public void CurrentTime_ParsesAsTemporalConstant()
    {
        SelectStatement result = Parse("SELECT CURRENT_TIME FROM t");
        CurrentTimestampExpression expr = Assert.IsType<CurrentTimestampExpression>(result.Columns[0].Expression);
        Assert.Equal(CurrentTimestampKind.CurrentTime, expr.Kind);
        Assert.Null(expr.Precision);
    }

    [Fact]
    public void CurrentTime_WithPrecision()
    {
        SelectStatement result = Parse("SELECT CURRENT_TIME(0) FROM t");
        CurrentTimestampExpression expr = Assert.IsType<CurrentTimestampExpression>(result.Columns[0].Expression);
        Assert.Equal(CurrentTimestampKind.CurrentTime, expr.Kind);
        Assert.Equal(0, expr.Precision);
    }

    [Fact]
    public void LocalTime_ParsesAsTemporalConstant()
    {
        SelectStatement result = Parse("SELECT LOCALTIME FROM t");
        CurrentTimestampExpression expr = Assert.IsType<CurrentTimestampExpression>(result.Columns[0].Expression);
        Assert.Equal(CurrentTimestampKind.CurrentTime, expr.Kind);
    }

    [Fact]
    public void LocalTimestamp_ParsesAsTemporalConstant()
    {
        SelectStatement result = Parse("SELECT LOCALTIMESTAMP FROM t");
        CurrentTimestampExpression expr = Assert.IsType<CurrentTimestampExpression>(result.Columns[0].Expression);
        Assert.Equal(CurrentTimestampKind.CurrentTimestamp, expr.Kind);
    }

    [Fact]
    public void LocalTimestamp_WithPrecision()
    {
        SelectStatement result = Parse("SELECT LOCALTIMESTAMP(6) FROM t");
        CurrentTimestampExpression expr = Assert.IsType<CurrentTimestampExpression>(result.Columns[0].Expression);
        Assert.Equal(CurrentTimestampKind.CurrentTimestamp, expr.Kind);
        Assert.Equal(6, expr.Precision);
    }

    [Fact]
    public void CurrentTimestamp_WithAlias()
    {
        SelectStatement result = Parse("SELECT CURRENT_TIMESTAMP AS ts FROM t");
        Assert.Equal("ts", result.Columns[0].Alias);
        Assert.IsType<CurrentTimestampExpression>(result.Columns[0].Expression);
    }

    // ───────────────────── Complex queries ─────────────────────

    [Fact]
    public void FullQueryWithAllClauses()
    {
        SelectStatement result = Parse(
            "SELECT resize(load_image(file_bytes), 224, 224) AS img, caption "
            + "FROM (SELECT file_name, file_bytes FROM images) AS inner_q "
            + "LEFT JOIN captions ON inner_q.file_name = captions.file_name "
            + "WHERE len(caption) > 10 "
            + "INTO 'output.parquet' SHARD ON sample_count 5000 "
            + "ORDER BY file_name ASC "
            + "LIMIT 100 OFFSET 0");

        Assert.Equal(2, result.Columns.Count);
        Assert.Equal("img", result.Columns[0].Alias);
        Assert.NotNull(result.From);
        Assert.IsType<SubquerySource>(result.From.Source);
        Assert.NotNull(result.Joins);
        Assert.Single(result.Joins);
        Assert.Equal(JoinType.Left, result.Joins[0].Type);
        Assert.NotNull(result.Where);
        Assert.NotNull(result.Into);
        Assert.Equal(OutputFormat.Parquet, result.Into.Format);
        Assert.NotNull(result.Into.Shard);
        Assert.Equal(ShardMode.SampleCount, result.Into.Shard.Mode);
        Assert.NotNull(result.OrderBy);
        Assert.Equal(100, Convert.ToInt32(((LiteralExpression)result.Limit!).Value));
        Assert.Equal(0, Convert.ToInt32(((LiteralExpression)result.Offset!).Value));
    }

    [Fact]
    public void FromTableAlias()
    {
        SelectStatement result = Parse("SELECT a FROM users AS u");

        Assert.NotNull(result.From);
        TableReference table = Assert.IsType<TableReference>(result.From.Source);
        Assert.Equal("users", table.Name);
        Assert.Equal("u", table.Alias);
    }

    [Fact]
    public void ComparisonOperators()
    {
        SelectStatement result = Parse("SELECT a FROM t WHERE x != 1 AND y <= 2 AND z >= 3");

        Assert.NotNull(result.Where);
        // The outermost operator should be AND
        BinaryExpression and2 = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.And, and2.Operator);
    }

    [Fact]
    public void SelectBooleanLiterals()
    {
        SelectStatement result = Parse("SELECT TRUE, FALSE FROM t");

        Assert.Equal(2, result.Columns.Count);

        LiteralExpression trueVal = Assert.IsType<LiteralExpression>(result.Columns[0].Expression);
        Assert.Equal(true, trueVal.Value);

        LiteralExpression falseVal = Assert.IsType<LiteralExpression>(result.Columns[1].Expression);
        Assert.Equal(false, falseVal.Value);
    }

    // ───────────────────── Quoted table names ─────────────────────

    [Fact]
    public void SelectFromDoubleQuotedTableName()
    {
        SelectStatement result = Parse("SELECT * FROM \"adult.data\"");

        Assert.NotNull(result.From);
        TableReference table = Assert.IsType<TableReference>(result.From.Source);
        Assert.Equal("adult.data", table.Name);
    }

    [Fact]
    public void SelectFromSingleQuotedTableName()
    {
        SelectStatement result = Parse("SELECT * FROM 'adult.data'");

        Assert.NotNull(result.From);
        TableReference table = Assert.IsType<TableReference>(result.From.Source);
        Assert.Equal("adult.data", table.Name);
    }

    // ───────────────────── GROUP BY ─────────────────────

    [Fact]
    public void GroupBySingleColumn()
    {
        SelectStatement result = Parse("SELECT category, COUNT(*) FROM products GROUP BY category");

        Assert.NotNull(result.GroupBy);
        Assert.Single(result.GroupBy.Expressions);
        ColumnReference groupKey = Assert.IsType<ColumnReference>(result.GroupBy.Expressions[0]);
        Assert.Equal("category", groupKey.ColumnName);
    }

    [Fact]
    public void GroupByMultipleColumns()
    {
        SelectStatement result = Parse(
            "SELECT department, status, COUNT(*) FROM orders GROUP BY department, status");

        Assert.NotNull(result.GroupBy);
        Assert.Equal(2, result.GroupBy.Expressions.Count);

        ColumnReference firstKey = Assert.IsType<ColumnReference>(result.GroupBy.Expressions[0]);
        Assert.Equal("department", firstKey.ColumnName);

        ColumnReference secondKey = Assert.IsType<ColumnReference>(result.GroupBy.Expressions[1]);
        Assert.Equal("status", secondKey.ColumnName);
    }

    [Fact]
    public void GroupByWithHaving()
    {
        SelectStatement result = Parse(
            "SELECT category, COUNT(*) FROM products GROUP BY category HAVING COUNT(*) > 5");

        Assert.NotNull(result.GroupBy);
        Assert.Single(result.GroupBy.Expressions);
        Assert.NotNull(result.Having);

        BinaryExpression having = Assert.IsType<BinaryExpression>(result.Having);
        Assert.Equal(BinaryOperator.GreaterThan, having.Operator);

        FunctionCallExpression havingFunction = Assert.IsType<FunctionCallExpression>(having.Left);
        Assert.Equal("COUNT", havingFunction.FunctionName);
    }

    [Fact]
    public void CountStarParsesAsLiteralStarArgument()
    {
        SelectStatement result = Parse("SELECT COUNT(*) FROM t");

        Assert.Single(result.Columns);
        FunctionCallExpression countCall =
            Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("COUNT", countCall.FunctionName);
        Assert.Single(countCall.Arguments);
        LiteralExpression starLiteral = Assert.IsType<LiteralExpression>(countCall.Arguments[0]);
        Assert.Equal("*", starLiteral.Value);
    }

    [Fact]
    public void GlobalAggregationWithoutGroupBy()
    {
        SelectStatement result = Parse("SELECT COUNT(*), SUM(price), AVG(quantity) FROM orders");

        Assert.Null(result.GroupBy);
        Assert.Equal(3, result.Columns.Count);

        FunctionCallExpression sum = Assert.IsType<FunctionCallExpression>(result.Columns[1].Expression);
        Assert.Equal("SUM", sum.FunctionName);
        Assert.Single(sum.Arguments);

        FunctionCallExpression avg = Assert.IsType<FunctionCallExpression>(result.Columns[2].Expression);
        Assert.Equal("AVG", avg.FunctionName);
    }

    [Fact]
    public void GroupByWithOrderByAndLimit()
    {
        SelectStatement result = Parse(
            "SELECT category, SUM(price) AS total "
            + "FROM products "
            + "GROUP BY category "
            + "ORDER BY total DESC "
            + "LIMIT 10");

        Assert.NotNull(result.GroupBy);
        Assert.Single(result.GroupBy.Expressions);
        Assert.Equal("total", result.Columns[1].Alias);
        Assert.NotNull(result.OrderBy);
        Assert.Equal(10, Convert.ToInt32(((LiteralExpression)result.Limit!).Value));
    }

    [Fact]
    public void GroupByWithQualifiedColumn()
    {
        SelectStatement result = Parse(
            "SELECT t.category, COUNT(*) FROM t GROUP BY t.category");

        Assert.NotNull(result.GroupBy);
        Assert.Single(result.GroupBy.Expressions);
        ColumnReference groupKey = Assert.IsType<ColumnReference>(result.GroupBy.Expressions[0]);
        Assert.Equal("t", groupKey.TableName);
        Assert.Equal("category", groupKey.ColumnName);
    }

    [Fact]
    public void GroupByAll_SetsIsAllFlag()
    {
        SelectStatement result = Parse(
            "SELECT department, region, COUNT(*) FROM sales GROUP BY ALL");

        Assert.NotNull(result.GroupBy);
        Assert.True(result.GroupBy.IsAll);
        Assert.Empty(result.GroupBy.Expressions);
    }

    [Fact]
    public void GroupByAll_CaseInsensitive()
    {
        SelectStatement result = Parse(
            "SELECT x, SUM(y) FROM t GROUP BY all");

        Assert.NotNull(result.GroupBy);
        Assert.True(result.GroupBy.IsAll);
    }

    [Fact]
    public void GroupByExplicit_IsAllIsFalse()
    {
        SelectStatement result = Parse(
            "SELECT department, COUNT(*) FROM sales GROUP BY department");

        Assert.NotNull(result.GroupBy);
        Assert.False(result.GroupBy.IsAll);
        Assert.Single(result.GroupBy.Expressions);
    }

    // ───────────────────── CASE expressions ─────────────────────

    [Fact]
    public void SearchedCaseExpression()
    {
        SelectStatement result = Parse(
            "SELECT CASE WHEN x > 0 THEN 'positive' ELSE 'non-positive' END FROM t");

        Assert.Single(result.Columns);
        CaseExpression caseExpr = Assert.IsType<CaseExpression>(result.Columns[0].Expression);
        Assert.Null(caseExpr.Operand);
        Assert.Single(caseExpr.WhenClauses);
        Assert.IsType<BinaryExpression>(caseExpr.WhenClauses[0].Condition);
        Assert.IsType<LiteralExpression>(caseExpr.WhenClauses[0].Result);
        Assert.NotNull(caseExpr.ElseResult);
        Assert.IsType<LiteralExpression>(caseExpr.ElseResult);
    }

    [Fact]
    public void SimpleCaseExpression()
    {
        SelectStatement result = Parse(
            "SELECT CASE status WHEN 1 THEN 'active' WHEN 2 THEN 'inactive' END FROM t");

        Assert.Single(result.Columns);
        CaseExpression caseExpr = Assert.IsType<CaseExpression>(result.Columns[0].Expression);
        Assert.NotNull(caseExpr.Operand);
        ColumnReference operand = Assert.IsType<ColumnReference>(caseExpr.Operand);
        Assert.Equal("status", operand.ColumnName);
        Assert.Equal(2, caseExpr.WhenClauses.Count);
        Assert.Null(caseExpr.ElseResult);
    }

    [Fact]
    public void CaseExpressionWithoutElse()
    {
        SelectStatement result = Parse(
            "SELECT CASE WHEN x = 1 THEN 'one' END FROM t");

        CaseExpression caseExpr = Assert.IsType<CaseExpression>(result.Columns[0].Expression);
        Assert.Null(caseExpr.Operand);
        Assert.Single(caseExpr.WhenClauses);
        Assert.Null(caseExpr.ElseResult);
    }

    [Fact]
    public void CaseExpressionMultipleWhenClauses()
    {
        SelectStatement result = Parse(
            "SELECT CASE WHEN x = 1 THEN 'a' WHEN x = 2 THEN 'b' WHEN x = 3 THEN 'c' ELSE 'd' END FROM t");

        CaseExpression caseExpr = Assert.IsType<CaseExpression>(result.Columns[0].Expression);
        Assert.Equal(3, caseExpr.WhenClauses.Count);
        Assert.NotNull(caseExpr.ElseResult);
    }

    [Fact]
    public void CaseExpressionInWhere()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t WHERE CASE WHEN x > 0 THEN 1 ELSE 0 END = 1");

        Assert.NotNull(result.Where);
        BinaryExpression binary = Assert.IsType<BinaryExpression>(result.Where);
        Assert.IsType<CaseExpression>(binary.Left);
    }

    [Fact]
    public void CaseExpressionWithAlias()
    {
        SelectStatement result = Parse(
            "SELECT CASE WHEN x > 0 THEN 'yes' ELSE 'no' END AS label FROM t");

        Assert.Equal("label", result.Columns[0].Alias);
        Assert.IsType<CaseExpression>(result.Columns[0].Expression);
    }

    [Fact]
    public void NestedCaseExpression()
    {
        SelectStatement result = Parse(
            "SELECT CASE WHEN x > 0 THEN CASE WHEN x > 10 THEN 'big' ELSE 'small' END ELSE 'zero' END FROM t");

        CaseExpression outer = Assert.IsType<CaseExpression>(result.Columns[0].Expression);
        CaseExpression inner = Assert.IsType<CaseExpression>(outer.WhenClauses[0].Result);
        Assert.Single(inner.WhenClauses);
        Assert.NotNull(inner.ElseResult);
    }

    // ───────────────────── Lambda expressions ─────────────────────

    [Fact]
    public void SingleParameterLambda_InFunctionArgument()
    {
        SelectStatement result = Parse(
            "SELECT array_transform(arr, x -> x * 2) FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("array_transform", function.FunctionName);
        Assert.Equal(2, function.Arguments.Count);

        LambdaExpression lambda = Assert.IsType<LambdaExpression>(function.Arguments[1]);
        Assert.Single(lambda.Parameters);
        Assert.Equal("x", lambda.Parameters[0]);
        Assert.IsType<BinaryExpression>(lambda.Body);
    }

    [Fact]
    public void ParenthesizedSingleParameterLambda()
    {
        SelectStatement result = Parse(
            "SELECT array_filter(arr, (x) -> x > 0) FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        LambdaExpression lambda = Assert.IsType<LambdaExpression>(function.Arguments[1]);
        Assert.Single(lambda.Parameters);
        Assert.Equal("x", lambda.Parameters[0]);
    }

    [Fact]
    public void MultiParameterLambda()
    {
        SelectStatement result = Parse(
            "SELECT array_reduce(arr, (acc, x) -> acc + x, 0) FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal(3, function.Arguments.Count);

        LambdaExpression lambda = Assert.IsType<LambdaExpression>(function.Arguments[1]);
        Assert.Equal(2, lambda.Parameters.Count);
        Assert.Equal("acc", lambda.Parameters[0]);
        Assert.Equal("x", lambda.Parameters[1]);
        Assert.IsType<BinaryExpression>(lambda.Body);
    }

    [Fact]
    public void LambdaWithFunctionCallBody()
    {
        SelectStatement result = Parse(
            "SELECT array_transform(tags, t -> upper(t)) FROM t");

        FunctionCallExpression outer = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        LambdaExpression lambda = Assert.IsType<LambdaExpression>(outer.Arguments[1]);
        FunctionCallExpression body = Assert.IsType<FunctionCallExpression>(lambda.Body);
        Assert.Equal("upper", body.FunctionName);
    }

    [Fact]
    public void LambdaWithComparisonBody()
    {
        SelectStatement result = Parse(
            "SELECT array_filter(scores, s -> s > 0.5) FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        LambdaExpression lambda = Assert.IsType<LambdaExpression>(function.Arguments[1]);
        BinaryExpression body = Assert.IsType<BinaryExpression>(lambda.Body);
        Assert.Equal(BinaryOperator.GreaterThan, body.Operator);
    }

    [Fact]
    public void LambdaWithComplexBody()
    {
        SelectStatement result = Parse(
            "SELECT array_transform(arr, x -> (x - 10) / 5) FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        LambdaExpression lambda = Assert.IsType<LambdaExpression>(function.Arguments[1]);
        Assert.IsType<BinaryExpression>(lambda.Body);
    }

    [Fact]
    public void LambdaHasSourceSpan()
    {
        SelectStatement result = Parse(
            "SELECT array_transform(arr, x -> x) FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        LambdaExpression lambda = Assert.IsType<LambdaExpression>(function.Arguments[1]);
        Assert.NotNull(lambda.Span);
    }

    [Fact]
    public void NonLambdaColumnReference_StillWorks()
    {
        SelectStatement result = Parse("SELECT x FROM t");

        ColumnReference column = Assert.IsType<ColumnReference>(result.Columns[0].Expression);
        Assert.Equal("x", column.ColumnName);
    }

    // ───────────────────── Array literal sugar ─────────────────────

    [Fact]
    public void ArrayLiteral_DesugarsToArrayFunction()
    {
        SelectStatement result = Parse("SELECT [1, 2, 3] FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("array", function.FunctionName);
        Assert.Equal(3, function.Arguments.Count);
        Assert.Equal((sbyte)1, Assert.IsType<LiteralExpression>(function.Arguments[0]).Value);
        Assert.Equal((sbyte)2, Assert.IsType<LiteralExpression>(function.Arguments[1]).Value);
        Assert.Equal((sbyte)3, Assert.IsType<LiteralExpression>(function.Arguments[2]).Value);
    }

    [Fact]
    public void ArrayLiteral_SingleElement()
    {
        SelectStatement result = Parse("SELECT [42] FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("array", function.FunctionName);
        Assert.Single(function.Arguments);
        Assert.Equal((sbyte)42, Assert.IsType<LiteralExpression>(function.Arguments[0]).Value);
    }

    [Fact]
    public void ArrayLiteral_Empty()
    {
        SelectStatement result = Parse("SELECT [] FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("array", function.FunctionName);
        Assert.Empty(function.Arguments);
    }

    [Fact]
    public void ArrayLiteral_WithExpressions()
    {
        SelectStatement result = Parse("SELECT [a + 1, b * 2] FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("array", function.FunctionName);
        Assert.Equal(2, function.Arguments.Count);
        Assert.IsType<BinaryExpression>(function.Arguments[0]);
        Assert.IsType<BinaryExpression>(function.Arguments[1]);
    }

    [Fact]
    public void ArrayLiteral_WithStringElements()
    {
        SelectStatement result = Parse("SELECT ['hello', 'world'] FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("array", function.FunctionName);
        Assert.Equal(2, function.Arguments.Count);
        Assert.Equal("hello", Assert.IsType<LiteralExpression>(function.Arguments[0]).Value);
        Assert.Equal("world", Assert.IsType<LiteralExpression>(function.Arguments[1]).Value);
    }

    [Fact]
    public void ArrayLiteral_Nested()
    {
        SelectStatement result = Parse("SELECT [[1, 2], [3, 4]] FROM t");

        FunctionCallExpression outer = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("array", outer.FunctionName);
        Assert.Equal(2, outer.Arguments.Count);

        FunctionCallExpression inner1 = Assert.IsType<FunctionCallExpression>(outer.Arguments[0]);
        Assert.Equal("array", inner1.FunctionName);
        Assert.Equal(2, inner1.Arguments.Count);

        FunctionCallExpression inner2 = Assert.IsType<FunctionCallExpression>(outer.Arguments[1]);
        Assert.Equal("array", inner2.FunctionName);
        Assert.Equal(2, inner2.Arguments.Count);
    }

    [Fact]
    public void ArrayLiteral_HasSourceSpan()
    {
        SelectStatement result = Parse("SELECT [1, 2] FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.NotNull(function.Span);
    }

    [Fact]
    public void ArrayLiteral_WithLambda()
    {
        SelectStatement result = Parse(
            "SELECT array_transform([10, 20, 30], x -> x * 2) FROM t");

        FunctionCallExpression outer = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("array_transform", outer.FunctionName);

        FunctionCallExpression arrayArg = Assert.IsType<FunctionCallExpression>(outer.Arguments[0]);
        Assert.Equal("array", arrayArg.FunctionName);
        Assert.Equal(3, arrayArg.Arguments.Count);

        Assert.IsType<LambdaExpression>(outer.Arguments[1]);
    }

    // ───────────────────── Window functions ─────────────────────

    [Fact]
    public void WindowFunction_RowNumber_OverEmpty()
    {
        SelectStatement result = Parse("SELECT ROW_NUMBER() OVER () FROM t");

        WindowFunctionCallExpression window =
            Assert.IsType<WindowFunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("ROW_NUMBER", window.FunctionName);
        Assert.Empty(window.Arguments);
        Assert.Null(window.Window.PartitionBy);
        Assert.Null(window.Window.OrderBy);
        Assert.Null(window.Window.Frame);
    }

    [Fact]
    public void WindowFunction_WithPartitionBy()
    {
        SelectStatement result = Parse(
            "SELECT ROW_NUMBER() OVER (PARTITION BY category) FROM t");

        WindowFunctionCallExpression window =
            Assert.IsType<WindowFunctionCallExpression>(result.Columns[0].Expression);
        Assert.NotNull(window.Window.PartitionBy);
        Assert.Single(window.Window.PartitionBy);

        ColumnReference partitionCol =
            Assert.IsType<ColumnReference>(window.Window.PartitionBy[0]);
        Assert.Equal("category", partitionCol.ColumnName);
    }

    [Fact]
    public void WindowFunction_WithOrderBy()
    {
        SelectStatement result = Parse(
            "SELECT RANK() OVER (ORDER BY score DESC) FROM t");

        WindowFunctionCallExpression window =
            Assert.IsType<WindowFunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("RANK", window.FunctionName);
        Assert.NotNull(window.Window.OrderBy);
        Assert.Single(window.Window.OrderBy);
        Assert.Equal(SortDirection.Descending, window.Window.OrderBy[0].Direction);
    }

    [Fact]
    public void WindowFunction_WithPartitionByAndOrderBy()
    {
        SelectStatement result = Parse(
            "SELECT ROW_NUMBER() OVER (PARTITION BY dept ORDER BY hire_date ASC) FROM t");

        WindowFunctionCallExpression window =
            Assert.IsType<WindowFunctionCallExpression>(result.Columns[0].Expression);
        Assert.NotNull(window.Window.PartitionBy);
        Assert.Single(window.Window.PartitionBy);
        Assert.NotNull(window.Window.OrderBy);
        Assert.Single(window.Window.OrderBy);
        Assert.Equal(SortDirection.Ascending, window.Window.OrderBy[0].Direction);
    }

    [Fact]
    public void WindowFunction_WithRowsFrame()
    {
        SelectStatement result = Parse(
            "SELECT SUM(val) OVER (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM t");

        WindowFunctionCallExpression window =
            Assert.IsType<WindowFunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("SUM", window.FunctionName);
        Assert.NotNull(window.Window.Frame);
        Assert.Equal(WindowFrameType.Rows, window.Window.Frame.FrameType);
        Assert.IsType<UnboundedPrecedingBound>(window.Window.Frame.Start);
        Assert.IsType<CurrentRowBound>(window.Window.Frame.End);
    }

    [Fact]
    public void WindowFunction_WithPrecedingAndFollowingFrame()
    {
        SelectStatement result = Parse(
            "SELECT AVG(val) OVER (ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING) FROM t");

        WindowFunctionCallExpression window =
            Assert.IsType<WindowFunctionCallExpression>(result.Columns[0].Expression);
        Assert.NotNull(window.Window.Frame);

        PrecedingBound start = Assert.IsType<PrecedingBound>(window.Window.Frame.Start);
        Assert.Equal(1, start.Offset);

        FollowingBound end = Assert.IsType<FollowingBound>(window.Window.Frame.End);
        Assert.Equal(1, end.Offset);
    }

    [Fact]
    public void WindowFunction_WithAlias()
    {
        SelectStatement result = Parse(
            "SELECT ROW_NUMBER() OVER (ORDER BY id) AS rn FROM t");

        WindowFunctionCallExpression window =
            Assert.IsType<WindowFunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("ROW_NUMBER", window.FunctionName);
        Assert.Equal("rn", result.Columns[0].Alias);
    }

    [Fact]
    public void WindowFunction_LagWithArguments()
    {
        SelectStatement result = Parse(
            "SELECT LAG(price, 2, 0) OVER (ORDER BY date) FROM t");

        WindowFunctionCallExpression window =
            Assert.IsType<WindowFunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("LAG", window.FunctionName);
        Assert.Equal(3, window.Arguments.Count);
    }

    [Fact]
    public void WindowFunction_MultiplePartitionByKeys()
    {
        SelectStatement result = Parse(
            "SELECT SUM(amount) OVER (PARTITION BY dept, region ORDER BY date) FROM t");

        WindowFunctionCallExpression window =
            Assert.IsType<WindowFunctionCallExpression>(result.Columns[0].Expression);
        Assert.NotNull(window.Window.PartitionBy);
        Assert.Equal(2, window.Window.PartitionBy.Count);
    }

    [Fact]
    public void WindowFunction_UnboundedFollowingFrame()
    {
        SelectStatement result = Parse(
            "SELECT SUM(val) OVER (ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) FROM t");

        WindowFunctionCallExpression window =
            Assert.IsType<WindowFunctionCallExpression>(result.Columns[0].Expression);
        Assert.NotNull(window.Window.Frame);
        Assert.IsType<CurrentRowBound>(window.Window.Frame.Start);
        Assert.IsType<UnboundedFollowingBound>(window.Window.Frame.End);
    }

    // ───────────────────── DISTINCT ─────────────────────

    [Fact]
    public void SelectDistinct_SetsDistinctFlag()
    {
        SelectStatement result = Parse("SELECT DISTINCT name FROM users");

        Assert.True(result.Distinct);
        Assert.Single(result.Columns);
        ColumnReference column = Assert.IsType<ColumnReference>(result.Columns[0].Expression);
        Assert.Equal("name", column.ColumnName);
    }

    [Fact]
    public void SelectWithoutDistinct_DefaultsFalse()
    {
        SelectStatement result = Parse("SELECT name FROM users");

        Assert.False(result.Distinct);
    }

    [Fact]
    public void SelectDistinct_MultipleColumns()
    {
        SelectStatement result = Parse("SELECT DISTINCT a, b, c FROM t");

        Assert.True(result.Distinct);
        Assert.Equal(3, result.Columns.Count);
    }

    [Fact]
    public void FunctionCall_WithDistinct()
    {
        SelectStatement result = Parse("SELECT COUNT(DISTINCT name) FROM users");

        Assert.False(result.Distinct);
        FunctionCallExpression func =
            Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("COUNT", func.FunctionName);
        Assert.True(func.Distinct);
        Assert.Single(func.Arguments);
        ColumnReference arg = Assert.IsType<ColumnReference>(func.Arguments[0]);
        Assert.Equal("name", arg.ColumnName);
    }

    [Fact]
    public void FunctionCall_WithoutDistinct_DefaultsFalse()
    {
        SelectStatement result = Parse("SELECT COUNT(name) FROM users");

        FunctionCallExpression func =
            Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.False(func.Distinct);
    }

    [Fact]
    public void SelectDistinct_WithCountDistinct()
    {
        SelectStatement result = Parse(
            "SELECT DISTINCT category, COUNT(DISTINCT name) FROM products GROUP BY category");

        Assert.True(result.Distinct);
        FunctionCallExpression func =
            Assert.IsType<FunctionCallExpression>(result.Columns[1].Expression);
        Assert.True(func.Distinct);
    }

    [Fact]
    public void SumDistinct()
    {
        SelectStatement result = Parse("SELECT SUM(DISTINCT price) FROM products");

        FunctionCallExpression func =
            Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("SUM", func.FunctionName);
        Assert.True(func.Distinct);
    }

    // ──────────────────── WITHIN GROUP ────────────────────

    /// <summary>
    /// <c>MODE() WITHIN GROUP (ORDER BY col)</c> parses as a FunctionCallExpression
    /// with the WITHIN GROUP order-by items kept on
    /// <see cref="FunctionCallExpression.WithinGroupOrderBy"/>; the argument
    /// list stays empty (the planner reads
    /// <see cref="DatumIngest.Functions.WithinGroupSemantics"/> on
    /// <c>MODE</c> and prepends the data column at planning time).
    /// </summary>
    [Fact]
    public void ModeWithinGroup_AscendingOrder_PopulatesWithinGroupOrderBy()
    {
        SelectStatement result = Parse(
            "SELECT MODE() WITHIN GROUP (ORDER BY order_hour) FROM orders");

        FunctionCallExpression func =
            Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);

        Assert.Equal("MODE", func.FunctionName);
        Assert.Empty(func.Arguments);
        Assert.Null(func.OrderBy);

        Assert.NotNull(func.WithinGroupOrderBy);
        Assert.Single(func.WithinGroupOrderBy);
        ColumnReference orderCol = Assert.IsType<ColumnReference>(func.WithinGroupOrderBy[0].Expression);
        Assert.Equal("order_hour", orderCol.ColumnName);
        Assert.Equal(SortDirection.Ascending, func.WithinGroupOrderBy[0].Direction);
    }

    /// <summary>
    /// Descending direction in WITHIN GROUP is preserved.
    /// </summary>
    [Fact]
    public void ModeWithinGroup_DescendingOrder_PreservesDirection()
    {
        SelectStatement result = Parse(
            "SELECT MODE() WITHIN GROUP (ORDER BY score DESC) FROM events");

        FunctionCallExpression func =
            Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);

        Assert.NotNull(func.WithinGroupOrderBy);
        Assert.Equal(SortDirection.Descending, func.WithinGroupOrderBy[0].Direction);
    }

    /// <summary>
    /// WITHIN GROUP works alongside a GROUP BY clause (the typical feature-engineering pattern).
    /// </summary>
    [Fact]
    public void ModeWithinGroup_WithGroupBy_ParsesCorrectly()
    {
        SelectStatement result = Parse(
            "SELECT user_id, MODE() WITHIN GROUP (ORDER BY order_hour) AS preferred_hour FROM orders GROUP BY user_id");

        Assert.Equal(2, result.Columns.Count);
        FunctionCallExpression func =
            Assert.IsType<FunctionCallExpression>(result.Columns[1].Expression);
        Assert.Equal("MODE", func.FunctionName);
        Assert.Equal("preferred_hour", result.Columns[1].Alias);
        Assert.NotNull(result.GroupBy);
        Assert.Single(result.GroupBy.Expressions);
    }

    /// <summary>
    /// <c>PERCENTILE_DISC(fraction) WITHIN GROUP (ORDER BY col)</c> keeps the
    /// fraction in the argument list and the ORDER BY column on
    /// <see cref="FunctionCallExpression.WithinGroupOrderBy"/>. The planner
    /// inspects <see cref="DatumIngest.Functions.WithinGroupSemantics.OrderedSet"/>
    /// on PERCENTILE_DISC and prepends the data column to args before
    /// dispatch, producing the two-arg contract <c>(expression, fraction)</c>
    /// the accumulator expects.
    /// </summary>
    [Fact]
    public void PercentileDiscWithinGroup_ParsesArgsAndWithinGroupSeparately()
    {
        SelectStatement result = Parse(
            "SELECT PERCENTILE_DISC(0.5) WITHIN GROUP (ORDER BY salary) FROM employees");

        FunctionCallExpression func =
            Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("PERCENTILE_DISC", func.FunctionName);

        // Inner args = the fraction only.
        Assert.Single(func.Arguments);
        LiteralExpression fraction = Assert.IsType<LiteralExpression>(func.Arguments[0]);
        Assert.Equal(0.5f, fraction.Value);

        // WITHIN GROUP carries the data column.
        Assert.Null(func.OrderBy);
        Assert.NotNull(func.WithinGroupOrderBy);
        Assert.Single(func.WithinGroupOrderBy);
        ColumnReference expr = Assert.IsType<ColumnReference>(func.WithinGroupOrderBy[0].Expression);
        Assert.Equal("salary", expr.ColumnName);
    }

    // ───────────────────── Struct literal ─────────────────────

    [Fact]
    public void StructLiteral_SingleField()
    {
        SelectStatement result = Parse("SELECT {x: 1} FROM t");

        StructLiteralExpression literal =
            Assert.IsType<StructLiteralExpression>(result.Columns[0].Expression);
        Assert.Single(literal.Fields);
        Assert.Equal("x", literal.Fields[0].Name);
        Assert.Equal((sbyte)1, Assert.IsType<LiteralExpression>(literal.Fields[0].Value).Value);
    }

    [Fact]
    public void StructLiteral_MultipleFields()
    {
        SelectStatement result = Parse("SELECT {name: 'alice', age: 30} FROM t");

        StructLiteralExpression literal =
            Assert.IsType<StructLiteralExpression>(result.Columns[0].Expression);
        Assert.Equal(2, literal.Fields.Count);
        Assert.Equal("name", literal.Fields[0].Name);
        Assert.Equal("alice", Assert.IsType<LiteralExpression>(literal.Fields[0].Value).Value);
        Assert.Equal("age", literal.Fields[1].Name);
        Assert.Equal((sbyte)30, Assert.IsType<LiteralExpression>(literal.Fields[1].Value).Value);
    }

    [Fact]
    public void StructLiteral_WithColumnReferenceValue()
    {
        SelectStatement result = Parse("SELECT {val: price} FROM t");

        StructLiteralExpression literal =
            Assert.IsType<StructLiteralExpression>(result.Columns[0].Expression);
        Assert.Single(literal.Fields);
        Assert.Equal("val", literal.Fields[0].Name);
        ColumnReference col = Assert.IsType<ColumnReference>(literal.Fields[0].Value);
        Assert.Equal("price", col.ColumnName);
    }

    [Fact]
    public void StructLiteral_HasSourceSpan()
    {
        SelectStatement result = Parse("SELECT {x: 1} FROM t");

        StructLiteralExpression literal =
            Assert.IsType<StructLiteralExpression>(result.Columns[0].Expression);
        Assert.NotNull(literal.Span);
    }

    /// <summary>Struct literal with bare alias (no AS keyword).</summary>
    [Fact]
    public void StructLiteral_BareAlias()
    {
        SelectStatement result = Parse(
            "SELECT r.Value, { test: r.Value } exp FROM RANGE(0, 100) r WHERE r.Value > 0");

        Assert.Equal(2, result.Columns.Count);
        Assert.IsType<StructLiteralExpression>(result.Columns[1].Expression);
        Assert.Equal("exp", result.Columns[1].Alias);
    }

    // ───────────────────── Index access (bracket operator) ─────────────────────

    [Fact]
    public void IndexAccess_OnColumnReference_IntegerIndex()
    {
        SelectStatement result = Parse("SELECT arr[0] FROM t");

        IndexAccessExpression access =
            Assert.IsType<IndexAccessExpression>(result.Columns[0].Expression);
        ColumnReference source = Assert.IsType<ColumnReference>(access.Source);
        Assert.Equal("arr", source.ColumnName);
        Assert.Equal((sbyte)0, Assert.IsType<LiteralExpression>(access.Index).Value);
    }

    [Fact]
    public void IndexAccess_OnColumnReference_StringIndex()
    {
        SelectStatement result = Parse("SELECT obj['field'] FROM t");

        IndexAccessExpression access =
            Assert.IsType<IndexAccessExpression>(result.Columns[0].Expression);
        ColumnReference source = Assert.IsType<ColumnReference>(access.Source);
        Assert.Equal("obj", source.ColumnName);
        Assert.Equal("field", Assert.IsType<LiteralExpression>(access.Index).Value);
    }

    [Fact]
    public void IndexAccess_OnStructLiteral()
    {
        SelectStatement result = Parse("SELECT {x: 1, y: 2}['x'] FROM t");

        IndexAccessExpression access =
            Assert.IsType<IndexAccessExpression>(result.Columns[0].Expression);
        Assert.IsType<StructLiteralExpression>(access.Source);
        Assert.Equal("x", Assert.IsType<LiteralExpression>(access.Index).Value);
    }

    [Fact]
    public void IndexAccess_HasSourceSpan()
    {
        SelectStatement result = Parse("SELECT arr[1] FROM t");

        IndexAccessExpression access =
            Assert.IsType<IndexAccessExpression>(result.Columns[0].Expression);
        Assert.NotNull(access.Span);
    }

    // ───────────────────── AT TIME ZONE ─────────────────────

    [Fact]
    public void AtTimeZone_ParsesColumnWithStringLiteral()
    {
        SelectStatement result = Parse("SELECT ts AT TIME ZONE 'America/New_York' FROM t");

        AtTimeZoneExpression atz = Assert.IsType<AtTimeZoneExpression>(result.Columns[0].Expression);
        Assert.IsType<ColumnReference>(atz.Expression);
        LiteralExpression tz = Assert.IsType<LiteralExpression>(atz.TimeZone);
        Assert.Equal("America/New_York", tz.Value);
    }

    [Fact]
    public void AtTimeZone_ParsesWithAlias()
    {
        SelectStatement result = Parse("SELECT ts AT TIME ZONE 'UTC' AS utc_ts FROM t");

        Assert.Equal("utc_ts", result.Columns[0].Alias);
        Assert.IsType<AtTimeZoneExpression>(result.Columns[0].Expression);
    }

    [Fact]
    public void AtTimeZone_HasSourceSpan()
    {
        SelectStatement result = Parse("SELECT ts AT TIME ZONE 'UTC' FROM t");

        AtTimeZoneExpression atz = Assert.IsType<AtTimeZoneExpression>(result.Columns[0].Expression);
        Assert.NotNull(atz.Span);
    }

    [Fact]
    public void AtTimeZone_InWhereClause()
    {
        // Verify it parses in a predicate position (chained with a comparison)
        SelectStatement result = Parse(
            "SELECT ts FROM t WHERE ts AT TIME ZONE 'UTC' = ts AT TIME ZONE 'America/New_York'");

        Assert.IsType<BinaryExpression>(result.Where);
    }

    // ───────────────────── typeof() and type literals ─────────────────────

    [Fact]
    public void TypeLiteral_ParsedInExpression()
    {
        SelectStatement result = Parse("SELECT Int32 FROM t");

        Assert.Single(result.Columns);
        TypeLiteralExpression typeLiteral = Assert.IsType<TypeLiteralExpression>(result.Columns[0].Expression);
        Assert.Equal("Int32", typeLiteral.TypeName);
    }

    [Fact]
    public void Typeof_Comparison_ParsedCorrectly()
    {
        SelectStatement result = Parse("SELECT * FROM t WHERE typeof(x) = Int32");

        BinaryExpression binary = Assert.IsType<BinaryExpression>(result.Where);
        FunctionCallExpression func = Assert.IsType<FunctionCallExpression>(binary.Left);
        Assert.Equal("typeof", func.FunctionName);
        TypeLiteralExpression typeLiteral = Assert.IsType<TypeLiteralExpression>(binary.Right);
        Assert.Equal("Int32", typeLiteral.TypeName);
    }

    [Fact]
    public void Cast_WithTypeKeyword_StillWorks()
    {
        SelectStatement result = Parse("SELECT CAST(x AS Int32) FROM t");

        Assert.Single(result.Columns);
        CastExpression cast = Assert.IsType<CastExpression>(result.Columns[0].Expression);
        Assert.Equal("Int32", cast.TargetType);
    }

    [Fact]
    public void TypeKeyword_AsAlias_Works()
    {
        SelectStatement result = Parse("SELECT 1 AS Int32 FROM t");

        Assert.Single(result.Columns);
        Assert.Equal("Int32", result.Columns[0].Alias);
    }

    [Fact]
    public void TypeLiteral_InCaseWhen()
    {
        SelectStatement result = Parse(
            "SELECT CASE typeof(x) WHEN Int32 THEN 'integer' ELSE 'other' END FROM t");

        Assert.Single(result.Columns);
        CaseExpression caseExpr = Assert.IsType<CaseExpression>(result.Columns[0].Expression);
        FunctionCallExpression operand = Assert.IsType<FunctionCallExpression>(caseExpr.Operand);
        Assert.Equal("typeof", operand.FunctionName);
        TypeLiteralExpression whenValue = Assert.IsType<TypeLiteralExpression>(caseExpr.WhenClauses[0].Condition);
        Assert.Equal("Int32", whenValue.TypeName);
    }

    // ───────────────────── IS [NOT] Type ─────────────────────

    [Fact]
    public void IsType_DesugarsToTypeofEquals()
    {
        SelectStatement result = Parse("SELECT * FROM t WHERE x IS Int32");

        BinaryExpression binary = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.Equal, binary.Operator);

        FunctionCallExpression func = Assert.IsType<FunctionCallExpression>(binary.Left);
        Assert.Equal("typeof", func.FunctionName);
        Assert.Single(func.Arguments);
        ColumnReference col = Assert.IsType<ColumnReference>(func.Arguments[0]);
        Assert.Equal("x", col.ColumnName);

        TypeLiteralExpression typeLiteral = Assert.IsType<TypeLiteralExpression>(binary.Right);
        Assert.Equal("Int32", typeLiteral.TypeName);
    }

    [Fact]
    public void IsNotType_DesugarsToTypeofNotEquals()
    {
        SelectStatement result = Parse("SELECT * FROM t WHERE x IS NOT Float64");

        BinaryExpression binary = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.NotEqual, binary.Operator);

        FunctionCallExpression func = Assert.IsType<FunctionCallExpression>(binary.Left);
        Assert.Equal("typeof", func.FunctionName);

        TypeLiteralExpression typeLiteral = Assert.IsType<TypeLiteralExpression>(binary.Right);
        Assert.Equal("Float64", typeLiteral.TypeName);
    }

    [Fact]
    public void IsNull_StillWorks_WithIsType()
    {
        SelectStatement result = Parse("SELECT * FROM t WHERE x IS NULL");

        IsNullExpression isNull = Assert.IsType<IsNullExpression>(result.Where);
        Assert.False(isNull.Negated);
    }

    [Fact]
    public void IsNotNull_StillWorks_WithIsType()
    {
        SelectStatement result = Parse("SELECT * FROM t WHERE x IS NOT NULL");

        IsNullExpression isNull = Assert.IsType<IsNullExpression>(result.Where);
        Assert.True(isNull.Negated);
    }

    // ───────────────────── Type-narrowing bind (x AS Int32 y AND ...) ─────────────────────

    [Fact]
    public void TypeNarrow_DesugarsToCanCastAndCast()
    {
        SelectStatement result = Parse("SELECT * FROM t WHERE x AS Int32 y AND y > 0");

        // Top-level: can_cast(x, Int32) AND CAST(x AS Int32) > 0
        BinaryExpression and = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.And, and.Operator);

        // Left: can_cast(x, Int32)
        FunctionCallExpression canCast = Assert.IsType<FunctionCallExpression>(and.Left);
        Assert.Equal("can_cast", canCast.FunctionName);
        Assert.Equal(2, canCast.Arguments.Count);
        ColumnReference guardCol = Assert.IsType<ColumnReference>(canCast.Arguments[0]);
        Assert.Equal("x", guardCol.ColumnName);
        TypeLiteralExpression guardType = Assert.IsType<TypeLiteralExpression>(canCast.Arguments[1]);
        Assert.Equal("Int32", guardType.TypeName);

        // Right: CAST(x AS Int32) > 0
        BinaryExpression cmp = Assert.IsType<BinaryExpression>(and.Right);
        Assert.Equal(BinaryOperator.GreaterThan, cmp.Operator);
        CastExpression cast = Assert.IsType<CastExpression>(cmp.Left);
        Assert.Equal("Int32", cast.TargetType);
        ColumnReference castCol = Assert.IsType<ColumnReference>(cast.Expression);
        Assert.Equal("x", castCol.ColumnName);
    }

    [Fact]
    public void TypeNarrow_CompoundRight_SubstitutesAllOccurrences()
    {
        SelectStatement result = Parse("SELECT * FROM t WHERE x AS Int32 y AND y > 0 AND y < 100");

        // Should be: typeof(x) = Int32 AND (CAST(x AS Int32) > 0 AND CAST(x AS Int32) < 100)
        BinaryExpression topAnd = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.And, topAnd.Operator);

        // Right side is another AND with both y references substituted
        BinaryExpression innerAnd = Assert.IsType<BinaryExpression>(topAnd.Right);
        Assert.Equal(BinaryOperator.And, innerAnd.Operator);

        BinaryExpression left = Assert.IsType<BinaryExpression>(innerAnd.Left);
        Assert.IsType<CastExpression>(left.Left);

        BinaryExpression right = Assert.IsType<BinaryExpression>(innerAnd.Right);
        Assert.IsType<CastExpression>(right.Left);
    }

    [Fact]
    public void TypeNarrow_InOrBranches_EachBranchIndependent()
    {
        SelectStatement result = Parse(
            "SELECT * FROM t WHERE (x AS Int32 y AND y > 0) OR (x AS String z AND len(z) > 3)");

        BinaryExpression or = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.Or, or.Operator);

        // Left branch: typeof(x) = Int32 AND CAST(x AS Int32) > 0
        BinaryExpression leftAnd = Assert.IsType<BinaryExpression>(or.Left);
        Assert.Equal(BinaryOperator.And, leftAnd.Operator);

        // Right branch: typeof(x) = String AND len(CAST(x AS String)) > 3
        BinaryExpression rightAnd = Assert.IsType<BinaryExpression>(or.Right);
        Assert.Equal(BinaryOperator.And, rightAnd.Operator);

        BinaryExpression lenCmp = Assert.IsType<BinaryExpression>(rightAnd.Right);
        FunctionCallExpression lenCall = Assert.IsType<FunctionCallExpression>(lenCmp.Left);
        Assert.Equal("len", lenCall.FunctionName);
        Assert.IsType<CastExpression>(lenCall.Arguments[0]);
    }

    [Fact]
    public void TypeNarrow_ComplexSource_DuplicatesExpression()
    {
        SelectStatement result = Parse(
            "SELECT * FROM t WHERE json_value(data, '$.x') AS Float64 score AND score > 0.5");

        BinaryExpression and = Assert.IsType<BinaryExpression>(result.Where);

        // Guard: can_cast(json_value(data, '$.x'), Float64)
        FunctionCallExpression canCast = Assert.IsType<FunctionCallExpression>(and.Left);
        Assert.Equal("can_cast", canCast.FunctionName);
        FunctionCallExpression jsonCall = Assert.IsType<FunctionCallExpression>(canCast.Arguments[0]);
        Assert.Equal("json_value", jsonCall.FunctionName);

        // Body: CAST(json_value(data, '$.x') AS Float64) > 0.5
        BinaryExpression cmp = Assert.IsType<BinaryExpression>(and.Right);
        CastExpression cast = Assert.IsType<CastExpression>(cmp.Left);
        FunctionCallExpression castSource = Assert.IsType<FunctionCallExpression>(cast.Expression);
        Assert.Equal("json_value", castSource.FunctionName);
    }

    [Fact]
    public void TypeNarrow_StandardAndStillWorks()
    {
        // Plain AND without narrowing must still parse correctly
        SelectStatement result = Parse("SELECT * FROM t WHERE a > 1 AND b < 2");

        BinaryExpression and = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.And, and.Operator);
    }

    // ───────────────────── Error message quality (deep failures inside subqueries) ─────────────────────

    [Fact]
    public void OffsetWithVariable_InsideSubquery_ParsesSuccessfully()
    {
        // Regression for the originally-reported user issue: `OFFSET @var`
        // inside a subquery used to fail to parse (OFFSET only accepted
        // NumberLiteral) and the error surfaced at `FROM (` rather than at
        // the OFFSET. Now `OFFSET expression` is supported end-to-end and
        // this exact shape parses cleanly. The companion runtime tests in
        // BatchExecutorTests verify the variable resolves correctly.
        const string sql =
            "SELECT * FROM (\n" +
            "  SELECT * FROM t LIMIT 5 OFFSET @offset\n" +
            ") T";

        QueryExpression q = SqlParser.Parse(sql);
        Assert.NotNull(q);
    }

    [Fact]
    public void SubqueryWithMalformedClause_ErrorPointsAtMalformedClauseNotAtFromParen()
    {
        // Same shape as the OFFSET case but with a different malformed
        // inner clause — pins the general behaviour: `(` at table-source
        // position commits to subquery, so any inner failure surfaces at
        // its real position.
        const string sql =
            "SELECT * FROM (\n" +
            "  SELECT * FROM t WHERE\n" + // WHERE without a predicate
            ") T";

        ParseException ex = Assert.Throws<ParseException>(() => SqlParser.Parse(sql));

        // Failure should be on line 2/3 (where WHERE is incomplete), not line 1.
        Assert.DoesNotContain("line 1", ex.Message);
    }

    [Fact]
    public void ValidSubquery_StillParsesAfterTryRemoval()
    {
        // Sanity check: removing `.Try()` from SubquerySourceParser must
        // not break the happy path. A well-formed subquery in FROM still
        // parses cleanly.
        SelectStatement result = Parse(
            "SELECT * FROM (SELECT id, name FROM users WHERE id > 10) AS u");

        Assert.NotNull(result.From);
        Assert.IsType<SubquerySource>(result.From.Source);
    }

    [Fact]
    public void DeclareMissingAtSign_InsideCreateFunctionBody_ErrorPointsAtBadDeclare()
    {
        // Regression for the user-reported issue: `DECLARE x String` (no @)
        // inside a CREATE FUNCTION body produced a misleading "unexpected
        // CREATE at line 1" error. Stock Superpower .Try() discards
        // committed-failure metadata when backtracking; we work around that
        // by factoring CreateFunctionParser into a `.Try()`-protected
        // prefix (CreateFunctionPrefix matches CREATE…FUNCTION and commits)
        // and an unprotected body. Once the prefix matches, body failures
        // propagate with deep Remainder.Position and Superpower's `.Or()`
        // alternation picks the deepest-Remainder branch — surfacing the
        // real failure at the bad DECLARE.
        const string sql =
            "CREATE FUNCTION Test()\n" +
            "RETURNS String\n" +
            "AS BEGIN\n" +
            "  DECLARE x String\n" + // missing @ on x
            "  RETURN 'test'\n" +
            "END";

        ParseException ex = Assert.Throws<ParseException>(() => SqlParser.ParseStatement(sql));

        // The bad DECLARE is on line 4. The error should point there, not
        // at line 1 (the CREATE) or line 3 (the AS BEGIN).
        Assert.DoesNotContain("line 1", ex.Message);
        Assert.DoesNotContain("line 3", ex.Message);
        Assert.Contains("line 4", ex.Message);
    }

    [Fact]
    public void ValidCreateFunction_StillParsesAfterTryPreserveErrorSwap()
    {
        // Sanity check: factoring CreateFunctionParser into prefix+body
        // and dropping the outer .Try() must not break the happy path.
        Statement stmt = SqlParser.ParseStatement(
            "CREATE FUNCTION Test() RETURNS String AS BEGIN " +
            "DECLARE @x String = 'hi'; RETURN @x END");

        Assert.IsType<CreateFunctionStatement>(stmt);
    }

    [Fact]
    public void DeclareMissingAtSign_InsideCreateProcedureBody_ErrorPointsAtBadDeclare()
    {
        // Symmetric to the CREATE FUNCTION case: CREATE PROCEDURE has the
        // same prefix-factored shape so a parse error in its BEGIN…END body
        // surfaces at the bad statement, not at the outer CREATE.
        const string sql =
            "CREATE PROCEDURE Test() AS BEGIN\n" +
            "  DECLARE x String\n" + // missing @ on x — line 2
            "  RETURN\n" +
            "END";

        ParseException ex = Assert.Throws<ParseException>(() => SqlParser.ParseStatement(sql));

        Assert.DoesNotContain("line 1", ex.Message);
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void ValidCreateProcedure_StillParsesAfterPrefixRestructure()
    {
        Statement stmt = SqlParser.ParseStatement(
            "CREATE PROCEDURE Test() AS BEGIN DECLARE @x String = 'hi' END");

        Assert.IsType<CreateProcedureStatement>(stmt);
    }

    // ───────────────────── Concat operator (||) ─────────────────────

    [Fact]
    public void ConcatOperator_DesugarsToConcatFunctionCall()
    {
        SelectStatement result = Parse("SELECT 'a' || 'b' FROM t");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(
            result.Columns[0].Expression);
        Assert.Equal("concat_strict", call.FunctionName);
        Assert.Equal(2, call.Arguments.Count);
        Assert.Equal("a", Assert.IsType<LiteralExpression>(call.Arguments[0]).Value);
        Assert.Equal("b", Assert.IsType<LiteralExpression>(call.Arguments[1]).Value);
    }

    [Fact]
    public void ConcatOperator_LeftAssociative_ChainsAsNestedConcat()
    {
        SelectStatement result = Parse("SELECT 'a' || 'b' || 'c' FROM t");

        // Left-associative: ((a || b) || c) → concat(concat('a','b'), 'c').
        // The variadic concat() flattens at evaluation time.
        FunctionCallExpression outer = Assert.IsType<FunctionCallExpression>(
            result.Columns[0].Expression);
        Assert.Equal("concat_strict", outer.FunctionName);
        FunctionCallExpression inner = Assert.IsType<FunctionCallExpression>(outer.Arguments[0]);
        Assert.Equal("concat_strict", inner.FunctionName);
        Assert.Equal("a", Assert.IsType<LiteralExpression>(inner.Arguments[0]).Value);
        Assert.Equal("b", Assert.IsType<LiteralExpression>(inner.Arguments[1]).Value);
        Assert.Equal("c", Assert.IsType<LiteralExpression>(outer.Arguments[1]).Value);
    }

    [Fact]
    public void ConcatOperator_AcceptsColumnReferences()
    {
        SelectStatement result = Parse("SELECT first_name || ' ' || last_name FROM users");

        FunctionCallExpression outer = Assert.IsType<FunctionCallExpression>(
            result.Columns[0].Expression);
        Assert.Equal("concat_strict", outer.FunctionName);
        FunctionCallExpression inner = Assert.IsType<FunctionCallExpression>(outer.Arguments[0]);
        Assert.Equal("first_name",
            Assert.IsType<ColumnReference>(inner.Arguments[0]).ColumnName);
        Assert.Equal("last_name",
            Assert.IsType<ColumnReference>(outer.Arguments[1]).ColumnName);
    }

    [Fact]
    public void ConcatOperator_BindsAtAdditiveLevel_LowerThanComparison()
    {
        // 'a' || 'b' = 'ab' parses as ('a' || 'b') = 'ab' — same as +/-,
        // higher precedence than comparison.
        SelectStatement result = Parse("SELECT 1 FROM t WHERE 'a' || 'b' = 'ab'");

        BinaryExpression eq = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.Equal, eq.Operator);
        FunctionCallExpression concat = Assert.IsType<FunctionCallExpression>(eq.Left);
        Assert.Equal("concat_strict", concat.FunctionName);
    }
}
