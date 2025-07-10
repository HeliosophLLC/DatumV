using DatumQuery.Execution;
using DatumQuery.Parsing.Ast;

namespace DatumQuery.Tests.Execution;

/// <summary>
/// Tests for <see cref="JoinKeyExtractor"/>, verifying extraction of
/// equi-join key pairs and residual conditions from ON expressions.
/// </summary>
public class JoinKeyExtractorTests
{
    // ─────────────── Null and empty ───────────────

    [Fact]
    public void TryExtract_NullCondition_ReturnsNull()
    {
        JoinKeyExtractionResult? result = JoinKeyExtractor.TryExtract(null);

        Assert.Null(result);
    }

    // ─────────────── Single column = column ───────────────

    [Fact]
    public void TryExtract_SimpleColumnEquality_ReturnsSingleKeyPair()
    {
        Expression condition = new BinaryExpression(
            new ColumnReference("a", "id"),
            BinaryOperator.Equal,
            new ColumnReference("b", "id"));

        JoinKeyExtractionResult? result = JoinKeyExtractor.TryExtract(condition);

        Assert.NotNull(result);
        Assert.Single(result.KeyPairs);
        Assert.IsType<ColumnReference>(result.KeyPairs[0].Left);
        Assert.IsType<ColumnReference>(result.KeyPairs[0].Right);
        Assert.Null(result.Residual);
    }

    // ─────────────── Function call = column (expression key) ───────────────

    [Fact]
    public void TryExtract_FunctionEquality_ReturnsSingleKeyPair()
    {
        Expression condition = new BinaryExpression(
            new FunctionCallExpression("GET_FILENAME", [new ColumnReference("z", "file_name")]),
            BinaryOperator.Equal,
            new ColumnReference("i", "file_name"));

        JoinKeyExtractionResult? result = JoinKeyExtractor.TryExtract(condition);

        Assert.NotNull(result);
        Assert.Single(result.KeyPairs);
        Assert.IsType<FunctionCallExpression>(result.KeyPairs[0].Left);
        Assert.IsType<ColumnReference>(result.KeyPairs[0].Right);
        Assert.Null(result.Residual);
    }

    // ─────────────── Compound AND keys ───────────────

    [Fact]
    public void TryExtract_CompoundAndKeys_ReturnsMultipleKeyPairs()
    {
        Expression condition = new BinaryExpression(
            new BinaryExpression(
                new ColumnReference("a", "x"),
                BinaryOperator.Equal,
                new ColumnReference("b", "x")),
            BinaryOperator.And,
            new BinaryExpression(
                new ColumnReference("a", "y"),
                BinaryOperator.Equal,
                new ColumnReference("b", "y")));

        JoinKeyExtractionResult? result = JoinKeyExtractor.TryExtract(condition);

        Assert.NotNull(result);
        Assert.Equal(2, result.KeyPairs.Count);
        Assert.Null(result.Residual);
    }

    [Fact]
    public void TryExtract_TripleAndKeys_ReturnsThreeKeyPairs()
    {
        // a.x = b.x AND a.y = b.y AND a.z = b.z
        Expression condition = new BinaryExpression(
            new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("a", "x"),
                    BinaryOperator.Equal,
                    new ColumnReference("b", "x")),
                BinaryOperator.And,
                new BinaryExpression(
                    new ColumnReference("a", "y"),
                    BinaryOperator.Equal,
                    new ColumnReference("b", "y"))),
            BinaryOperator.And,
            new BinaryExpression(
                new ColumnReference("a", "z"),
                BinaryOperator.Equal,
                new ColumnReference("b", "z")));

        JoinKeyExtractionResult? result = JoinKeyExtractor.TryExtract(condition);

        Assert.NotNull(result);
        Assert.Equal(3, result.KeyPairs.Count);
        Assert.Null(result.Residual);
    }

    // ─────────────── Non-equi conditions ───────────────

    [Fact]
    public void TryExtract_PureGreaterThan_ReturnsNull()
    {
        Expression condition = new BinaryExpression(
            new ColumnReference("a", "x"),
            BinaryOperator.GreaterThan,
            new ColumnReference("b", "x"));

        JoinKeyExtractionResult? result = JoinKeyExtractor.TryExtract(condition);

        Assert.Null(result);
    }

    [Fact]
    public void TryExtract_PureLike_ReturnsNull()
    {
        Expression condition = new BinaryExpression(
            new ColumnReference("a", "name"),
            BinaryOperator.Like,
            new LiteralExpression("%test%"));

        JoinKeyExtractionResult? result = JoinKeyExtractor.TryExtract(condition);

        Assert.Null(result);
    }

    // ─────────────── Mixed equi + non-equi → keys + residual ───────────────

    [Fact]
    public void TryExtract_MixedEquiAndGreaterThan_ReturnsKeyAndResidual()
    {
        // a.x = b.x AND a.y > b.y
        Expression condition = new BinaryExpression(
            new BinaryExpression(
                new ColumnReference("a", "x"),
                BinaryOperator.Equal,
                new ColumnReference("b", "x")),
            BinaryOperator.And,
            new BinaryExpression(
                new ColumnReference("a", "y"),
                BinaryOperator.GreaterThan,
                new ColumnReference("b", "y")));

        JoinKeyExtractionResult? result = JoinKeyExtractor.TryExtract(condition);

        Assert.NotNull(result);
        Assert.Single(result.KeyPairs);
        Assert.NotNull(result.Residual);
        Assert.IsType<BinaryExpression>(result.Residual);

        BinaryExpression residual = (BinaryExpression)result.Residual;
        Assert.Equal(BinaryOperator.GreaterThan, residual.Operator);
    }

    [Fact]
    public void TryExtract_TwoEquiOneLike_ReturnsTwoKeysAndResidual()
    {
        // a.x = b.x AND a.y = b.y AND a.name LIKE '%test%'
        Expression condition = new BinaryExpression(
            new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("a", "x"),
                    BinaryOperator.Equal,
                    new ColumnReference("b", "x")),
                BinaryOperator.And,
                new BinaryExpression(
                    new ColumnReference("a", "y"),
                    BinaryOperator.Equal,
                    new ColumnReference("b", "y"))),
            BinaryOperator.And,
            new BinaryExpression(
                new ColumnReference("a", "name"),
                BinaryOperator.Like,
                new LiteralExpression("%test%")));

        JoinKeyExtractionResult? result = JoinKeyExtractor.TryExtract(condition);

        Assert.NotNull(result);
        Assert.Equal(2, result.KeyPairs.Count);
        Assert.NotNull(result.Residual);
    }

    // ─────────────── CAST expression in key ───────────────

    [Fact]
    public void TryExtract_CastEquality_ReturnsSingleKeyPair()
    {
        Expression condition = new BinaryExpression(
            new CastExpression(new ColumnReference("a", "id"), "INT"),
            BinaryOperator.Equal,
            new ColumnReference("b", "id"));

        JoinKeyExtractionResult? result = JoinKeyExtractor.TryExtract(condition);

        Assert.NotNull(result);
        Assert.Single(result.KeyPairs);
        Assert.IsType<CastExpression>(result.KeyPairs[0].Left);
        Assert.Null(result.Residual);
    }

    // ─────────────── OR condition is not decomposed ───────────────

    [Fact]
    public void TryExtract_OrCondition_TreatedAsSingleEquality()
    {
        // (a.x = b.x OR a.y = b.y) — OR is not flattened, so this is
        // not an equality at the top level → returns null.
        Expression condition = new BinaryExpression(
            new BinaryExpression(
                new ColumnReference("a", "x"),
                BinaryOperator.Equal,
                new ColumnReference("b", "x")),
            BinaryOperator.Or,
            new BinaryExpression(
                new ColumnReference("a", "y"),
                BinaryOperator.Equal,
                new ColumnReference("b", "y")));

        JoinKeyExtractionResult? result = JoinKeyExtractor.TryExtract(condition);

        Assert.Null(result);
    }
}
