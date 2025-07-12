using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

public class SqlParserTests
{
    private static SelectStatement Parse(string sql)
    {
        return SqlParser.Parse(sql);
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
    public void SelectTableStar()
    {
        SelectStatement result = Parse("SELECT t.* FROM t");

        Assert.Single(result.Columns);
        SelectTableColumns tableColumns = Assert.IsType<SelectTableColumns>(result.Columns[0]);
        Assert.Equal("t", tableColumns.TableName);
    }

    [Fact]
    public void SelectWithAlias()
    {
        SelectStatement result = Parse("SELECT name AS n FROM users");

        Assert.Single(result.Columns);
        Assert.Equal("n", result.Columns[0].Alias);
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
        Assert.Equal(42.0, literal.Value);
    }

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
        SelectStatement result = Parse("SELECT normalize(x, 0, 255) AS norm FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(
            result.Columns[0].Expression);
        Assert.Equal("normalize", function.FunctionName);
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
        Assert.Equal(5.0, right.Value);
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

        TableReference from = Assert.IsType<TableReference>(result.From.Source);
        Assert.Equal("t1", from.Name);
        Assert.Equal("x", from.Alias);

        Assert.NotNull(result.Joins);
        TableReference joined = Assert.IsType<TableReference>(result.Joins[0].Source);
        Assert.Equal("t2", joined.Name);
        Assert.Equal("y", joined.Alias);
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

        Assert.Equal(10, result.Limit);
        Assert.Null(result.Offset);
    }

    [Fact]
    public void LimitWithOffset()
    {
        SelectStatement result = Parse(
            "SELECT a FROM t LIMIT 10 OFFSET 20");

        Assert.Equal(10, result.Limit);
        Assert.Equal(20, result.Offset);
    }

    // ───────────────────── Subqueries ─────────────────────

    [Fact]
    public void SubqueryInFrom()
    {
        SelectStatement result = Parse(
            "SELECT name FROM (SELECT name, age FROM users) AS sub");

        SubquerySource subquery = Assert.IsType<SubquerySource>(result.From.Source);
        Assert.Equal("sub", subquery.Alias);
        Assert.Equal(2, subquery.Query.Columns.Count);
    }

    [Fact]
    public void NestedSubqueries()
    {
        SelectStatement result = Parse(
            "SELECT x FROM (SELECT y FROM (SELECT z FROM t) AS inner_q) AS outer_q");

        SubquerySource outer = Assert.IsType<SubquerySource>(result.From.Source);
        Assert.Equal("outer_q", outer.Alias);

        SubquerySource inner = Assert.IsType<SubquerySource>(outer.Query.From.Source);
        Assert.Equal("inner_q", inner.Alias);
    }

    // ───────────────────── Function source in FROM ─────────────────────

    [Fact]
    public void FunctionSourceInFrom()
    {
        SelectStatement result = Parse(
            "SELECT x FROM RANGE(0, 360) AS r");

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

        FunctionSource source = Assert.IsType<FunctionSource>(result.From.Source);
        Assert.Equal("RANGE", source.FunctionName);
        Assert.Null(source.Alias);
    }

    [Fact]
    public void FunctionSourceWithThreeArguments()
    {
        SelectStatement result = Parse(
            "SELECT x FROM RANGE(0, 360, 0.5) AS r");

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

        Assert.IsType<TableReference>(result.From.Source);
        Assert.NotNull(result.Joins);
        Assert.Single(result.Joins);
        Assert.Equal(JoinType.Cross, result.Joins[0].Type);
        FunctionSource joined = Assert.IsType<FunctionSource>(result.Joins[0].Source);
        Assert.Equal("RANGE", joined.FunctionName);
        Assert.Equal("r", joined.Alias);
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
        SelectStatement result = Parse("SELECT CAST(x AS Scalar) FROM t");

        CastExpression cast = Assert.IsType<CastExpression>(result.Columns[0].Expression);
        Assert.Equal("Scalar", cast.TargetType);
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
        Assert.Equal(100, result.Limit);
        Assert.Equal(0, result.Offset);
    }

    [Fact]
    public void FromTableAlias()
    {
        SelectStatement result = Parse("SELECT a FROM users AS u");

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
}
