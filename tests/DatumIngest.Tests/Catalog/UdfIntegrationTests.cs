using DatumIngest.Catalog;
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

        catalog.Plan("CREATE FUNCTION shout(@name STRING) AS upper(@name)");

        Assert.True(catalog.Udfs.TryGet("shout", out UdfDescriptor? udf));
        Assert.Equal("shout", udf!.Name);
        Assert.Single(udf.Parameters);
        Assert.Equal("name", udf.Parameters[0].Name);
    }

    [Fact]
    public void CreateFunction_CaseInsensitiveLookup()
    {
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE FUNCTION Shout(@name STRING) AS upper(@name)");

        Assert.True(catalog.Udfs.TryGet("SHOUT", out _));
        Assert.True(catalog.Udfs.TryGet("shout", out _));
    }

    [Fact]
    public void CreateFunction_DuplicateName_Throws()
    {
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE FUNCTION shout(@s STRING) AS upper(@s)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("CREATE FUNCTION shout(@s STRING) AS lower(@s)"));
        Assert.Contains("already registered", ex.Message);
    }

    [Fact]
    public void CreateFunction_OrReplace_OverwritesExisting()
    {
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE FUNCTION shout(@s STRING) AS upper(@s)");
        catalog.Plan("CREATE OR REPLACE FUNCTION shout(@s STRING) AS lower(@s)");

        Assert.True(catalog.Udfs.TryGet("shout", out UdfDescriptor? udf));
        // Body changed from upper to lower — verify by formatting.
        string body = DatumIngest.Execution.QueryExplainer.FormatExpression(udf!.Body);
        Assert.Contains("lower", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateFunction_IfNotExists_NoOpWhenAlreadyRegistered()
    {
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE FUNCTION shout(@s STRING) AS upper(@s)");
        // Second registration is a no-op; original definition wins.
        catalog.Plan("CREATE FUNCTION IF NOT EXISTS shout(@s STRING) AS lower(@s)");

        Assert.True(catalog.Udfs.TryGet("shout", out UdfDescriptor? udf));
        string body = DatumIngest.Execution.QueryExplainer.FormatExpression(udf!.Body);
        Assert.Contains("upper", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DropFunction_RemovesUdf()
    {
        TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE FUNCTION shout(@s STRING) AS upper(@s)");
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
    public void CreateFunction_DirectSelfReference_RejectedAtRegistration()
    {
        TableCatalog catalog = CreateCatalog();

        // The body references udf.loop — itself. Should be rejected at
        // registration time because the body validator runs the inliner
        // against the (currently empty) registry, where udf.loop is unknown.
        // Once we attempt to register it, it's not yet in the registry, so
        // the body's reference can't resolve — caught as "not registered".
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("CREATE FUNCTION loop(@x INT32) AS udf.loop(@x)"));
        Assert.Contains("loop", ex.Message);
    }

    [Fact]
    public void Plan_QueryWithUdfCall_IsInlined()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id", "name"],
            new object[] { 1, "alice" },
            new object[] { 2, "bob" });

        catalog.Plan("CREATE FUNCTION shout(@s STRING) AS upper(@s)");

        // Plan a query that uses the UDF. After Plan, the operator tree
        // should contain `upper(name)` (the substituted body), not a UDF
        // call.
        IQueryPlan plan = catalog.Plan("SELECT udf.shout(name) FROM orders");
        ExplainPlanNode tree = plan.ExplainTree;

        // The plan's text representation should reference upper(name),
        // not udf.shout — the inliner ran successfully.
        string text = ExplainToText(tree);
        Assert.Contains("upper", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("udf.", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_QueryReferencingUnknownUdf_Throws()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["id", "name"],
            new object[] { 1, "alice" });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("SELECT udf.never_defined(name) FROM orders"));
        Assert.Contains("never_defined", ex.Message);
    }

    [Fact]
    public void Plan_BatchUnsupported_ReportsClearly()
    {
        // Plan accepts a single statement; batch handling is the host's job.
        // Confirm a multi-statement input is rejected with a parse error
        // rather than silently using the first statement.
        TableCatalog catalog = CreateCatalog();

        Assert.ThrowsAny<Exception>(() => catalog.Plan(
            "CREATE FUNCTION a(@x INT32) AS @x; CREATE FUNCTION b(@y INT32) AS @y"));
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
