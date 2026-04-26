using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// End-to-end tests for UDF DDL through <see cref="TableCatalog.Plan(string)"/>:
/// registration, removal, error surfaces, and verification that subsequent
/// queries see UDFs inlined at plan time.
/// </summary>
/// <remarks>
/// These tests stop at plan construction and inspect catalog state — actual
/// execution is covered indirectly by the inliner tests against the AST.
/// Operator-level execution tests hit pre-existing arena-migration regressions
/// in the engine that are unrelated to UDFs.
/// </remarks>
public class UdfIntegrationTests : ServiceTestBase
{
    [Fact]
    public void CreateFunction_RegistersUdfInCatalog()
    {
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE FUNCTION shout(name STRING) AS upper(name)");

        Assert.True(catalog.Udfs.TryGet("shout", out UdfDescriptor? udf));
        Assert.Equal("shout", udf!.Name);
        Assert.Single(udf.Parameters);
        Assert.Equal("name", udf.Parameters[0].Name);
    }

    [Fact]
    public void CreateFunction_CaseInsensitiveLookup()
    {
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE FUNCTION Shout(name STRING) AS upper(name)");

        Assert.True(catalog.Udfs.TryGet("SHOUT", out _));
        Assert.True(catalog.Udfs.TryGet("shout", out _));
    }

    [Fact]
    public void CreateFunction_DuplicateName_Throws()
    {
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE FUNCTION shout(s STRING) AS upper(s)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("CREATE FUNCTION shout(s STRING) AS lower(s)"));
        Assert.Contains("already registered", ex.Message);
    }

    [Fact]
    public void CreateFunction_OrReplace_OverwritesExisting()
    {
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE FUNCTION shout(s STRING) AS upper(s)");
        catalog.Plan("CREATE OR REPLACE FUNCTION shout(s STRING) AS lower(s)");

        Assert.True(catalog.Udfs.TryGet("shout", out UdfDescriptor? udf));
        // Body changed from upper to lower — verify by formatting.
        string body = DatumIngest.Execution.QueryExplainer.FormatExpression(udf!.ExpressionBody!);
        Assert.Contains("lower", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateFunction_OrAlter_OverwritesExisting()
    {
        // OR ALTER is a T-SQL synonym for OR REPLACE — should behave identically.
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE FUNCTION shout(s STRING) AS upper(s)");
        catalog.Plan("CREATE OR ALTER FUNCTION shout(s STRING) AS lower(s)");

        Assert.True(catalog.Udfs.TryGet("shout", out UdfDescriptor? udf));
        string body = DatumIngest.Execution.QueryExplainer.FormatExpression(udf!.ExpressionBody!);
        Assert.Contains("lower", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateFunction_IfNotExists_NoOpWhenAlreadyRegistered()
    {
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE FUNCTION shout(s STRING) AS upper(s)");
        // Second registration is a no-op; original definition wins.
        catalog.Plan("CREATE FUNCTION IF NOT EXISTS shout(s STRING) AS lower(s)");

        Assert.True(catalog.Udfs.TryGet("shout", out UdfDescriptor? udf));
        string body = DatumIngest.Execution.QueryExplainer.FormatExpression(udf!.ExpressionBody!);
        Assert.Contains("upper", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DropFunction_RemovesUdf()
    {
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE FUNCTION shout(s STRING) AS upper(s)");
        catalog.Plan("DROP FUNCTION shout");

        Assert.False(catalog.Udfs.TryGet("shout", out _));
    }

    [Fact]
    public void DropFunction_NonExistent_Throws()
    {
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("DROP FUNCTION never_registered"));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void DropFunction_IfExists_NoOpWhenAbsent()
    {
        TableCatalog catalog = CreateCatalog();

        // Should not throw.
        catalog.Plan("DROP FUNCTION IF EXISTS never_registered");

        Assert.False(catalog.Udfs.TryGet("never_registered", out _));
    }

    [Fact]
    public void CreateFunction_DirectSelfReference_RejectedAtFirstCall()
    {
        // Post-S7d the registration-time pre-flight inliner no longer
        // errors on unresolved references (calls just pass through and
        // resolve at evaluation time). The cycle is caught the first time
        // the UDF is referenced from a query: the inliner recurses into
        // the substituted body, sees itself on the inlining stack, and
        // throws.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION loop(x INT32) AS loop(x)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("SELECT loop(1)"));
        Assert.Contains("Cyclic UDF reference", ex.Message);
    }

    [Fact]
    public void Plan_QueryWithUdfCall_IsInlined()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id", "name"],
            new object[] { 1, "alice" },
            new object[] { 2, "bob" });

        catalog.Plan("CREATE FUNCTION shout(s STRING) AS upper(s)");

        // Plan a query that uses the UDF. After Plan, the operator tree
        // should contain `upper(name)` (the substituted body), not a UDF
        // call.
        IQueryPlan plan = catalog.Plan("SELECT shout(name) FROM orders");
        ExplainPlanNode tree = plan.ExplainTree;

        // The plan's text representation should reference upper(name),
        // not shout — the inliner ran successfully.
        string text = ExplainToText(tree);
        Assert.Contains("upper", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("udf.", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateFunction_ProceduralBody_BadArity_ThrowsAtDdlTime()
    {
        // ProceduralBodyArityGate walks RETURN / SET / IF predicates and
        // DECLARE initializers. A `concat(text)` call (single arg, needs
        // ≥2) inside the body should now fail at CREATE FUNCTION rather
        // than at the first call site.
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE FUNCTION mash(text String) RETURNS String AS BEGIN RETURN concat(text) END"));
        Assert.Contains("udf.mash", ex.Message);
        Assert.Contains("concat", ex.Message);
    }

    [Fact]
    public void CreateFunction_ProceduralBody_GoodArityWithLocal_Succeeds()
    {
        // Locals introduced via DECLARE get tracked into the scope so a
        // subsequent `concat(local, 'x')` call resolves and validates
        // cleanly. This is the realistic body shape — params get massaged
        // into locals before they hit further function calls.
        TableCatalog catalog = CreateCatalog();

        catalog.Plan(
            "CREATE FUNCTION decorate(text String) RETURNS String AS BEGIN " +
            "DECLARE prefix String = 'hello-'; " +
            "RETURN concat(prefix, text) " +
            "END");

        Assert.True(catalog.Udfs.TryGet("decorate", out _));
    }

    [Fact]
    public void Plan_QueryReferencingUnknownFunction_ThrowsAtPlanTime()
    {
        // PlanTimeFunctionGate rejects unknown function names before any
        // operator is built. The runtime "Unknown function" path stays as a
        // backstop, but users hit the diagnostic instantly — before a
        // neighbor projection like `models.X(...)` warms an ONNX session.
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id", "name"],
            new object[] { 1, "alice" });

        Exception ex = Assert.ThrowsAny<Exception>(
            () => catalog.Plan("SELECT never_defined(name) FROM orders"));
        Assert.Contains("never_defined", ex.Message);
        Assert.Contains("Unknown function", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_ScalarPositionTableValuedFunction_RedirectsToFromClause()
    {
        // A TVF used as a scalar expression should get the same helpful
        // "use it in a FROM clause" nudge the runtime evaluator gives.
        // `range` is registered as a TVF (FunctionRegistry.CreateDefault).
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id"],
            new object[] { 1 });

        Exception ex = Assert.ThrowsAny<Exception>(
            () => catalog.Plan("SELECT range(10) FROM orders"));
        Assert.Contains("range", ex.Message);
        Assert.Contains("table-valued function", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FROM clause", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_BatchUnsupported_ReportsClearly()
    {
        // Plan accepts a single statement; batch handling is the host's job.
        // Confirm a multi-statement input is rejected with a parse error
        // rather than silently using the first statement.
        TableCatalog catalog = CreateCatalog();

        Assert.ThrowsAny<Exception>(() => catalog.Plan(
            "CREATE FUNCTION a(x INT32) AS x; CREATE FUNCTION b(y INT32) AS y"));
    }

    // ───────────────────── Default parameters ─────────────────────

    [Fact]
    public void CreateFunction_NonContiguousDefaults_RejectedAtRegistration()
    {
        // A required parameter after a defaulted one would make positional
        // argument matching ambiguous. The catalog rejects the shape eagerly.
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan(
                "CREATE FUNCTION mid(a INT32, b INT32 = 0, c INT32) AS a + b + c"));
        Assert.Contains("contiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("c", ex.Message);
    }

    [Fact]
    public void CreateFunction_DefaultsAtTail_AcceptedAndQueryable()
    {
        // `add(2)` should fill `b` from its default of 5 → 7. We can't
        // execute against an empty catalog, but we can verify a SELECT
        // referencing the partial-arity call plans without complaint.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION addnums(a INT32, b INT32 = 5) AS a + b");

        // Plan a SELECT that calls the UDF with only the required arg.
        // Inlining happens at plan time, so a malformed default would
        // throw here.
        catalog.Plan("SELECT addnums(2)");
        catalog.Plan("SELECT addnums(2, 10)");
    }

    [Fact]
    public void CreateFunction_TooFewArgs_BelowMinimum_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION addnums(a INT32, b INT32 = 0) AS a + b");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("SELECT addnums()"));
        Assert.Contains("addnums", ex.Message);
    }

    [Fact]
    public void CreateFunction_TooManyArgs_AboveMaximum_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION addnums(a INT32, b INT32 = 0) AS a + b");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("SELECT addnums(1, 2, 3)"));
        Assert.Contains("addnums", ex.Message);
    }

    private static string ExplainToText(ExplainPlanNode node)
    {
        System.Text.StringBuilder sb = new();
        WriteNode(sb, node, depth: 0);
        return sb.ToString();
    }

    private static void WriteNode(System.Text.StringBuilder sb, ExplainPlanNode node, int depth)
    {
        sb.Append(' ', depth * 2);
        sb.Append(node.OperatorName);
        if (!string.IsNullOrEmpty(node.Details))
        {
            sb.Append(' ').Append(node.Details);
        }
        sb.AppendLine();
        foreach (ExplainPlanNode child in node.Children)
        {
            WriteNode(sb, child, depth + 1);
        }
    }
}
