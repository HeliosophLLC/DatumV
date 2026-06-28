namespace Heliosoph.DatumV.Tests.Execution;

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

/// <summary>
/// Regression: LIKE/ILIKE/REGEXP must bind tighter than AND/OR. A predicate
/// like <c>x LIKE 'a' OR x LIKE 'b'</c> previously misparsed as
/// <c>x LIKE ('a' OR x LIKE 'b')</c> — the pattern operand greedily consumed
/// the trailing boolean operator — which surfaced at runtime as
/// "LIKE requires string operands." (the pattern resolved to a Boolean).
/// </summary>
public sealed class LikePrecedenceTests : ServiceTestBase
{
    private static ModelCatalog BuildCatalogWithEcho()
    {
        ModelCatalog catalog = new(modelDirectory: System.IO.Path.GetTempPath());
        catalog.Register(new ModelCatalogEntry(
            Name: "echo",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => EchoModel.Instance,
            OptionalArgKinds: null));
        return catalog;
    }

    [Fact]
    public void LikeBindsTighterThanOr()
    {
        SelectStatement stmt = SqlParser.Parse(
            "SELECT a FROM t WHERE name LIKE '%x%' OR name LIKE '%y%'")
            is SelectQueryExpression sqe ? sqe.Statement : throw new System.Exception("not a select");

        // Top of the predicate must be OR, with a LIKE on each side.
        BinaryExpression or = Assert.IsType<BinaryExpression>(stmt.Where);
        Assert.Equal(BinaryOperator.Or, or.Operator);
        Assert.Equal(BinaryOperator.Like, Assert.IsType<BinaryExpression>(or.Left).Operator);
        Assert.Equal(BinaryOperator.Like, Assert.IsType<BinaryExpression>(or.Right).Operator);
    }

    [Fact]
    public void LikeBindsTighterThanAnd()
    {
        SelectStatement stmt = SqlParser.Parse(
            "SELECT a FROM t WHERE name LIKE '%x%' AND age > 3")
            is SelectQueryExpression sqe ? sqe.Statement : throw new System.Exception("not a select");

        BinaryExpression and = Assert.IsType<BinaryExpression>(stmt.Where);
        Assert.Equal(BinaryOperator.And, and.Operator);
        Assert.Equal(BinaryOperator.Like, Assert.IsType<BinaryExpression>(and.Left).Operator);
    }

    [Fact]
    public void LikePatternStillAllowsConcatenation()
    {
        // `||` (concat) binds tighter than LIKE's pattern boundary, so a
        // concatenated pattern is still a single operand.
        SelectStatement stmt = SqlParser.Parse(
            "SELECT a FROM t WHERE name LIKE '%' || suffix")
            is SelectQueryExpression sqe ? sqe.Statement : throw new System.Exception("not a select");

        BinaryExpression like = Assert.IsType<BinaryExpression>(stmt.Where);
        Assert.Equal(BinaryOperator.Like, like.Operator);
        // Right operand is the concat desugar, not a bare literal.
        Assert.IsType<FunctionCallExpression>(like.Right);
    }

    /// <summary>
    /// End-to-end against the documented model-card example shape: a subquery
    /// projects a String-returning model call as <c>caption</c>, an outer
    /// <c>WHERE caption LIKE ... OR caption LIKE ...</c> filters over it.
    /// </summary>
    [Fact]
    public async Task SubqueryModelCaption_OuterLikeOrFilter()
    {
        // file = Int32 stands in for a non-String Image column; the misparse
        // used to bind caption's LIKE against a Boolean and throw.
        TableCatalog catalog = CreateCatalog(
            tableName: "coco",
            columns: ["file_name", "file"],
            new object?[] { "a person walking", 1 },
            new object?[] { "a cat sleeping", 2 },
            new object?[] { "a man riding", 3 });
        catalog.Models = BuildCatalogWithEcho();

        const string sql = """
            SELECT file_name, file AS image, caption
            FROM (
                SELECT file_name, file, models.echo(file_name) AS caption
                FROM coco
                LIMIT 10
            ) t
            WHERE caption LIKE '%person%'
               OR caption LIKE '%man%'
            """;

        List<Row> rows = await ExecuteQueryAsync(sql, catalog);

        // "a person walking" and "a man riding" match; "a cat sleeping" doesn't.
        Assert.Equal(2, rows.Count);
        Arena scratch = catalog.Pool.Backing.RentArena();
        HashSet<string> captions = rows.Select(r =>
        {
            r.TryGetValue("caption", out DataValue dv);
            return dv.AsString(scratch);
        }).ToHashSet();
        Assert.Contains("a person walking", captions);
        Assert.Contains("a man riding", captions);
    }
}
