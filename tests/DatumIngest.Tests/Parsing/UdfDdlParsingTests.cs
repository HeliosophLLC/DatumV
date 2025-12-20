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
            "CREATE FUNCTION shout(@name STRING) AS upper(@name)");

        Assert.Equal("shout", create.Name);
        Assert.Single(create.Parameters);
        Assert.Equal("name", create.Parameters[0].Name);
        Assert.Equal("STRING", create.Parameters[0].TypeName, ignoreCase: true);
        Assert.False(create.Parameters[0].IsNotNull);
        Assert.Null(create.ReturnTypeName);
        Assert.False(create.ReturnIsNotNull);
        Assert.False(create.IfNotExists);
        Assert.False(create.OrReplace);
    }

    [Fact]
    public void Create_MultipleParams_ParsesAllInOrder()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION add3(@a INT32, @b INT32, @c INT32) AS @a + @b + @c");

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
            "CREATE FUNCTION sq(@x INT32) RETURNS INT32 AS @x * @x");

        Assert.Equal("INT32", create.ReturnTypeName, ignoreCase: true);
        Assert.False(create.ReturnIsNotNull);
    }

    [Fact]
    public void Create_ParamWithIsNotNull_SetsFlag()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION shout(@name STRING IS NOT NULL) AS upper(@name)");

        Assert.True(create.Parameters[0].IsNotNull);
    }

    [Fact]
    public void Create_MixedNullableAndNotNullParams_CapturesPerParameter()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION combine(@a STRING IS NOT NULL, @b STRING) AS concat(@a, @b)");

        Assert.True(create.Parameters[0].IsNotNull);
        Assert.False(create.Parameters[1].IsNotNull);
    }

    [Fact]
    public void Create_ArrayParameter_AngleBracket_PreservesCanonicalForm()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION first_player(@players Array<STRING>) AS @players[0]");

        Assert.Equal("Array<STRING>", create.Parameters[0].TypeName);
        Assert.False(create.Parameters[0].IsNotNull);
    }

    [Fact]
    public void Create_ArrayParameter_PostfixSugar_DesugarsToCanonical()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION first_score(@scores FLOAT32[]) AS @scores[0]");

        Assert.Equal("Array<FLOAT32>", create.Parameters[0].TypeName);
    }

    [Fact]
    public void Create_ArrayParameter_WithIsNotNull_ComposesCorrectly()
    {
        // The IS NOT NULL modifier sits *after* the type, so the array
        // parser doesn't swallow it; the parameter still records both pieces.
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION join_names(@players Array<STRING> IS NOT NULL) AS array_to_string(@players, ',')");

        Assert.Equal("Array<STRING>", create.Parameters[0].TypeName);
        Assert.True(create.Parameters[0].IsNotNull);
    }

    [Fact]
    public void Create_ArrayReturnType_PreservedOnDescriptor()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION lift(@x INT32) RETURNS Array<INT32> AS array_of(@x)");

        Assert.Equal("Array<INT32>", create.ReturnTypeName);
    }

    [Fact]
    public void Create_ReturnsWithIsNotNull_SetsFlag()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION sq(@x INT32) RETURNS INT32 IS NOT NULL AS @x * @x");

        Assert.Equal("INT32", create.ReturnTypeName, ignoreCase: true);
        Assert.True(create.ReturnIsNotNull);
    }

    [Fact]
    public void Create_OrReplace_SetsFlag()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE OR REPLACE FUNCTION shout(@name STRING) AS upper(@name)");

        Assert.True(create.OrReplace);
        Assert.False(create.IfNotExists);
    }

    [Fact]
    public void Create_OrAlter_SetsFlag()
    {
        // T-SQL spelling — accepted as a synonym for OR REPLACE.
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE OR ALTER FUNCTION shout(@name STRING) AS upper(@name)");

        Assert.True(create.OrReplace);
        Assert.False(create.IfNotExists);
    }

    [Fact]
    public void Create_IfNotExists_SetsFlag()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION IF NOT EXISTS shout(@name STRING) AS upper(@name)");

        Assert.True(create.IfNotExists);
        Assert.False(create.OrReplace);
    }

    [Fact]
    public void Create_ParamWithDefault_CapturesDefaultExpression()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION shout(@name STRING = 'world') AS upper(@name)");

        Assert.Single(create.Parameters);
        Assert.NotNull(create.Parameters[0].Default);
        Assert.False(create.Parameters[0].IsNotNull);
    }

    [Fact]
    public void Create_ParamWithDefault_AndIsNotNull_BothFlagsSet()
    {
        // IS NOT NULL precedes the default because `expr IS NOT NULL` is
        // itself a valid scalar predicate; placing the modifier last would
        // be ambiguous with the default expression.
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION add(@x INT32 IS NOT NULL = 0, @y INT32 IS NOT NULL = 0) AS @x + @y");

        Assert.Equal(2, create.Parameters.Count);
        Assert.NotNull(create.Parameters[0].Default);
        Assert.True(create.Parameters[0].IsNotNull);
        Assert.NotNull(create.Parameters[1].Default);
        Assert.True(create.Parameters[1].IsNotNull);
    }

    [Fact]
    public void Create_MixOfRequiredAndDefault_CapturesBoth()
    {
        // Required @a; defaulted @b — minimum arity is 1.
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION add(@a INT32, @b INT32 = 1) AS @a + @b");

        Assert.Null(create.Parameters[0].Default);
        Assert.NotNull(create.Parameters[1].Default);
    }

    [Fact]
    public void Create_BodyIsTemplateString_ParsesAsConcatCall()
    {
        // Validates that the new template-string syntax composes with UDF DDL.
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION greet(@name STRING) AS `Hello ${@name}!`");

        FunctionCallExpression body = Assert.IsType<FunctionCallExpression>(create.ExpressionBody);
        Assert.Equal("concat", body.FunctionName);
    }

    [Fact]
    public void Create_BodyIsModelCall_ParsesAsFunctionCall()
    {
        // The body can reference models.X — the planner's hoist pass handles
        // it after UDF inlining. Just confirm it parses.
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION classify(@img IMAGE) AS models.mobilenetv2(@img)");

        FunctionCallExpression body = Assert.IsType<FunctionCallExpression>(create.ExpressionBody);
        Assert.Equal("models.mobilenetv2", body.FunctionName);
    }

    [Fact]
    public void Create_BodyIsBinaryExpression_ParsesAsExpression()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION inc(@x INT32) AS @x + 1");

        BinaryExpression body = Assert.IsType<BinaryExpression>(create.ExpressionBody);
        Assert.Equal(BinaryOperator.Add, body.Operator);
    }

    // ───────────────────── Procedural UDFs (BEGIN…END body) ─────────────────────

    [Fact]
    public void Create_ProceduralBody_PopulatesStatementBodyAndClearsExpressionBody()
    {
        // Smallest legal procedural function: takes one parameter, returns
        // an expression of it. The body parses into a Statement[] (not the
        // Expression slot used by macro UDFs) and Body is null.
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION sq(@x INT32) RETURNS INT32 BEGIN RETURN @x * @x END");

        Assert.Null(create.ExpressionBody);
        Assert.NotNull(create.StatementBody);
        Assert.Single(create.StatementBody);
        Assert.IsType<ReturnStatement>(create.StatementBody[0]);
        Assert.Equal("INT32", create.ReturnTypeName, ignoreCase: true);
        Assert.False(create.IsPure);
    }

    [Fact]
    public void Create_ProceduralBody_DeclareThenReturn_PreservesStatementOrder()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION step(@x INT32) RETURNS INT32 BEGIN " +
                "DECLARE @y INT32 = @x + 1; " +
                "RETURN @y " +
            "END");

        Assert.NotNull(create.StatementBody);
        Assert.Equal(2, create.StatementBody.Count);
        Assert.IsType<DeclareStatement>(create.StatementBody[0]);
        Assert.IsType<ReturnStatement>(create.StatementBody[1]);
    }

    [Fact]
    public void Create_PureProceduralFunction_SetsIsPureFlag()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE PURE FUNCTION sq(@x INT32) RETURNS INT32 BEGIN RETURN @x * @x END");

        Assert.True(create.IsPure);
        Assert.NotNull(create.StatementBody);
    }

    [Fact]
    public void Create_OrReplacePureFunction_SetsBothFlags()
    {
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE OR REPLACE PURE FUNCTION sq(@x INT32) RETURNS INT32 BEGIN RETURN @x * @x END");

        Assert.True(create.OrReplace);
        Assert.True(create.IsPure);
    }

    [Fact]
    public void Create_OrAlterPureFunction_SetsBothFlags()
    {
        // T-SQL spelling — OR ALTER is a synonym for OR REPLACE and composes
        // with PURE in the same position.
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE OR ALTER PURE FUNCTION sq(@x INT32) RETURNS INT32 BEGIN RETURN @x * @x END");

        Assert.True(create.OrReplace);
        Assert.True(create.IsPure);
    }

    [Fact]
    public void Create_PureMacro_IsRejected()
    {
        // PURE has no meaning for inlined macros (CSE already deduplicates the
        // substituted expression). The parser should refuse rather than accept
        // a flag that does nothing.
        FormatException ex = Assert.Throws<FormatException>(() =>
            Parse<CreateFunctionStatement>(
                "CREATE PURE FUNCTION sq(@x INT32) AS @x * @x"));

        Assert.Contains("PURE", ex.Message);
    }

    [Fact]
    public void Create_ProceduralBodyWithoutReturnsClause_IsRejected()
    {
        // Procedural bodies are opaque to the planner so RETURNS T must be
        // explicit — without it the type system has no return shape.
        FormatException ex = Assert.Throws<FormatException>(() =>
            Parse<CreateFunctionStatement>(
                "CREATE FUNCTION sq(@x INT32) BEGIN RETURN @x * @x END"));

        Assert.Contains("RETURNS", ex.Message);
    }

    [Fact]
    public void Create_ProceduralBodyMissingTrailingReturn_IsRejected()
    {
        // Procedural bodies must end in RETURN — otherwise some control-flow
        // path could exit with no value defined for the function's result.
        FormatException ex = Assert.Throws<FormatException>(() =>
            Parse<CreateFunctionStatement>(
                "CREATE FUNCTION sq(@x INT32) RETURNS INT32 BEGIN " +
                    "DECLARE @y INT32 = @x * @x " +
                "END"));

        Assert.Contains("RETURN", ex.Message);
    }

    [Fact]
    public void Create_ProceduralBodyWithTopLevelSelect_IsRejected()
    {
        // A scalar function returns one value, not a result set. A bare
        // SELECT in statement position has no place to send its rows.
        FormatException ex = Assert.Throws<FormatException>(() =>
            Parse<CreateFunctionStatement>(
                "CREATE FUNCTION sq(@x INT32) RETURNS INT32 BEGIN " +
                    "SELECT @x; " +
                    "RETURN @x " +
                "END"));

        Assert.Contains("SELECT", ex.Message);
    }

    [Fact]
    public void Create_ProceduralBodyWithSubqueryInReturn_IsAccepted()
    {
        // Subqueries in expression position are fine — the rejection only
        // covers top-level statement-position SELECTs. RETURN (SELECT ...)
        // is a useful idiom for "compute one value via a query".
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION lookup(@id INT32) RETURNS STRING BEGIN " +
                "RETURN (SELECT name FROM users WHERE id = @id) " +
            "END");

        Assert.NotNull(create.StatementBody);
        Assert.IsType<ReturnStatement>(create.StatementBody[^1]);
    }

    [Fact]
    public void Create_ProceduralBodyWithIfElse_BothBranchesEndingInReturn()
    {
        // Procedural control flow with multiple terminating RETURNs.
        // The trailing RETURN at the body's top level still satisfies the
        // "last statement is RETURN" check.
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION abs2(@x INT32) RETURNS INT32 BEGIN " +
                "IF @x < 0 RETURN -@x ELSE RETURN @x " +
            "END");

        Assert.NotNull(create.StatementBody);
        Assert.Single(create.StatementBody);
        Assert.IsType<IfStatement>(create.StatementBody[0]);
    }

    [Fact]
    public void Create_ProceduralBody_RoundTripsThroughBatchParser()
    {
        // Confirm the procedural form composes with the batch parser — a
        // semicolon after END terminates the statement cleanly and a
        // following statement parses as its own batch entry.
        IReadOnlyList<Statement> batch = SqlParser.ParseBatch(
            "CREATE FUNCTION sq(@x INT32) RETURNS INT32 BEGIN RETURN @x * @x END; " +
            "SELECT udf.sq(3) FROM dual;");

        Assert.Equal(2, batch.Count);
        Assert.IsType<CreateFunctionStatement>(batch[0]);
        Assert.IsType<QueryStatement>(batch[1]);
    }

    [Fact]
    public void Create_MacroForm_RemainsUnchanged_StatementBodyIsNull()
    {
        // Regression check: existing macro UDFs must keep parsing into the
        // Expression-body slot with a null StatementBody and IsPure=false.
        CreateFunctionStatement create = Parse<CreateFunctionStatement>(
            "CREATE FUNCTION shout(@name STRING) AS upper(@name)");

        Assert.NotNull(create.ExpressionBody);
        Assert.Null(create.StatementBody);
        Assert.False(create.IsPure);
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
            "CREATE FUNCTION shout(@name STRING) AS upper(@name);");

        Assert.Single(batch);
        Assert.IsType<CreateFunctionStatement>(batch[0]);
    }

    [Fact]
    public void Batch_CreateThenSelect_ParsesBoth()
    {
        IReadOnlyList<Statement> batch = SqlParser.ParseBatch(
            "CREATE FUNCTION shout(@name STRING) AS upper(@name); SELECT udf.shout('hi') FROM dual;");

        Assert.Equal(2, batch.Count);
        Assert.IsType<CreateFunctionStatement>(batch[0]);
        Assert.IsType<QueryStatement>(batch[1]);
    }

    // ───────────────────── EXEC statement ─────────────────────

    [Fact]
    public void Exec_NamespacedUdfCall_ProducesExecStatement()
    {
        ExecStatement exec = Parse<ExecStatement>("EXEC udf.shout('hello')");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(exec.Call);
        Assert.Equal("udf.shout", call.FunctionName);
        Assert.Single(call.Arguments);
    }

    [Fact]
    public void Exec_BareFunction_ProducesExecStatement()
    {
        ExecStatement exec = Parse<ExecStatement>("EXEC upper('hello')");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(exec.Call);
        Assert.Equal("upper", call.FunctionName);
    }

    [Fact]
    public void Exec_MultipleArgs_ParsesAllArguments()
    {
        ExecStatement exec = Parse<ExecStatement>("EXEC udf.add3(1, 2, 3)");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(exec.Call);
        Assert.Equal("udf.add3", call.FunctionName);
        Assert.Equal(3, call.Arguments.Count);
    }

    [Fact]
    public void Exec_NoArgs_ParsesEmptyArgumentList()
    {
        ExecStatement exec = Parse<ExecStatement>("EXEC udf.nullary()");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(exec.Call);
        Assert.Equal("udf.nullary", call.FunctionName);
        Assert.Empty(call.Arguments);
    }

    [Fact]
    public void Exec_TrailingSemicolon_IsAcceptedByBatch()
    {
        IReadOnlyList<Statement> batch = SqlParser.ParseBatch("EXEC udf.shout('hello');");

        Assert.Single(batch);
        Assert.IsType<ExecStatement>(batch[0]);
    }

    [Fact]
    public void Exec_SpanPointsToExecKeyword_NotFunctionName()
    {
        ExecStatement exec = Parse<ExecStatement>("EXEC udf.shout('hello')");

        Assert.NotNull(exec.Span);
        Assert.Equal(1, exec.Span!.Column);
    }
}
