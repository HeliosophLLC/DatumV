using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="ExpressionTypeResolver"/> covering all expression kinds.
/// </summary>
public sealed class ExpressionTypeResolverTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    private static readonly Schema TestSchema = new(
    [
        new ColumnInfo("id", DataKind.Float32, nullable: false),
        new ColumnInfo("name", DataKind.String, nullable: true),
        new ColumnInfo("embedding", DataKind.Vector, nullable: false),
        new ColumnInfo("created", DataKind.Date, nullable: false),
        new ColumnInfo("t.qualified_col", DataKind.Float32, nullable: false),
    ]);

    // ───────────────────── Literals ─────────────────────

    [Theory]
    [InlineData(42, DataKind.Int32)]
    [InlineData(42L, DataKind.Int64)]
    [InlineData(3.14f, DataKind.Float32)]
    [InlineData(3.14, DataKind.Float64)]
    [InlineData(true, DataKind.Boolean)]
    public void ResolveLiteral_NumericOrBool_ReturnsScalar(object value, DataKind expected)
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new LiteralExpression(value), TestSchema, DefaultFunctions);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveLiteral_String_ReturnsString()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new LiteralExpression("hello"), TestSchema, DefaultFunctions);

        Assert.Equal(DataKind.String, result);
    }

    [Fact]
    public void ResolveLiteral_Null_ReturnsScalar()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new LiteralExpression(null), TestSchema, DefaultFunctions);

        Assert.Equal(DataKind.Float32, result);
    }

    // ───────────────────── Column references ─────────────────────

    [Fact]
    public void ResolveColumn_Unqualified_ReturnsKind()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new ColumnReference("name"), TestSchema, DefaultFunctions);

        Assert.Equal(DataKind.String, result);
    }

    [Fact]
    public void ResolveColumn_Qualified_ReturnsKind()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new ColumnReference("t", "qualified_col"), TestSchema, DefaultFunctions);

        Assert.Equal(DataKind.Float32, result);
    }

    [Fact]
    public void ResolveColumn_Unknown_ReturnsNull()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new ColumnReference("nonexistent"), TestSchema, DefaultFunctions);

        Assert.Null(result);
    }

    // ───────────────────── Binary expressions ─────────────────────

    [Theory]
    [InlineData(BinaryOperator.Equal)]
    [InlineData(BinaryOperator.NotEqual)]
    [InlineData(BinaryOperator.LessThan)]
    [InlineData(BinaryOperator.GreaterThan)]
    [InlineData(BinaryOperator.LessThanOrEqual)]
    [InlineData(BinaryOperator.GreaterThanOrEqual)]
    [InlineData(BinaryOperator.And)]
    [InlineData(BinaryOperator.Or)]
    [InlineData(BinaryOperator.Like)]
    [InlineData(BinaryOperator.ILike)]
    [InlineData(BinaryOperator.Regexp)]
    public void ResolveBinary_ComparisonOrLogical_ReturnsBoolean(BinaryOperator op)
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new BinaryExpression(new ColumnReference("id"), op, new LiteralExpression(5)),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Boolean, result);
    }

    [Fact]
    public void ResolveBinary_Arithmetic_ReturnsCommonKind()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new BinaryExpression(
                new ColumnReference("id"),
                BinaryOperator.Add,
                new LiteralExpression(10)),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Float32, result);
    }

    // ───────────────────── Unary expressions ─────────────────────

    [Fact]
    public void ResolveUnary_Not_ReturnsBoolean()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new UnaryExpression(UnaryOperator.Not, new ColumnReference("id")),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Boolean, result);
    }

    [Fact]
    public void ResolveUnary_Negate_PreservesOperandKind()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new UnaryExpression(UnaryOperator.Negate, new ColumnReference("id")),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Float32, result);
    }

    // ───────────────────── Function calls ─────────────────────

    [Fact]
    public void ResolveFunction_KnownFunction_ReturnsValidatedKind()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new FunctionCallExpression("abs", [new ColumnReference("id")]),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Float32, result);
    }

    [Fact]
    public void ResolveFunction_VectorReduction_ReturnsScalar()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new FunctionCallExpression("vec_sum", [new ColumnReference("embedding")]),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Float32, result);
    }

    [Fact]
    public void ResolveFunction_UnknownFunction_ReturnsNull()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new FunctionCallExpression("nonexistent_func", [new ColumnReference("id")]),
            TestSchema,
            DefaultFunctions);

        Assert.Null(result);
    }

    // ───────────────────── Special expressions ─────────────────────

    [Fact]
    public void ResolveIn_ReturnsBoolean()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new InExpression(
                new ColumnReference("name"),
                [new LiteralExpression("a"), new LiteralExpression("b")]),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Boolean, result);
    }

    [Fact]
    public void ResolveBetween_ReturnsBoolean()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new BetweenExpression(
                new ColumnReference("id"),
                new LiteralExpression(1),
                new LiteralExpression(100)),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Boolean, result);
    }

    [Fact]
    public void ResolveIsNull_ReturnsBoolean()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new IsNullExpression(new ColumnReference("name")),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Boolean, result);
    }

    [Fact]
    public void ResolveCast_KnownTarget_ReturnsTargetKind()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new CastExpression(new ColumnReference("id"), "UInt8"),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.UInt8, result);
    }

    [Fact]
    public void ResolveCast_UnknownTarget_ReturnsNull()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new CastExpression(new ColumnReference("id"), "InvalidType"),
            TestSchema,
            DefaultFunctions);

        Assert.Null(result);
    }

    // ───────────────────── CASE expressions ─────────────────────

    [Fact]
    public void ResolveCase_AllBranchesScalar_ReturnsScalar()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(true), new LiteralExpression(1))],
                new LiteralExpression(0)),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Int32, result);
    }

    [Fact]
    public void ResolveCase_AllBranchesString_ReturnsString()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new CaseExpression(
                null,
                [
                    new WhenClause(new LiteralExpression(true), new LiteralExpression("a")),
                    new WhenClause(new LiteralExpression(false), new LiteralExpression("b")),
                ],
                new LiteralExpression("c")),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.String, result);
    }

    [Fact]
    public void ResolveCase_WithoutElse_ResolvesFromThenBranches()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(true), new ColumnReference("id"))],
                null),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Float32, result);
    }

    [Fact]
    public void ResolveCase_SimpleCaseWithOperand_ReturnsUnifiedType()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new CaseExpression(
                new ColumnReference("id"),
                [
                    new WhenClause(new LiteralExpression(1), new LiteralExpression("one")),
                    new WhenClause(new LiteralExpression(2), new LiteralExpression("two")),
                ],
                new LiteralExpression("other")),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.String, result);
    }

    // ───────────────────── CASE branch coercion ─────────────────────

    [Fact]
    public void ResolveCase_MixedStringAndScalar_ResolvesToScalar()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(true), new LiteralExpression("0"))],
                new LiteralExpression(1)),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Int32, result);
    }

    [Fact]
    public void ResolveCase_MixedStringAndBoolean_ResolvesToBoolean()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(true), new LiteralExpression("true"))],
                new LiteralExpression(false)),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Boolean, result);
    }

    [Fact]
    public void ResolveCase_StringAndColumnScalar_ResolvesToScalar()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(true), new ColumnReference("id"))],
                new LiteralExpression("fallback")),
            TestSchema,
            DefaultFunctions);

        Assert.Equal(DataKind.Float32, result);
    }

    [Fact]
    public void ResolveCase_IncompatibleNonStringTypes_ReturnsNull()
    {
        DataKind? result = ExpressionTypeResolver.ResolveType(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(true), new ColumnReference("created"))],
                new ColumnReference("id")),
            TestSchema,
            DefaultFunctions);

        Assert.Null(result);
    }
}
