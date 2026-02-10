using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// S8: parser surface for schema-qualified DML and index DDL. INSERT /
/// UPDATE / DELETE / REINDEX / ANALYZE / CREATE INDEX all accept
/// <c>schema.table</c> targets in S8 — bringing them in line with the
/// schema-qualified <c>CREATE TABLE</c> / <c>DROP TABLE</c> /
/// <c>ALTER TABLE</c> support that landed in S3.
/// </summary>
public class QualifiedDmlParsingTests : ServiceTestBase
{
    [Fact]
    public void Insert_SchemaQualified_SplitsSchemaAndName()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO myapp.users (id) VALUES (1)");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.Equal("myapp", insert.SchemaName);
        Assert.Equal("users", insert.TableName);
    }

    [Fact]
    public void Insert_Unqualified_LeavesSchemaNull()
    {
        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO users (id) VALUES (1)");

        InsertStatement insert = Assert.IsType<InsertStatement>(statement);
        Assert.Null(insert.SchemaName);
        Assert.Equal("users", insert.TableName);
    }

    [Fact]
    public void Update_SchemaQualified_SplitsSchemaAndName()
    {
        Statement statement = SqlParser.ParseStatement(
            "UPDATE myapp.users SET name = 'x' WHERE id = 1");

        UpdateStatement update = Assert.IsType<UpdateStatement>(statement);
        Assert.Equal("myapp", update.SchemaName);
        Assert.Equal("users", update.TableName);
    }

    [Fact]
    public void Delete_SchemaQualified_SplitsSchemaAndName()
    {
        Statement statement = SqlParser.ParseStatement(
            "DELETE FROM myapp.users WHERE id = 1");

        DeleteStatement delete = Assert.IsType<DeleteStatement>(statement);
        Assert.Equal("myapp", delete.SchemaName);
        Assert.Equal("users", delete.TableName);
    }

    [Fact]
    public void CreateIndex_SchemaQualifiedTable_SplitsSchemaAndName()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE INDEX idx_email ON myapp.users (email)");

        CreateIndexStatement create = Assert.IsType<CreateIndexStatement>(statement);
        Assert.Equal("idx_email", create.IndexName);
        Assert.Equal("myapp", create.SchemaName);
        Assert.Equal("users", create.TableName);
    }

    [Fact]
    public void Reindex_SchemaQualified_SplitsSchemaAndName()
    {
        Statement statement = SqlParser.ParseStatement("REINDEX myapp.users");

        ReindexTableStatement reindex = Assert.IsType<ReindexTableStatement>(statement);
        Assert.Equal("myapp", reindex.SchemaName);
        Assert.Equal("users", reindex.TableName);
    }

    [Fact]
    public void ReindexTable_SchemaQualified_SplitsSchemaAndName()
    {
        Statement statement = SqlParser.ParseStatement("REINDEX TABLE myapp.users");

        ReindexTableStatement reindex = Assert.IsType<ReindexTableStatement>(statement);
        Assert.Equal("myapp", reindex.SchemaName);
        Assert.Equal("users", reindex.TableName);
    }

    [Fact]
    public void Analyze_SchemaQualified_SplitsSchemaAndName()
    {
        Statement statement = SqlParser.ParseStatement("ANALYZE myapp.users");

        AnalyzeTableStatement analyze = Assert.IsType<AnalyzeTableStatement>(statement);
        Assert.Equal("myapp", analyze.SchemaName);
        Assert.Equal("users", analyze.TableName);
    }
}
