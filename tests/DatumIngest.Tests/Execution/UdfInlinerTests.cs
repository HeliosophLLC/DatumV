using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the AST-level <see cref="UdfInliner"/>: parameter substitution,
/// arity validation, cycle detection, and the validation wrappers added by
/// <c>IS NOT NULL</c> annotations and <c>RETURNS</c> type declarations.
/// </summary>
public class UdfInlinerTests : ServiceTestBase
{
    private static UdfDescriptor CreateUdf(string sql)
    {
        CreateFunctionStatement create = (CreateFunctionStatement)SqlParser.ParseStatement(sql);
        // Macro-only test helper — ExpressionBody is guaranteed non-null
        // because every CREATE FUNCTION here uses the AS-expression form.
        // Tests register at <c>public</c> so the inliner's default
        // <c>[public, system]</c> search path resolves them.
        return new UdfDescriptor(
            SchemaName: create.SchemaName ?? "public",
            Name: create.Name,
            Parameters: create.Parameters,
            ReturnTypeName: create.ReturnTypeName,
            ExpressionBody: create.ExpressionBody!,
            ReturnIsNotNull: create.ReturnIsNotNull);
    }

    private static Expression Inline(string callSql, params string[] udfDdl)
    {
        UdfRegistry registry = new();
        foreach (string ddl in udfDdl)
        {
            registry.Register(CreateUdf(ddl));
        }

        Expression call = ((SelectQueryExpression)SqlParser.Parse($"SELECT {callSql}")).Statement.Columns[0].Expression;
        return UdfInliner.Inline(call, registry);
    }

    // ───────────────────── Parameter substitution ─────────────────────

    [Fact]
    public void Inline_OneArgUdf_SubstitutesParameter()
    {
        Expression result = Inline(
            "shout(name)",
            "CREATE FUNCTION shout(@s STRING) AS upper(@s)");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("upper", call.FunctionName);
        ColumnReference arg = Assert.IsType<ColumnReference>(call.Arguments[0]);
        Assert.Equal("name", arg.ColumnName);
    }

    [Fact]
    public void Inline_TwoArgUdf_SubstitutesBothParameters()
    {
        Expression result = Inline(
            "addnums(x, y)",
            "CREATE FUNCTION addnums(@a INT32, @b INT32) AS @a + @b");

        BinaryExpression sum = Assert.IsType<BinaryExpression>(result);
        Assert.Equal(BinaryOperator.Add, sum.Operator);
        Assert.Equal("x", Assert.IsType<ColumnReference>(sum.Left).ColumnName);
        Assert.Equal("y", Assert.IsType<ColumnReference>(sum.Right).ColumnName);
    }

    [Fact]
    public void Inline_ZeroArgUdf_BodyEmittedAsIs()
    {
        Expression result = Inline(
            "zero()",
            "CREATE FUNCTION zero() AS 0");

        LiteralExpression literal = Assert.IsType<LiteralExpression>(result);
        Assert.Equal((sbyte)0, literal.Value);
    }

    [Fact]
    public void Inline_LiteralArg_InlinedDirectly()
    {
        Expression result = Inline(
            "shout('hello')",
            "CREATE FUNCTION shout(@s STRING) AS upper(@s)");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(result);
        LiteralExpression arg = Assert.IsType<LiteralExpression>(call.Arguments[0]);
        Assert.Equal("hello", arg.Value);
    }

    [Fact]
    public void Inline_NestedUdfCall_FullyInlines()
    {
        // b calls a — after inlining, no UDF references remain.
        Expression result = Inline(
            "b(name)",
            "CREATE FUNCTION a(@s STRING) AS upper(@s)",
            "CREATE FUNCTION b(@s STRING) AS a(@s)");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("upper", call.FunctionName);
        // No UDF references should remain after full inlining — only built-ins.
        Assert.Null(call.SchemaName);
    }

    [Fact]
    public void Inline_UdfInArgument_BothLevelsInlined()
    {
        // The argument to the outer UDF is itself a UDF call. The argument is
        // inlined first (children-first), then substituted into the outer body.
        Expression result = Inline(
            "scale(bump(x))",
            "CREATE FUNCTION bump(@v INT32) AS @v + 1",
            "CREATE FUNCTION scale(@v INT32) AS @v * 2");

        BinaryExpression mul = Assert.IsType<BinaryExpression>(result);
        Assert.Equal(BinaryOperator.Multiply, mul.Operator);
        BinaryExpression innerAdd = Assert.IsType<BinaryExpression>(mul.Left);
        Assert.Equal(BinaryOperator.Add, innerAdd.Operator);
    }

    [Fact]
    public void Inline_TemplateBody_ConcatPreserved()
    {
        // A UDF whose body is a backtick template lowers to concat() at parse
        // time; the inliner just substitutes the params inside that concat.
        Expression result = Inline(
            "greet(name)",
            "CREATE FUNCTION greet(@n STRING) AS `Hello ${@n}!`");

        FunctionCallExpression concat = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("concat", concat.FunctionName);
        // Args: 'Hello ', name, '!'
        Assert.Equal(3, concat.Arguments.Count);
        Assert.Equal("name", Assert.IsType<ColumnReference>(concat.Arguments[1]).ColumnName);
    }

    // ───────────────────── Validation ─────────────────────

    [Fact]
    public void Inline_TooFewArgs_Throws()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Inline(
            "addnums(x)",
            "CREATE FUNCTION addnums(@a INT32, @b INT32) AS @a + @b"));

        Assert.Contains("expects 2", ex.Message);
        Assert.Contains("got 1", ex.Message);
    }

    [Fact]
    public void Inline_TooManyArgs_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Inline(
            "addnums(x, y, z)",
            "CREATE FUNCTION addnums(@a INT32, @b INT32) AS @a + @b"));
    }

    [Fact]
    public void Inline_UnregisteredCall_PassesThrough()
    {
        // Post-S7d the inliner only inlines macro UDFs that resolve through
        // the registry. Anything else — built-ins, models, or unknown
        // names — passes through unchanged for the scalar-dispatch path
        // to handle at evaluation time.
        UdfRegistry registry = new();
        registry.Register(CreateUdf("CREATE FUNCTION other(@x INT32) AS @x")); // unrelated

        Expression call = ((SelectQueryExpression)SqlParser.Parse("SELECT missing(x)")).Statement.Columns[0].Expression;
        Expression result = UdfInliner.Inline(call, registry);

        FunctionCallExpression fn = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("missing", fn.FunctionName);
    }

    [Fact]
    public void Inline_EmptyRegistry_NonUdfExpression_PreservesShape()
    {
        // No UDF calls in the expression → walk completes a no-op (modulo
        // arg-list backing collection: arrays vs lists are not record-equal,
        // so we compare by formatted text instead of full record equality).
        UdfRegistry empty = new();
        Expression call = ((SelectQueryExpression)SqlParser.Parse("SELECT upper(x)")).Statement.Columns[0].Expression;
        Expression result = UdfInliner.Inline(call, empty);

        Assert.Equal(
            QueryExplainer.FormatExpression(call),
            QueryExplainer.FormatExpression(result));
    }

    // ───────────────────── Cycle detection ─────────────────────

    [Fact]
    public void Inline_DirectSelfReference_ThrowsAtCallSite()
    {
        UdfRegistry registry = new();

        // Self-reference at registration. The inliner check at registration
        // time (in TableCatalog.ApplyCreateFunction) catches this; but if we
        // bypass that and force-register a self-cycle, the call site detects
        // it.
        registry.Register(new UdfDescriptor(
            SchemaName: "public",
            Name: "loop",
            Parameters: new[] { new UdfParameter("x", "INT32") },
            ReturnTypeName: null,
            // Body is loop(@x) — creates a self-cycle.
            ExpressionBody: new FunctionCallExpression("loop",
                new[] { (Expression)new VariableExpression("x") },
                SchemaName: "public")));

        Expression call = ((SelectQueryExpression)SqlParser.Parse("SELECT loop(y)")).Statement.Columns[0].Expression;
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => UdfInliner.Inline(call, registry));
        Assert.Contains("Cyclic UDF reference", ex.Message);
        Assert.Contains("loop", ex.Message);
    }

    [Fact]
    public void Inline_IndirectCycle_ThrowsWithChainInMessage()
    {
        // a → b → a is detected when the inliner walks back into 'a'.
        UdfRegistry registry = new();
        registry.Register(new UdfDescriptor(
            SchemaName: "public",
            Name: "a",
            Parameters: new[] { new UdfParameter("x", "INT32") },
            ReturnTypeName: null,
            ExpressionBody: new FunctionCallExpression("b",
                new[] { (Expression)new VariableExpression("x") },
                SchemaName: "public")));
        registry.Register(new UdfDescriptor(
            SchemaName: "public",
            Name: "b",
            Parameters: new[] { new UdfParameter("x", "INT32") },
            ReturnTypeName: null,
            ExpressionBody: new FunctionCallExpression("a",
                new[] { (Expression)new VariableExpression("x") },
                SchemaName: "public")));

        Expression call = ((SelectQueryExpression)SqlParser.Parse("SELECT a(z)")).Statement.Columns[0].Expression;
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => UdfInliner.Inline(call, registry));
        Assert.Contains("a", ex.Message);
        Assert.Contains("b", ex.Message);
    }

    // ───────────────────── Lambda + UDF interaction ─────────────────────

    [Fact]
    public void Inline_LambdaParamUnaffectedBySubstitution()
    {
        // UDF param '@x' substitution lands at the outer array position; the
        // lambda's bare-name 'y' is unrelated and stays as-is.
        Expression result = Inline(
            "f(arr)",
            "CREATE FUNCTION f(@x ARRAY) AS array_transform(@x, y -> y * 2)");

        FunctionCallExpression at = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("array_transform", at.FunctionName);
        ColumnReference firstArg = Assert.IsType<ColumnReference>(at.Arguments[0]);
        Assert.Equal("arr", firstArg.ColumnName);
        // Second arg is the lambda — its body still references 'y'.
        LambdaExpression lambda = Assert.IsType<LambdaExpression>(at.Arguments[1]);
        BinaryExpression mul = Assert.IsType<BinaryExpression>(lambda.Body);
        Assert.Equal("y", Assert.IsType<ColumnReference>(mul.Left).ColumnName);
    }

    [Fact]
    public void Inline_LambdaBareNameDistinctFromUdfVariable()
    {
        // UDF param @x and lambda param x live in separate namespaces.
        // VariableExpression substitution targets only @-prefixed nodes,
        // so the lambda's bare-name `x` stays bare.
        Expression result = Inline(
            "f(arr)",
            "CREATE FUNCTION f(@x ARRAY) AS array_transform(@x, x -> x * 2)");

        FunctionCallExpression at = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("arr", Assert.IsType<ColumnReference>(at.Arguments[0]).ColumnName);
        // Lambda body 'x' must still reference the lambda's bound 'x'.
        LambdaExpression lambda = Assert.IsType<LambdaExpression>(at.Arguments[1]);
        BinaryExpression mul = Assert.IsType<BinaryExpression>(lambda.Body);
        Assert.Equal("x", Assert.IsType<ColumnReference>(mul.Left).ColumnName);
    }

    // ───────────────────── IS NOT NULL parameter wrapping ─────────────────────

    [Fact]
    public void Inline_NotNullParam_WrapsArgInAssertNotNull()
    {
        // When a parameter is declared IS NOT NULL, the substituted argument
        // is wrapped with __assert_not_null at the inlining boundary so the
        // body never sees a null value for that parameter.
        Expression result = Inline(
            "shout(name)",
            "CREATE FUNCTION shout(@s STRING IS NOT NULL) AS upper(@s)");

        // Result: upper(__assert_not_null(name, '...'))
        FunctionCallExpression upper = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("upper", upper.FunctionName);
        FunctionCallExpression guard = Assert.IsType<FunctionCallExpression>(upper.Arguments[0]);
        Assert.Equal("__assert_not_null", guard.FunctionName);
        Assert.Equal("name", Assert.IsType<ColumnReference>(guard.Arguments[0]).ColumnName);
    }

    [Fact]
    public void Inline_NullableParam_DoesNotWrap()
    {
        // No IS NOT NULL → no wrapper, body sees the raw arg.
        Expression result = Inline(
            "shout(name)",
            "CREATE FUNCTION shout(@s STRING) AS upper(@s)");

        FunctionCallExpression upper = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("upper", upper.FunctionName);
        Assert.IsType<ColumnReference>(upper.Arguments[0]);
    }

    [Fact]
    public void Inline_MixedNullableAndNotNullParams_WrapsOnlyNotNull()
    {
        Expression result = Inline(
            "combine(a, b)",
            "CREATE FUNCTION combine(@x STRING IS NOT NULL, @y STRING) AS concat(@x, @y)");

        FunctionCallExpression concat = Assert.IsType<FunctionCallExpression>(result);
        // First arg: wrapped.
        FunctionCallExpression guard = Assert.IsType<FunctionCallExpression>(concat.Arguments[0]);
        Assert.Equal("__assert_not_null", guard.FunctionName);
        // Second arg: unwrapped column reference.
        Assert.IsType<ColumnReference>(concat.Arguments[1]);
    }

    // ───────────────────── RETURNS enforcement ─────────────────────

    [Fact]
    public void Inline_ReturnsType_WrapsBodyInCast()
    {
        Expression result = Inline(
            "parsed(s)",
            "CREATE FUNCTION parsed(@s STRING) RETURNS INT32 AS try_cast(@s, INT32)");

        // Outer wrapper is a CAST to INT32.
        CastExpression cast = Assert.IsType<CastExpression>(result);
        Assert.Equal("INT32", cast.TargetType, ignoreCase: true);
    }

    [Fact]
    public void Inline_ReturnsTypeIsNotNull_WrapsCastInAssertNotNull()
    {
        Expression result = Inline(
            "parsed(s)",
            "CREATE FUNCTION parsed(@s STRING) RETURNS INT32 IS NOT NULL AS try_cast(@s, INT32)");

        // Outer is __assert_not_null; its first arg is the CAST.
        FunctionCallExpression guard = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("__assert_not_null", guard.FunctionName);
        Assert.IsType<CastExpression>(guard.Arguments[0]);
    }

    [Fact]
    public void Inline_NoReturnAnnotation_NoWrapping()
    {
        // Default — no RETURNS, no wrap. The body is emitted bare.
        Expression result = Inline(
            "id(x)",
            "CREATE FUNCTION id(@x INT32) AS @x");

        // Substituted to a ColumnReference — no Cast, no AssertNotNull.
        Assert.IsType<ColumnReference>(result);
    }

    // ───────────────────── Top-level query inlining ─────────────────────

    [Fact]
    public void Inline_QueryExpression_RewritesSelectListAndWhere()
    {
        UdfRegistry registry = new();
        registry.Register(CreateUdf("CREATE FUNCTION shout(@s STRING) AS upper(@s)"));

        QueryExpression q = SqlParser.Parse(
            "SELECT shout(name) FROM users WHERE shout(role) = 'ADMIN'");
        QueryExpression inlined = UdfInliner.Inline(q, registry);

        SelectStatement stmt = ((SelectQueryExpression)inlined).Statement;
        // SELECT list — UDF replaced with upper(name).
        FunctionCallExpression projected = Assert.IsType<FunctionCallExpression>(stmt.Columns[0].Expression);
        Assert.Equal("upper", projected.FunctionName);
        // WHERE — UDF replaced inside the equality.
        BinaryExpression where = Assert.IsType<BinaryExpression>(stmt.Where);
        FunctionCallExpression upperInWhere = Assert.IsType<FunctionCallExpression>(where.Left);
        Assert.Equal("upper", upperInWhere.FunctionName);
    }

    [Fact]
    public void Inline_RandomCallsInBody_PreservedAsIs()
    {
        // The core promise: UDFs are macros. Random functions in the body
        // are NOT pre-evaluated — they remain as function calls and the
        // engine sees N independent calls when the UDF appears N times.
        UdfRegistry registry = new();
        registry.Register(CreateUdf(
            "CREATE FUNCTION roll() AS random(0.7, 1.0)"));

        // Same call site twice — both inline to independent random calls.
        QueryExpression q = SqlParser.Parse(
            "SELECT roll() AS a, roll() AS b FROM dual");
        QueryExpression inlined = UdfInliner.Inline(q, registry);

        SelectStatement stmt = ((SelectQueryExpression)inlined).Statement;
        FunctionCallExpression aCall = Assert.IsType<FunctionCallExpression>(stmt.Columns[0].Expression);
        FunctionCallExpression bCall = Assert.IsType<FunctionCallExpression>(stmt.Columns[1].Expression);
        Assert.Equal("random", aCall.FunctionName);
        Assert.Equal("random", bCall.FunctionName);
        // Two AST nodes — not a shared reference. Each call site evaluates
        // independently; the planner will not CSE them.
        Assert.NotSame(aCall, bCall);
    }
}
