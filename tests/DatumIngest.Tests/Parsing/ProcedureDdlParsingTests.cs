using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Tests for <c>CREATE PROCEDURE</c> / <c>DROP PROCEDURE</c> DDL parsing.
/// Validates that the AST captures the parameter list, the BEGIN/END
/// body, and the OR REPLACE / IF NOT EXISTS modifiers.
/// </summary>
public class ProcedureDdlParsingTests : ServiceTestBase
{
    private static T Parse<T>(string sql) where T : Statement
    {
        Statement stmt = SqlParser.ParseStatement(sql);
        return Assert.IsType<T>(stmt);
    }

    [Fact]
    public void Create_NoParams_ProducesCreateProcedureStatement()
    {
        CreateProcedureStatement create = Parse<CreateProcedureStatement>(
            "CREATE PROCEDURE noop() AS BEGIN SELECT 1 END");

        Assert.Equal("noop", create.Name);
        Assert.Empty(create.Parameters);
        Assert.False(create.IfNotExists);
        Assert.False(create.OrReplace);
        Assert.NotNull(create.Body);
        Assert.Single(create.Body.Statements);
    }

    [Fact]
    public void Create_SingleParam_ParsesAtPrefixAndType()
    {
        CreateProcedureStatement create = Parse<CreateProcedureStatement>(
            "CREATE PROCEDURE inc(@x INT32) AS BEGIN SET @x = @x + 1 END");

        Assert.Single(create.Parameters);
        Assert.Equal("x", create.Parameters[0].Name);
        Assert.Equal("INT32", create.Parameters[0].TypeName, ignoreCase: true);
        Assert.False(create.Parameters[0].IsNotNull);
    }

    [Fact]
    public void Create_MultipleParams_AllCaptured()
    {
        CreateProcedureStatement create = Parse<CreateProcedureStatement>(
            "CREATE PROCEDURE add3(@a INT32, @b INT32, @c INT32) AS BEGIN " +
            "  DECLARE @sum INT32 = @a + @b + @c " +
            "END");

        Assert.Equal(3, create.Parameters.Count);
        Assert.Equal(["a", "b", "c"], create.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void Create_ParamWithIsNotNull_SetsFlag()
    {
        CreateProcedureStatement create = Parse<CreateProcedureStatement>(
            "CREATE PROCEDURE need_name(@name STRING IS NOT NULL) AS BEGIN " +
            "  SELECT @name " +
            "END");

        Assert.True(create.Parameters[0].IsNotNull);
    }

    [Fact]
    public void Create_OrReplace_SetsFlag()
    {
        CreateProcedureStatement create = Parse<CreateProcedureStatement>(
            "CREATE OR REPLACE PROCEDURE noop() AS BEGIN SELECT 1 END");

        Assert.True(create.OrReplace);
    }

    [Fact]
    public void Create_OrAlter_SetsFlag()
    {
        // T-SQL spelling — accepted as a synonym for OR REPLACE.
        CreateProcedureStatement create = Parse<CreateProcedureStatement>(
            "CREATE OR ALTER PROCEDURE noop() AS BEGIN SELECT 1 END");

        Assert.True(create.OrReplace);
    }

    [Fact]
    public void Create_IfNotExists_SetsFlag()
    {
        CreateProcedureStatement create = Parse<CreateProcedureStatement>(
            "CREATE PROCEDURE IF NOT EXISTS noop() AS BEGIN SELECT 1 END");

        Assert.True(create.IfNotExists);
    }

    [Fact]
    public void Create_BodyContainsControlFlow_PreservesStructure()
    {
        CreateProcedureStatement create = Parse<CreateProcedureStatement>(
            "CREATE PROCEDURE classify(@score FLOAT64) AS BEGIN " +
            "  IF @score > 0.5 " +
            "    SELECT 'high' " +
            "  ELSE " +
            "    SELECT 'low' " +
            "END");

        Assert.Single(create.Body.Statements);
        Assert.IsType<IfStatement>(create.Body.Statements[0]);
    }

    [Fact]
    public void Drop_BasicForm_ProducesDropProcedureStatement()
    {
        DropProcedureStatement drop = Parse<DropProcedureStatement>("DROP PROCEDURE foo");

        Assert.Equal("foo", drop.Name);
        Assert.False(drop.IfExists);
    }

    [Fact]
    public void Drop_IfExists_SetsFlag()
    {
        DropProcedureStatement drop = Parse<DropProcedureStatement>(
            "DROP PROCEDURE IF EXISTS foo");

        Assert.True(drop.IfExists);
    }

    [Fact]
    public void Create_BodyMustBeBeginEnd_NotASingleStatement()
    {
        // Procedures require a BEGIN/END block; a bare statement body is rejected.
        Assert.ThrowsAny<Exception>(() => SqlParser.ParseStatement(
            "CREATE PROCEDURE foo() AS SELECT 1"));
    }
}
