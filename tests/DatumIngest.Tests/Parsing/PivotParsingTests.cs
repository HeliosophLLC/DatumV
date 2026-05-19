using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Parsing;

/// <summary>
/// Parser tests for PIVOT and UNPIVOT clauses.
/// </summary>
public class PivotParsingTests : ServiceTestBase
{
    private static SelectStatement Parse(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }

    // ───────────────────── PIVOT ─────────────────────

    [Fact]
    public void PivotBasicAggregateWithExplicitValueList()
    {
        SelectStatement result = Parse(
            "SELECT * FROM sales PIVOT (SUM(amount) FOR region IN ('North', 'South'))");

        Assert.NotNull(result.Pivot);
        PivotClause pivot = result.Pivot!;

        Assert.Single(pivot.Aggregates);
        FunctionCallExpression aggregate = pivot.Aggregates[0];
        Assert.Equal("SUM", aggregate.FunctionName, StringComparer.OrdinalIgnoreCase);
        Assert.Single(aggregate.Arguments);
        ColumnReference aggregateArgument = Assert.IsType<ColumnReference>(aggregate.Arguments[0]);
        Assert.Equal("amount", aggregateArgument.ColumnName);

        Assert.Equal("region", pivot.PivotColumn.ColumnName);
        Assert.Null(pivot.PivotColumn.TableName);

        Assert.NotNull(pivot.ValueList);
        Assert.Equal(2, pivot.ValueList!.Count);
        LiteralExpression north = Assert.IsType<LiteralExpression>(pivot.ValueList[0]);
        Assert.Equal("North", north.Value);
        LiteralExpression south = Assert.IsType<LiteralExpression>(pivot.ValueList[1]);
        Assert.Equal("South", south.Value);

        Assert.Null(pivot.Alias);
    }

    [Fact]
    public void PivotWithAutoDiscoverValueList()
    {
        SelectStatement result = Parse(
            "SELECT * FROM sales PIVOT (SUM(amount) FOR region)");

        Assert.NotNull(result.Pivot);
        Assert.Null(result.Pivot!.ValueList);
    }

    [Fact]
    public void PivotWithAlias()
    {
        SelectStatement result = Parse(
            "SELECT * FROM sales PIVOT (SUM(amount) FOR region IN ('East', 'West')) AS p");

        Assert.NotNull(result.Pivot);
        Assert.Equal("p", result.Pivot!.Alias);
    }

    [Fact]
    public void PivotWithMultipleAggregates()
    {
        SelectStatement result = Parse(
            "SELECT * FROM sales PIVOT (SUM(amount), COUNT(id) FOR region IN ('North'))");

        Assert.NotNull(result.Pivot);
        Assert.Equal(2, result.Pivot!.Aggregates.Count);
        Assert.Equal("SUM", result.Pivot.Aggregates[0].FunctionName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("COUNT", result.Pivot.Aggregates[1].FunctionName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void PivotWithNumericValueList()
    {
        SelectStatement result = Parse(
            "SELECT * FROM scores PIVOT (AVG(score) FOR quarter IN (1, 2, 3, 4))");

        Assert.NotNull(result.Pivot);
        Assert.NotNull(result.Pivot!.ValueList);
        Assert.Equal(4, result.Pivot.ValueList!.Count);
    }

    [Fact]
    public void PivotLeavesOtherClausesUnaffected()
    {
        SelectStatement result = Parse(
            "SELECT * FROM sales WHERE year = 2024 PIVOT (SUM(amount) FOR region)");

        Assert.NotNull(result.Where);
        Assert.NotNull(result.Pivot);
        Assert.Null(result.Unpivot);
    }

    // ───────────────────── UNPIVOT ─────────────────────

    [Fact]
    public void UnpivotBasic()
    {
        SelectStatement result = Parse(
            "SELECT * FROM wide UNPIVOT (value FOR col_name IN (north, south, east))");

        Assert.NotNull(result.Unpivot);
        UnpivotClause unpivot = result.Unpivot!;

        Assert.Equal("value", unpivot.ValueColumnName);
        Assert.Equal("col_name", unpivot.NameColumnName);

        Assert.Equal(3, unpivot.SourceColumns.Count);
        Assert.Equal("north", unpivot.SourceColumns[0].ColumnName);
        Assert.Equal("south", unpivot.SourceColumns[1].ColumnName);
        Assert.Equal("east", unpivot.SourceColumns[2].ColumnName);

        Assert.False(unpivot.IncludeNulls);
        Assert.Null(unpivot.Alias);
    }

    [Fact]
    public void UnpivotIncludeNulls()
    {
        SelectStatement result = Parse(
            "SELECT * FROM wide UNPIVOT INCLUDE NULLS (value FOR col_name IN (a, b))");

        Assert.NotNull(result.Unpivot);
        Assert.True(result.Unpivot!.IncludeNulls);
    }

    [Fact]
    public void UnpivotWithAlias()
    {
        SelectStatement result = Parse(
            "SELECT * FROM wide UNPIVOT (val FOR nm IN (x, y)) AS u");

        Assert.NotNull(result.Unpivot);
        Assert.Equal("u", result.Unpivot!.Alias);
    }

    [Fact]
    public void UnpivotSingleSourceColumn()
    {
        SelectStatement result = Parse(
            "SELECT * FROM t UNPIVOT (v FOR n IN (col1))");

        Assert.NotNull(result.Unpivot);
        Assert.Single(result.Unpivot!.SourceColumns);
        Assert.Equal("col1", result.Unpivot.SourceColumns[0].ColumnName);
    }

    [Fact]
    public void UnpivotLeavesOtherClausesUnaffected()
    {
        SelectStatement result = Parse(
            "SELECT * FROM wide UNPIVOT (v FOR n IN (a, b)) ORDER BY n");

        Assert.NotNull(result.Unpivot);
        Assert.NotNull(result.OrderBy);
        Assert.Null(result.Pivot);
    }

    // ───────────────────── Error recovery ─────────────────────

    [Fact]
    public void NoPivotOrUnpivotWhenAbsent()
    {
        SelectStatement result = Parse("SELECT a, b FROM t WHERE x = 1");

        Assert.Null(result.Pivot);
        Assert.Null(result.Unpivot);
    }
}
