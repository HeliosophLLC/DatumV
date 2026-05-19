using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Parsing;

/// <summary>
/// Parser tests for the CROSS VALIDATE clause:
/// <c>CROSS VALIDATE(k = N [, seed = S]) ON key [STRATIFY BY col] [GROUP BY col] AS alias</c>.
/// </summary>
public sealed class CrossValidateParsingTests : ServiceTestBase
{
    private static SelectStatement Parse(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }

    // ───────────────────── Basic syntax ─────────────────────

    [Fact]
    public void BasicCrossValidate_ParsesKAndAlias()
    {
        SelectStatement result = Parse(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 5) ON id AS fold");

        Assert.NotNull(result.CrossValidate);
        CrossValidateClause cv = result.CrossValidate!;

        LiteralExpression k = Assert.IsType<LiteralExpression>(cv.FoldCount);
        Assert.Equal(5d, Convert.ToDouble(k.Value));
        Assert.Null(cv.Seed);
        Assert.Single(cv.KeyColumns);
        Assert.Equal("id", ((ColumnReference)cv.KeyColumns[0]).ColumnName);
        Assert.Equal("fold", cv.OutputAlias);
        Assert.Null(cv.StratifyColumns);
        Assert.Null(cv.GroupColumns);
    }

    [Fact]
    public void WithSeed_ParsesSeedParameter()
    {
        SelectStatement result = Parse(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 5, seed = 42) ON id AS fold");

        Assert.NotNull(result.CrossValidate);
        CrossValidateClause cv = result.CrossValidate!;

        LiteralExpression k = Assert.IsType<LiteralExpression>(cv.FoldCount);
        Assert.Equal(5d, Convert.ToDouble(k.Value));

        LiteralExpression seed = Assert.IsType<LiteralExpression>(cv.Seed!);
        Assert.Equal(42d, Convert.ToDouble(seed.Value));
    }

    [Fact]
    public void CompositeKey_ParsesMultipleOnColumns()
    {
        SelectStatement result = Parse(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 10, seed = 7) ON (user_id, session_id) AS fold");

        Assert.NotNull(result.CrossValidate);
        CrossValidateClause cv = result.CrossValidate!;

        Assert.Equal(2, cv.KeyColumns.Count);
        Assert.Equal("user_id", ((ColumnReference)cv.KeyColumns[0]).ColumnName);
        Assert.Equal("session_id", ((ColumnReference)cv.KeyColumns[1]).ColumnName);
    }

    // ───────────────────── STRATIFY BY ─────────────────────

    [Fact]
    public void StratifyBy_ParsesStratificationColumn()
    {
        SelectStatement result = Parse(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 5, seed = 42) ON id STRATIFY BY label AS fold");

        Assert.NotNull(result.CrossValidate);
        CrossValidateClause cv = result.CrossValidate!;

        Assert.NotNull(cv.StratifyColumns);
        Assert.Single(cv.StratifyColumns!);
        Assert.Equal("label", ((ColumnReference)cv.StratifyColumns![0]).ColumnName);
    }

    // ───────────────────── GROUP BY ─────────────────────

    [Fact]
    public void GroupBy_ParsesGroupColumn()
    {
        SelectStatement result = Parse(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 5) ON patient_id GROUP BY patient_id AS fold");

        Assert.NotNull(result.CrossValidate);
        CrossValidateClause cv = result.CrossValidate!;

        Assert.NotNull(cv.GroupColumns);
        Assert.Single(cv.GroupColumns!);
        Assert.Equal("patient_id", ((ColumnReference)cv.GroupColumns![0]).ColumnName);
    }

    // ───────────────────── Case insensitivity ─────────────────────

    [Theory]
    [InlineData("validate")]
    [InlineData("VALIDATE")]
    [InlineData("Validate")]
    public void Validate_IsCaseInsensitive(string validate)
    {
        SelectStatement result = Parse(
            $"SELECT *, fold FROM data CROSS {validate}(k = 5) ON id AS fold");

        Assert.NotNull(result.CrossValidate);
    }

    // ───────────────────── Combined with other clauses ─────────────────────

    [Fact]
    public void WithOrderByAndLimit_ParsesCorrectly()
    {
        SelectStatement result = Parse(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 5) ON id AS fold ORDER BY fold LIMIT 100");

        Assert.NotNull(result.CrossValidate);
        Assert.NotNull(result.OrderBy);
        Assert.Equal(100, Convert.ToInt32(((LiteralExpression)result.Limit!).Value));
    }

    [Fact]
    public void WithWhere_ParsesCorrectly()
    {
        SelectStatement result = Parse(
            "SELECT *, fold FROM data WHERE is_valid = 1 CROSS VALIDATE(k = 5) ON id AS fold");

        Assert.NotNull(result.CrossValidate);
        Assert.NotNull(result.Where);
    }

    // ───────────────────── No interference with CROSS JOIN ─────────────────────

    [Fact]
    public void CrossJoin_StillParsesCorrectly()
    {
        SelectStatement result = Parse(
            "SELECT * FROM a CROSS JOIN b");

        Assert.Null(result.CrossValidate);
        Assert.NotNull(result.Joins);
        Assert.Single(result.Joins!);
        Assert.Equal(JoinType.Cross, result.Joins![0].Type);
    }

    [Fact]
    public void CrossJoinWithCrossValidate_ParsesBoth()
    {
        SelectStatement result = Parse(
            "SELECT *, fold FROM a CROSS JOIN b ON a.id = b.id CROSS VALIDATE(k = 3) ON a.id AS fold");

        Assert.NotNull(result.Joins);
        Assert.NotNull(result.CrossValidate);
        Assert.Equal("fold", result.CrossValidate!.OutputAlias);
    }

    // ───────────────────── Without CROSS VALIDATE ─────────────────────

    [Fact]
    public void NoCrossValidate_ParsesWithNull()
    {
        SelectStatement result = Parse("SELECT * FROM data");

        Assert.Null(result.CrossValidate);
    }

    // ───────────────────── Error cases ─────────────────────

    [Fact]
    public void MissingAs_Fails()
    {
        Assert.ThrowsAny<Exception>(() => Parse(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 5) ON id"));
    }

    [Fact]
    public void MissingOn_Fails()
    {
        Assert.ThrowsAny<Exception>(() => Parse(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 5) AS fold"));
    }
}
