using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the AST-level <see cref="UdfInliner"/>: parameter substitution,
/// arity validation, cycle detection, and shadowing of lambda / SCAN scopes.
/// </summary>
public class UdfInlinerTests : ServiceTestBase
{
    private static UdfDescriptor CreateUdf(string sql)
    {
        CreateFunctionStatement create = (CreateFunctionStatement)SqlParser.ParseStatement(sql);
        return new UdfDescriptor(create.Name, create.Parameters, create.ReturnTypeName, create.Body);
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
            "udf.shout(name)",
            "CREATE FUNCTION shout(s STRING) AS upper(s)");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("upper", call.FunctionName);
        ColumnReference arg = Assert.IsType<ColumnReference>(call.Arguments[0]);
        Assert.Equal("name", arg.ColumnName);
    }

    [Fact]
    public void Inline_TwoArgUdf_SubstitutesBothParameters()
    {
        Expression result = Inline(
            "udf.add(x, y)",
            "CREATE FUNCTION add(a INT32, b INT32) AS a + b");

        BinaryExpression sum = Assert.IsType<BinaryExpression>(result);
        Assert.Equal(BinaryOperator.Add, sum.Operator);
        Assert.Equal("x", Assert.IsType<ColumnReference>(sum.Left).ColumnName);
        Assert.Equal("y", Assert.IsType<ColumnReference>(sum.Right).ColumnName);
    }

    [Fact]
    public void Inline_ZeroArgUdf_BodyEmittedAsIs()
    {
        Expression result = Inline(
            "udf.zero()",
            "CREATE FUNCTION zero() AS 0");

        LiteralExpression literal = Assert.IsType<LiteralExpression>(result);
        Assert.Equal((sbyte)0, literal.Value);
    }

    [Fact]
    public void Inline_LiteralArg_InlinedDirectly()
    {
        Expression result = Inline(
            "udf.shout('hello')",
            "CREATE FUNCTION shout(s STRING) AS upper(s)");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(result);
        LiteralExpression arg = Assert.IsType<LiteralExpression>(call.Arguments[0]);
        Assert.Equal("hello", arg.Value);
    }

    [Fact]
    public void Inline_NestedUdfCall_FullyInlines()
    {
        // udf.b calls udf.a — after inlining, no UDF references remain.
        Expression result = Inline(
            "udf.b(name)",
            "CREATE FUNCTION a(s STRING) AS upper(s)",
            "CREATE FUNCTION b(s STRING) AS udf.a(s)");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("upper", call.FunctionName);
        Assert.DoesNotContain("udf.", QueryExplainer.FormatExpression(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inline_UdfInArgument_BothLevelsInlined()
    {
        // The argument to the outer UDF is itself a UDF call. The argument is
        // inlined first (children-first), then substituted into the outer body.
        // Avoid naming UDFs after SQL keywords like INNER / OUTER which the
        // parser reserves; UDFs are macro-substituted by the planner so the
        // *call sites* couldn't disambiguate them anyway.
        Expression result = Inline(
            "udf.scale(udf.bump(x))",
            "CREATE FUNCTION bump(v INT32) AS v + 1",
            "CREATE FUNCTION scale(v INT32) AS v * 2");

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
            "udf.greet(name)",
            "CREATE FUNCTION greet(n STRING) AS `Hello ${n}!`");

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
            "udf.add(x)",
            "CREATE FUNCTION add(a INT32, b INT32) AS a + b"));

        Assert.Contains("expects 2", ex.Message);
        Assert.Contains("got 1", ex.Message);
    }

    [Fact]
    public void Inline_TooManyArgs_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Inline(
            "udf.add(x, y, z)",
            "CREATE FUNCTION add(a INT32, b INT32) AS a + b"));
    }

    [Fact]
    public void Inline_UnregisteredUdf_Throws()
    {
        UdfRegistry empty = new();
        empty.Register(CreateUdf("CREATE FUNCTION other(x INT32) AS x")); // unrelated

        Expression call = ((SelectQueryExpression)SqlParser.Parse("SELECT udf.missing(x)")).Statement.Columns[0].Expression;
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => UdfInliner.Inline(call, empty));
        Assert.Contains("missing", ex.Message);
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
            "loop",
            new[] { new UdfParameter("x", "INT32") },
            null,
            // Body is udf.loop(x) — creates a self-cycle.
            new FunctionCallExpression("udf.loop",
                new[] { (Expression)new ColumnReference("x") })));

        Expression call = ((SelectQueryExpression)SqlParser.Parse("SELECT udf.loop(y)")).Statement.Columns[0].Expression;
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
            "a",
            new[] { new UdfParameter("x", "INT32") },
            null,
            new FunctionCallExpression("udf.b",
                new[] { (Expression)new ColumnReference("x") })));
        registry.Register(new UdfDescriptor(
            "b",
            new[] { new UdfParameter("x", "INT32") },
            null,
            new FunctionCallExpression("udf.a",
                new[] { (Expression)new ColumnReference("x") })));

        Expression call = ((SelectQueryExpression)SqlParser.Parse("SELECT udf.a(z)")).Statement.Columns[0].Expression;
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => UdfInliner.Inline(call, registry));
        Assert.Contains("a", ex.Message);
        Assert.Contains("b", ex.Message);
    }

    // ───────────────────── Shadowing ─────────────────────

    [Fact]
    public void Inline_LambdaShadowsUdfParam_LambdaParamWins()
    {
        // UDF param 'x' must not capture the lambda's bound 'x'.
        Expression result = Inline(
            "udf.f(arr)",
            // Body: array_transform(arr, x -> x * 2)
            // Param: arr ARRAY  — would naively try to substitute 'x' but
            // shouldn't, because the lambda binds its own 'x'.
            "CREATE FUNCTION f(x ARRAY) AS array_transform(x, y -> y * 2)");

        // After inlining, the lambda's body should still reference 'y' (the
        // lambda parameter), not the substituted UDF arg. We're testing that
        // substitution only touches the OUTER 'x' (replaced with 'arr'),
        // and leaves the lambda's body alone.
        FunctionCallExpression at = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("array_transform", at.FunctionName);
        // First arg is the UDF param 'x' substituted with the call arg 'arr'.
        ColumnReference firstArg = Assert.IsType<ColumnReference>(at.Arguments[0]);
        Assert.Equal("arr", firstArg.ColumnName);
        // Second arg is the lambda — its body must still have 'y'.
        LambdaExpression lambda = Assert.IsType<LambdaExpression>(at.Arguments[1]);
        BinaryExpression mul = Assert.IsType<BinaryExpression>(lambda.Body);
        Assert.Equal("y", Assert.IsType<ColumnReference>(mul.Left).ColumnName);
    }

    [Fact]
    public void Inline_LambdaParamSameNameAsUdfParam_LambdaScopeIsHonored()
    {
        // Trickier case: UDF param 'x' AND lambda param 'x'. The lambda's
        // body's 'x' must NOT be substituted.
        Expression result = Inline(
            "udf.f(arr)",
            "CREATE FUNCTION f(x ARRAY) AS array_transform(x, x -> x * 2)");

        FunctionCallExpression at = Assert.IsType<FunctionCallExpression>(result);
        // The first 'x' (the array) is in the outer scope and gets substituted.
        Assert.Equal("arr", Assert.IsType<ColumnReference>(at.Arguments[0]).ColumnName);
        // The lambda body's 'x' must still be 'x' — the lambda binds it.
        LambdaExpression lambda = Assert.IsType<LambdaExpression>(at.Arguments[1]);
        BinaryExpression mul = Assert.IsType<BinaryExpression>(lambda.Body);
        Assert.Equal("x", Assert.IsType<ColumnReference>(mul.Left).ColumnName);
    }

    // ───────────────────── Top-level query inlining ─────────────────────

    [Fact]
    public void Inline_QueryExpression_RewritesSelectListAndWhere()
    {
        UdfRegistry registry = new();
        registry.Register(CreateUdf("CREATE FUNCTION shout(s STRING) AS upper(s)"));

        QueryExpression q = SqlParser.Parse(
            "SELECT udf.shout(name) FROM users WHERE udf.shout(role) = 'ADMIN'");
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
            "CREATE FUNCTION roll() AS random_float32(0.7, 1.0)"));

        // Same call site twice — both inline to independent random_float32 calls.
        QueryExpression q = SqlParser.Parse(
            "SELECT udf.roll() AS a, udf.roll() AS b FROM dual");
        QueryExpression inlined = UdfInliner.Inline(q, registry);

        SelectStatement stmt = ((SelectQueryExpression)inlined).Statement;
        FunctionCallExpression aCall = Assert.IsType<FunctionCallExpression>(stmt.Columns[0].Expression);
        FunctionCallExpression bCall = Assert.IsType<FunctionCallExpression>(stmt.Columns[1].Expression);
        Assert.Equal("random_float32", aCall.FunctionName);
        Assert.Equal("random_float32", bCall.FunctionName);
        // Two AST nodes — not a shared reference. Each call site evaluates
        // independently; the planner will not CSE them.
        Assert.NotSame(aCall, bCall);
    }
}
