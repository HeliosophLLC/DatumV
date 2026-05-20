using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Functions;

/// <summary>
/// Tests for <see cref="ParameterCheckWalker"/>. Each test parses a SQL CHECK
/// expression through the engine's expression parser and asserts the
/// canonicalised <see cref="ParameterCheck"/> shape matches the expected
/// typed subclass. Direct-construction tests are in <c>ParameterCheckTests</c>;
/// these focus on the AST → typed-shape mapping.
/// </summary>
public sealed class ParameterCheckWalkerTests
{
    private static Expression Parse(string source)
    {
        // Wrap in a SELECT so the existing expression parser handles it; we
        // pull the projected expression back out.
        SelectQueryExpression query = (SelectQueryExpression)SqlParser.Parse($"SELECT {source}");
        return query.Statement.Columns[0].Expression;
    }

    [Fact]
    public void Between_CanonicalisesToBetweenCheck()
    {
        Expression expr = Parse("threshold BETWEEN 0 AND 1");
        ParameterCheck check = ParameterCheckWalker.Canonicalise(expr, "threshold");

        BetweenCheck between = Assert.IsType<BetweenCheck>(check);
        Assert.Equal(0m, between.Min);
        Assert.Equal(1m, between.Max);
    }

    [Fact]
    public void BetweenWithFloatLiterals_PromotesToDecimalExactly()
    {
        Expression expr = Parse("threshold BETWEEN 0.0 AND 1.0");
        ParameterCheck check = ParameterCheckWalker.Canonicalise(expr, "threshold");

        BetweenCheck between = Assert.IsType<BetweenCheck>(check);
        Assert.Equal(0.0m, between.Min);
        Assert.Equal(1.0m, between.Max);
    }

    [Fact]
    public void Conjunction_OfTwoComparisons_CanonicalisesToBetween()
    {
        // (x >= 0) AND (x <= 1) should collapse to BetweenCheck(0, 1).
        Expression expr = Parse("threshold >= 0 AND threshold <= 1");
        ParameterCheck check = ParameterCheckWalker.Canonicalise(expr, "threshold");

        BetweenCheck between = Assert.IsType<BetweenCheck>(check);
        Assert.Equal(0m, between.Min);
        Assert.Equal(1m, between.Max);
    }

    [Fact]
    public void GreaterThan_ProducesGreaterThanCheck_Strict()
    {
        Expression expr = Parse("count > 0");
        ParameterCheck check = ParameterCheckWalker.Canonicalise(expr, "count");

        GreaterThanCheck gt = Assert.IsType<GreaterThanCheck>(check);
        Assert.Equal(0m, gt.Min);
        Assert.False(gt.Inclusive);
    }

    [Fact]
    public void GreaterThanOrEqual_ProducesGreaterThanCheck_Inclusive()
    {
        Expression expr = Parse("count >= 0");
        ParameterCheck check = ParameterCheckWalker.Canonicalise(expr, "count");

        GreaterThanCheck gt = Assert.IsType<GreaterThanCheck>(check);
        Assert.Equal(0m, gt.Min);
        Assert.True(gt.Inclusive);
    }

    [Fact]
    public void OperandsReversed_StillRecognised()
    {
        // `0 < threshold` is equivalent to `threshold > 0`; the walker
        // should canonicalise either form to the same GreaterThanCheck.
        Expression expr = Parse("0 < threshold");
        ParameterCheck check = ParameterCheckWalker.Canonicalise(expr, "threshold");

        GreaterThanCheck gt = Assert.IsType<GreaterThanCheck>(check);
        Assert.Equal(0m, gt.Min);
        Assert.False(gt.Inclusive);
    }

    [Fact]
    public void LessThan_ProducesLessThanCheck()
    {
        Expression expr = Parse("size < 1024");
        ParameterCheck check = ParameterCheckWalker.Canonicalise(expr, "size");

        LessThanCheck lt = Assert.IsType<LessThanCheck>(check);
        Assert.Equal(1024m, lt.Max);
        Assert.False(lt.Inclusive);
    }

    [Fact]
    public void In_StringValues_ProducesInCheck()
    {
        Expression expr = Parse("variant IN ('small', 'medium', 'large')");
        ParameterCheck check = ParameterCheckWalker.Canonicalise(expr, "variant");

        InCheck inCheck = Assert.IsType<InCheck>(check);
        Assert.Equal(new[] { "small", "medium", "large" }, inCheck.Values);
    }

    [Fact]
    public void In_IntegerValues_StringifiedThroughInvariantCulture()
    {
        Expression expr = Parse("target_size IN (416, 640)");
        ParameterCheck check = ParameterCheckWalker.Canonicalise(expr, "target_size");

        InCheck inCheck = Assert.IsType<InCheck>(check);
        Assert.Equal(new[] { "416", "640" }, inCheck.Values);
    }

    [Fact]
    public void UnrecognisedShape_FallsBackToCustomCheck()
    {
        // An equality with both operands non-trivial doesn't fit any canonical
        // shape — must land as CustomCheck so the original Expression is
        // preserved for the SQL evaluator path (Slice D follow-up).
        Expression expr = Parse("threshold = threshold * 2");
        ParameterCheck check = ParameterCheckWalker.Canonicalise(expr, "threshold");

        CustomCheck custom = Assert.IsType<CustomCheck>(check);
        Assert.NotNull(custom.Expr);
    }

    [Fact]
    public void NegativeLiteral_ParsesViaUnaryMinus()
    {
        // The parser wraps negative literals in a UnaryExpression(Negate);
        // the walker must fold through the unary minus so `BETWEEN -1 AND 1`
        // produces BetweenCheck(-1, 1) and not CustomCheck.
        Expression expr = Parse("delta BETWEEN -1 AND 1");
        ParameterCheck check = ParameterCheckWalker.Canonicalise(expr, "delta");

        BetweenCheck between = Assert.IsType<BetweenCheck>(check);
        Assert.Equal(-1m, between.Min);
        Assert.Equal(1m, between.Max);
    }

    [Fact]
    public void ParamNameIsCaseInsensitive()
    {
        // SQL identifiers are case-insensitive; the walker must accept the
        // parameter name in any case.
        Expression expr = Parse("Threshold BETWEEN 0 AND 1");
        ParameterCheck check = ParameterCheckWalker.Canonicalise(expr, "threshold");

        Assert.IsType<BetweenCheck>(check);
    }
}
