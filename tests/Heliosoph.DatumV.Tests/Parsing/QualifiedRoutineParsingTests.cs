using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Parsing;

/// <summary>
/// S7b: parser splits <c>schema.fn(...)</c> into
/// <see cref="FunctionCallExpression.SchemaName"/> + bare
/// <see cref="FunctionCallExpression.FunctionName"/>, and the
/// CREATE/DROP FUNCTION/PROCEDURE statements accept qualified targets
/// the same way schema-qualified table DDL does. The
/// <c>CallName</c> helper bridges the bare-string lookup contract for
/// call sites that haven't migrated to the qualified lookup yet.
/// </summary>
public sealed class QualifiedRoutineParsingTests : ServiceTestBase
{
    [Fact]
    public void FunctionCall_Bare_LeavesSchemaNull()
    {
        QueryExpression q = SqlParser.Parse("SELECT upper(name) FROM t");
        SelectQueryExpression sqe = Assert.IsType<SelectQueryExpression>(q);
        FunctionCallExpression fn = Assert.IsType<FunctionCallExpression>(
            sqe.Statement.Columns[0].Expression);

        Assert.Null(fn.SchemaName);
        Assert.Equal("upper", fn.FunctionName);
        Assert.Equal("upper", fn.CallName);
    }

    [Fact]
    public void FunctionCall_UdfSchema_Splits()
    {
        QueryExpression q = SqlParser.Parse("SELECT udf.shout(name) FROM t");
        SelectQueryExpression sqe = Assert.IsType<SelectQueryExpression>(q);
        FunctionCallExpression fn = Assert.IsType<FunctionCallExpression>(
            sqe.Statement.Columns[0].Expression);

        Assert.Equal("udf", fn.SchemaName);
        Assert.Equal("shout", fn.FunctionName);
        Assert.Equal("udf.shout", fn.CallName);
    }

    [Fact]
    public void FunctionCall_ArbitrarySchema_Splits()
    {
        // No special-casing for the schema name — anything goes in S7b's
        // parser surface. The applier validates at plan time.
        QueryExpression q = SqlParser.Parse("SELECT myapp.classify(img) FROM t");
        SelectQueryExpression sqe = Assert.IsType<SelectQueryExpression>(q);
        FunctionCallExpression fn = Assert.IsType<FunctionCallExpression>(
            sqe.Statement.Columns[0].Expression);

        Assert.Equal("myapp", fn.SchemaName);
        Assert.Equal("classify", fn.FunctionName);
    }

    [Fact]
    public void TableValuedFunctionSource_Qualified_Splits()
    {
        QueryExpression q = SqlParser.Parse("SELECT * FROM system.range(0, 10)");
        SelectQueryExpression sqe = Assert.IsType<SelectQueryExpression>(q);
        FunctionSource fs = Assert.IsType<FunctionSource>(sqe.Statement.From!.Source);

        Assert.Equal("system", fs.SchemaName);
        Assert.Equal("range", fs.FunctionName);
        Assert.Equal("system.range", fs.CallName);
    }

    [Fact]
    public void TableValuedFunctionSource_Bare_LeavesSchemaNull()
    {
        QueryExpression q = SqlParser.Parse("SELECT * FROM range(0, 10)");
        SelectQueryExpression sqe = Assert.IsType<SelectQueryExpression>(q);
        FunctionSource fs = Assert.IsType<FunctionSource>(sqe.Statement.From!.Source);

        Assert.Null(fs.SchemaName);
        Assert.Equal("range", fs.FunctionName);
    }

    // ──────────────────── DDL ────────────────────

    [Fact]
    public void CreateFunction_SchemaQualified_PopulatesSchema()
    {
        Statement stmt = SqlParser.ParseStatement(
            "CREATE FUNCTION myapp.classify(img IMAGE) RETURNS INT32 AS BEGIN RETURN 1 END");

        CreateFunctionStatement create = Assert.IsType<CreateFunctionStatement>(stmt);
        Assert.Equal("myapp", create.SchemaName);
        Assert.Equal("classify", create.Name);
    }

    [Fact]
    public void CreateFunction_Bare_LeavesSchemaNull()
    {
        Statement stmt = SqlParser.ParseStatement(
            "CREATE FUNCTION classify(img IMAGE) RETURNS INT32 AS BEGIN RETURN 1 END");

        CreateFunctionStatement create = Assert.IsType<CreateFunctionStatement>(stmt);
        Assert.Null(create.SchemaName);
        Assert.Equal("classify", create.Name);
    }

    [Fact]
    public void DropFunction_SchemaQualified_PopulatesSchema()
    {
        Statement stmt = SqlParser.ParseStatement("DROP FUNCTION myapp.classify");

        DropFunctionStatement drop = Assert.IsType<DropFunctionStatement>(stmt);
        Assert.Equal("myapp", drop.SchemaName);
        Assert.Equal("classify", drop.Name);
    }

    [Fact]
    public void CreateProcedure_SchemaQualified_PopulatesSchema()
    {
        Statement stmt = SqlParser.ParseStatement(
            "CREATE PROCEDURE myapp.tally(n INT32) AS BEGIN SELECT n END");

        CreateProcedureStatement create = Assert.IsType<CreateProcedureStatement>(stmt);
        Assert.Equal("myapp", create.SchemaName);
        Assert.Equal("tally", create.Name);
    }

    [Fact]
    public void DropProcedure_SchemaQualified_PopulatesSchema()
    {
        Statement stmt = SqlParser.ParseStatement("DROP PROCEDURE myapp.tally");

        DropProcedureStatement drop = Assert.IsType<DropProcedureStatement>(stmt);
        Assert.Equal("myapp", drop.SchemaName);
        Assert.Equal("tally", drop.Name);
    }

    [Fact]
    public void Call_SchemaQualified_SplitsFunctionCall()
    {
        Statement stmt = SqlParser.ParseStatement("CALL myapp.tally(42)");

        CallStatement call = Assert.IsType<CallStatement>(stmt);
        FunctionCallExpression fn = Assert.IsType<FunctionCallExpression>(call.Call);
        Assert.Equal("myapp", fn.SchemaName);
        Assert.Equal("tally", fn.FunctionName);
    }

    [Fact]
    public void Call_LegacyProcPrefix_StillProducesProcSchema()
    {
        // Pre-S7d procedures live under the placeholder `proc` schema; the
        // S7b parser surfaces that as `SchemaName == "proc"` consistently.
        Statement stmt = SqlParser.ParseStatement("CALL proc.tally(42)");

        CallStatement call = Assert.IsType<CallStatement>(stmt);
        FunctionCallExpression fn = Assert.IsType<FunctionCallExpression>(call.Call);
        Assert.Equal("proc", fn.SchemaName);
        Assert.Equal("tally", fn.FunctionName);
    }
}
