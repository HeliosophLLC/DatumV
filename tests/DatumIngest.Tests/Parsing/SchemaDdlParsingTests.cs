using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Parser tests for the schema-aware DDL added in S3:
/// <list type="bullet">
///   <item><description><c>CREATE TABLE schema.t</c> / <c>DROP TABLE schema.t</c> /
///     <c>ALTER TABLE schema.t</c> qualified-name parsing.</description></item>
///   <item><description><c>CREATE SCHEMA</c> / <c>DROP SCHEMA</c> (+ <c>IF
///     NOT EXISTS</c>, <c>IF EXISTS</c>, <c>CASCADE</c>, <c>RESTRICT</c>).</description></item>
///   <item><description><c>SET search_path = a, b, c</c> and the
///     <c>TO</c>-instead-of-<c>=</c> variant.</description></item>
/// </list>
/// </summary>
public class SchemaDdlParsingTests : ServiceTestBase
{
    // ───────────────────── CREATE / DROP / ALTER schema-qualified ─────────────────────

    [Fact]
    public void CreateTable_SchemaQualified_SplitsSchemaAndName()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TABLE myapp.users (id INT, name STRING)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Equal("myapp", create.SchemaName);
        Assert.Equal("users", create.TableName);
        Assert.Equal(2, create.Columns.Count);
    }

    [Fact]
    public void CreateTable_Unqualified_LeavesSchemaNull()
    {
        Statement statement = SqlParser.ParseStatement(
            "CREATE TABLE users (id INT)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(statement);
        Assert.Null(create.SchemaName);
        Assert.Equal("users", create.TableName);
    }

    [Fact]
    public void DropTable_SchemaQualified_SplitsSchemaAndName()
    {
        Statement statement = SqlParser.ParseStatement(
            "DROP TABLE IF EXISTS myapp.users");

        DropTableStatement drop = Assert.IsType<DropTableStatement>(statement);
        Assert.Equal("myapp", drop.SchemaName);
        Assert.Equal("users", drop.TableName);
        Assert.True(drop.IfExists);
    }

    [Fact]
    public void AlterTable_AddColumn_SchemaQualified()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE myapp.users ADD COLUMN email STRING");

        AlterTableAddColumnStatement alter = Assert.IsType<AlterTableAddColumnStatement>(statement);
        Assert.Equal("myapp", alter.SchemaName);
        Assert.Equal("users", alter.TableName);
        Assert.Equal("email", alter.ColumnName);
    }

    [Fact]
    public void AlterTable_DropColumn_SchemaQualified()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE myapp.users DROP COLUMN email");

        AlterTableDropColumnStatement alter = Assert.IsType<AlterTableDropColumnStatement>(statement);
        Assert.Equal("myapp", alter.SchemaName);
        Assert.Equal("users", alter.TableName);
        Assert.Equal("email", alter.ColumnName);
    }

    [Fact]
    public void AlterTable_DropConstraint_SchemaQualified()
    {
        Statement statement = SqlParser.ParseStatement(
            "ALTER TABLE myapp.users DROP CONSTRAINT users_pkey");

        AlterTableDropConstraintStatement alter = Assert.IsType<AlterTableDropConstraintStatement>(statement);
        Assert.Equal("myapp", alter.SchemaName);
        Assert.Equal("users", alter.TableName);
        Assert.Equal("users_pkey", alter.ConstraintName);
    }

    // ───────────────────── CREATE SCHEMA ─────────────────────

    [Fact]
    public void CreateSchema_Basic()
    {
        Statement statement = SqlParser.ParseStatement("CREATE SCHEMA myapp");

        CreateSchemaStatement create = Assert.IsType<CreateSchemaStatement>(statement);
        Assert.Equal("myapp", create.SchemaName);
        Assert.False(create.IfNotExists);
    }

    [Fact]
    public void CreateSchema_IfNotExists()
    {
        Statement statement = SqlParser.ParseStatement("CREATE SCHEMA IF NOT EXISTS myapp");

        CreateSchemaStatement create = Assert.IsType<CreateSchemaStatement>(statement);
        Assert.Equal("myapp", create.SchemaName);
        Assert.True(create.IfNotExists);
    }

    // ───────────────────── DROP SCHEMA ─────────────────────

    [Fact]
    public void DropSchema_Basic_DefaultsToRestrict()
    {
        Statement statement = SqlParser.ParseStatement("DROP SCHEMA myapp");

        DropSchemaStatement drop = Assert.IsType<DropSchemaStatement>(statement);
        Assert.Equal("myapp", drop.SchemaName);
        Assert.False(drop.IfExists);
        Assert.False(drop.Cascade); // Absence of CASCADE = RESTRICT.
    }

    [Fact]
    public void DropSchema_IfExistsCascade()
    {
        Statement statement = SqlParser.ParseStatement("DROP SCHEMA IF EXISTS myapp CASCADE");

        DropSchemaStatement drop = Assert.IsType<DropSchemaStatement>(statement);
        Assert.Equal("myapp", drop.SchemaName);
        Assert.True(drop.IfExists);
        Assert.True(drop.Cascade);
    }

    [Fact]
    public void DropSchema_ExplicitRestrict_IsTreatedSameAsAbsence()
    {
        Statement statement = SqlParser.ParseStatement("DROP SCHEMA myapp RESTRICT");

        DropSchemaStatement drop = Assert.IsType<DropSchemaStatement>(statement);
        Assert.False(drop.Cascade);
    }

    // ───────────────────── SET search_path ─────────────────────

    [Fact]
    public void SetSearchPath_SingleSchema()
    {
        Statement statement = SqlParser.ParseStatement("SET search_path = myapp");

        SetSearchPathStatement set = Assert.IsType<SetSearchPathStatement>(statement);
        Assert.Equal(new[] { "myapp" }, set.Schemas);
    }

    [Fact]
    public void SetSearchPath_MultipleSchemas()
    {
        Statement statement = SqlParser.ParseStatement(
            "SET search_path = myapp, public, system");

        SetSearchPathStatement set = Assert.IsType<SetSearchPathStatement>(statement);
        Assert.Equal(new[] { "myapp", "public", "system" }, set.Schemas);
    }

    [Fact]
    public void SetSearchPath_AcceptsToInsteadOfEquals()
    {
        // PG accepts both `SET x = y` and `SET x TO y`; we mirror that.
        Statement statement = SqlParser.ParseStatement(
            "SET search_path TO myapp, public");

        SetSearchPathStatement set = Assert.IsType<SetSearchPathStatement>(statement);
        Assert.Equal(new[] { "myapp", "public" }, set.Schemas);
    }

    [Fact]
    public void SetVariable_StillParsesAsSetStatement_NotSearchPath()
    {
        // Ensures the new SET search_path parser doesn't shadow the
        // existing SET var = ... variable-assignment statement.
        Statement statement = SqlParser.ParseStatement("SET x = 42");

        Assert.IsType<SetStatement>(statement);
    }
}
