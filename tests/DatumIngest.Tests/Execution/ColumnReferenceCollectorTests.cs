using DatumIngest.Execution;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="ColumnReferenceCollector"/>, verifying that
/// all expression types are traversed and column references are collected.
/// </summary>
public class ColumnReferenceCollectorTests : ServiceTestBase
{
    // ─────────────── Single references ───────────────

    [Fact]
    public void Collect_ColumnReference_ReturnsReference()
    {
        Expression expression = new ColumnReference("t", "id");

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Single(references);
        Assert.Contains(("t", "id"), references);
    }

    [Fact]
    public void Collect_UnqualifiedColumnReference_ReturnsNullTableName()
    {
        Expression expression = new ColumnReference("name");

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Single(references);
        Assert.Contains(((string?)null, "name"), references);
    }

    [Fact]
    public void Collect_Literal_ReturnsEmpty()
    {
        Expression expression = new LiteralExpression(42);

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Empty(references);
    }

    // ─────────────── Composite expressions ───────────────

    [Fact]
    public void Collect_BinaryExpression_CollectsBothSides()
    {
        Expression expression = new BinaryExpression(
            new ColumnReference("a", "x"),
            BinaryOperator.Equal,
            new ColumnReference("b", "y"));

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Equal(2, references.Count);
        Assert.Contains(("a", "x"), references);
        Assert.Contains(("b", "y"), references);
    }

    [Fact]
    public void Collect_UnaryExpression_CollectsOperand()
    {
        Expression expression = new UnaryExpression(
            UnaryOperator.Not,
            new ColumnReference("t", "flag"));

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Single(references);
        Assert.Contains(("t", "flag"), references);
    }

    [Fact]
    public void Collect_FunctionCall_CollectsArguments()
    {
        Expression expression = new FunctionCallExpression(
            "GET_FILENAME",
            [new ColumnReference("z", "file_name")]);

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Single(references);
        Assert.Contains(("z", "file_name"), references);
    }

    [Fact]
    public void Collect_FunctionCallMultipleArguments_CollectsAll()
    {
        Expression expression = new FunctionCallExpression(
            "CONCAT",
            [
                new ColumnReference("t", "first"),
                new ColumnReference("t", "last")
            ]);

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Equal(2, references.Count);
        Assert.Contains(("t", "first"), references);
        Assert.Contains(("t", "last"), references);
    }

    // ─────────────── Predicates ───────────────

    [Fact]
    public void Collect_InExpression_CollectsExpressionAndValues()
    {
        Expression expression = new InExpression(
            new ColumnReference("t", "status"),
            [new LiteralExpression("active"), new ColumnReference("t", "default_status")]);

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Equal(2, references.Count);
        Assert.Contains(("t", "status"), references);
        Assert.Contains(("t", "default_status"), references);
    }

    [Fact]
    public void Collect_BetweenExpression_CollectsAllThreeParts()
    {
        Expression expression = new BetweenExpression(
            new ColumnReference("t", "value"),
            new ColumnReference("t", "low"),
            new ColumnReference("t", "high"));

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Equal(3, references.Count);
    }

    [Fact]
    public void Collect_IsNullExpression_CollectsInnerExpression()
    {
        Expression expression = new IsNullExpression(
            new ColumnReference("t", "nullable_col"));

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Single(references);
        Assert.Contains(("t", "nullable_col"), references);
    }

    [Fact]
    public void Collect_CastExpression_CollectsInnerExpression()
    {
        Expression expression = new CastExpression(
            new ColumnReference("t", "id"), "INT");

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Single(references);
        Assert.Contains(("t", "id"), references);
    }

    // ─────────────── Table alias collection ───────────────

    [Fact]
    public void CollectTableAliases_ReturnsDistinctAliases()
    {
        Expression expression = new BinaryExpression(
            new ColumnReference("a", "x"),
            BinaryOperator.And,
            new BinaryExpression(
                new ColumnReference("a", "y"),
                BinaryOperator.Equal,
                new ColumnReference("b", "z")));

        HashSet<string> aliases = ColumnReferenceCollector.CollectTableAliases(expression);

        Assert.Equal(2, aliases.Count);
        Assert.Contains("a", aliases);
        Assert.Contains("b", aliases);
    }

    [Fact]
    public void CollectTableAliases_ExcludesUnqualifiedReferences()
    {
        Expression expression = new BinaryExpression(
            new ColumnReference("name"),
            BinaryOperator.Like,
            new LiteralExpression("%test%"));

        HashSet<string> aliases = ColumnReferenceCollector.CollectTableAliases(expression);

        Assert.Empty(aliases);
    }

    [Fact]
    public void CollectTableAliases_SingleTable_ReturnsSingleAlias()
    {
        Expression expression = new BinaryExpression(
            new ColumnReference("c", "caption"),
            BinaryOperator.Like,
            new LiteralExpression("%bicycle%"));

        HashSet<string> aliases = ColumnReferenceCollector.CollectTableAliases(expression);

        Assert.Single(aliases);
        Assert.Contains("c", aliases);
    }

    // ─────────────── Nested deep expression ───────────────

    [Fact]
    public void Collect_DeeplyNested_CollectsAll()
    {
        Expression expression = new BinaryExpression(
            new FunctionCallExpression("UPPER", [new ColumnReference("a", "name")]),
            BinaryOperator.Equal,
            new FunctionCallExpression("LOWER",
            [
                new CastExpression(new ColumnReference("b", "label"), "VARCHAR")
            ]));

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Equal(2, references.Count);
        Assert.Contains(("a", "name"), references);
        Assert.Contains(("b", "label"), references);
    }

    // ─────────────── CASE expression ───────────────

    [Fact]
    public void Collect_SearchedCaseExpression_CollectsAllReferences()
    {
        Expression expression = new CaseExpression(
            null,
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("t", "status"),
                        BinaryOperator.Equal,
                        new LiteralExpression(1)),
                    new ColumnReference("t", "label")),
            ],
            new ColumnReference("t", "fallback"));

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Equal(3, references.Count);
        Assert.Contains(("t", "status"), references);
        Assert.Contains(("t", "label"), references);
        Assert.Contains(("t", "fallback"), references);
    }

    [Fact]
    public void Collect_SimpleCaseExpression_IncludesOperand()
    {
        Expression expression = new CaseExpression(
            new ColumnReference("t", "category"),
            [new WhenClause(new LiteralExpression(1), new ColumnReference("t", "name"))],
            null);

        HashSet<(string?, string)> references = ColumnReferenceCollector.Collect(expression);

        Assert.Equal(2, references.Count);
        Assert.Contains(("t", "category"), references);
        Assert.Contains(("t", "name"), references);
    }
}
