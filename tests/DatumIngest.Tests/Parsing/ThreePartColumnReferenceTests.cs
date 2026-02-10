using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Parser tests for S6: three-part column references —
/// <c>schema.table.column</c> and <c>schema.table.*</c>. Two-part and
/// bare-column references should still parse identically to before.
/// </summary>
public class ThreePartColumnReferenceTests : ServiceTestBase
{
    private static ColumnReference ParseFirstColumn(string sql)
    {
        QueryExpression query = SqlParser.Parse(sql);
        SelectStatement select = ((SelectQueryExpression)query).Statement;
        return Assert.IsType<ColumnReference>(select.Columns[0].Expression);
    }

    [Fact]
    public void BareColumnReference_LeavesQualifiersNull()
    {
        ColumnReference col = ParseFirstColumn("SELECT id FROM t");

        Assert.Null(col.SchemaName);
        Assert.Null(col.TableName);
        Assert.Equal("id", col.ColumnName);
    }

    [Fact]
    public void TwoPart_TableColumn_LeavesSchemaNull()
    {
        ColumnReference col = ParseFirstColumn("SELECT t.id FROM t");

        Assert.Null(col.SchemaName);
        Assert.Equal("t", col.TableName);
        Assert.Equal("id", col.ColumnName);
    }

    [Fact]
    public void ThreePart_SchemaTableColumn_SplitsAllThree()
    {
        ColumnReference col = ParseFirstColumn("SELECT public.users.id FROM public.users");

        Assert.Equal("public", col.SchemaName);
        Assert.Equal("users", col.TableName);
        Assert.Equal("id", col.ColumnName);
    }

    [Fact]
    public void ThreePart_StarColumn_ParsesAsThirdSegmentWildcard()
    {
        ColumnReference col = ParseFirstColumn("SELECT public.users.* FROM public.users");

        Assert.Equal("public", col.SchemaName);
        Assert.Equal("users", col.TableName);
        Assert.Equal("*", col.ColumnName);
    }

    [Fact]
    public void TwoPart_StarColumn_StillParses()
    {
        ColumnReference col = ParseFirstColumn("SELECT t.* FROM t");

        Assert.Null(col.SchemaName);
        Assert.Equal("t", col.TableName);
        Assert.Equal("*", col.ColumnName);
    }
}
