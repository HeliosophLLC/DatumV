using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Tests for <c>CREATE FUNCTION</c> / <c>DROP FUNCTION</c> DDL parsing.
/// Validates that the AST captures parameter shapes, return-type annotations,
/// guard clauses, and that the body can be any scalar expression — including
/// model invocations and template strings.
/// </summary>
public class UdfDdlParsingTests : ServiceTestBase
{
    private static T Parse<T>(string sql) where T : Statement
    {
        Statement stmt = SqlParser.ParseStatement(sql);
        return Assert.IsType<T>(stmt);
    }

    [Fact]
    public void Create_SingleStringParam_ProducesCreateFunctionStatement()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION shout(name STRING) AS upper(name)");

        Assert.Equal("shout", create.Name);
        Assert.Single(create.Parameters);
        Assert.Equal("name", create.Parameters[0].Name);
        Assert.Equal("STRING", create.Parameters[0].TypeName, ignoreCase: true);
        Assert.Null(create.ReturnTypeName);
        Assert.False(create.IfNotExists);
        Assert.False(create.OrReplace);
    }

    [Fact]
    public void Create_MultipleParams_ParsesAllInOrder()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION add3(a INT32, b INT32, c INT32) AS a + b + c");

        Assert.Equal(3, create.Parameters.Count);
        Assert.Equal("a", create.Parameters[0].Name);
        Assert.Equal("b", create.Parameters[1].Name);
        Assert.Equal("c", create.Parameters[2].Name);
    }

    [Fact]
    public void Create_NoParams_ParsesEmptyList()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION nullary() AS 42");

        Assert.Empty(create.Parameters);
    }

    [Fact]
    public void Create_WithReturnsAnnotation_CapturesReturnType()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION sq(x INT32) RETURNS INT32 AS x * x");

        Assert.Equal("INT32", create.ReturnTypeName, ignoreCase: true);
    }

    [Fact]
    public void Create_OrReplace_SetsFlag()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE OR REPLACE FUNCTION shout(name STRING) AS upper(name)");

        Assert.True(create.OrReplace);
        Assert.False(create.IfNotExists);
    }

    [Fact]
    public void Create_IfNotExists_SetsFlag()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION IF NOT EXISTS shout(name STRING) AS upper(name)");

        Assert.True(create.IfNotExists);
        Assert.False(create.OrReplace);
    }

    [Fact]
    public void Create_BodyIsTemplateString_ParsesAsConcatCall()
    {
        // Validates that the new template-string syntax composes with UDF DDL.
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION greet(name STRING) AS `Hello ${name}!`");

        FunctionCallExpression body = Assert.IsType<FunctionCallExpression>(create.Body);
        Assert.Equal("concat", body.FunctionName);
    }

    [Fact]
    public void Create_BodyIsModelCall_ParsesAsFunctionCall()
    {
        // The body can reference models.X — the planner's hoist pass handles
        // it after UDF inlining. Just confirm it parses.
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION classify(img IMAGE) AS models.mobilenetv2(img)");

        FunctionCallExpression body = Assert.IsType<FunctionCallExpression>(create.Body);
        Assert.Equal("models.mobilenetv2", body.FunctionName);
    }

    [Fact]
    public void Create_BodyIsBinaryExpression_ParsesAsExpression()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION inc(x INT32) AS x + 1");

        BinaryExpression body = Assert.IsType<BinaryExpression>(create.Body);
        Assert.Equal(BinaryOperator.Add, body.Operator);
    }

    [Fact]
    public void Drop_BasicForm_ProducesDropFunctionStatement()
    {
        DropFunctionStatement drop = Parse<DropFunctionStatement>("DROP FUNCTION shout");

        Assert.Equal("shout", drop.Name);
        Assert.False(drop.IfExists);
    }

    [Fact]
    public void Drop_IfExists_SetsFlag()
    {
        DropFunctionStatement drop = Parse<DropFunctionStatement>("DROP FUNCTION IF EXISTS shout");

        Assert.True(drop.IfExists);
    }

    [Fact]
    public void Create_TrailingSemicolon_IsAcceptedByBatch()
    {
        IReadOnlyList<Statement> batch = SqlParser.ParseBatch(
            "CREATE FUNCTION shout(name STRING) AS upper(name);");

        Assert.Single(batch);
        Assert.IsType<CreateFunctionStatement>(batch[0]);
    }

    [Fact]
    public void Batch_CreateThenSelect_ParsesBoth()
    {
        IReadOnlyList<Statement> batch = SqlParser.ParseBatch(
            "CREATE FUNCTION shout(name STRING) AS upper(name); SELECT udf.shout('hi') FROM dual;");

        Assert.Equal(2, batch.Count);
        Assert.IsType<CreateFunctionStatement>(batch[0]);
        Assert.IsType<QueryStatement>(batch[1]);
    }
}
