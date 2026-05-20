using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Pins the side-effect-free contract of <see cref="TablePlan"/>:
/// <see cref="TableCatalog.PlanAsync(string)"/> for CREATE/DROP TABLE
/// must not register or unregister the table provider (or touch the
/// underlying <c>.datum</c> file) until the returned plan is iterated.
/// </summary>
public sealed class TablePlanTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public TablePlanTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-table-plan-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task PlanAsync_CreateTable_DoesNotRegisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        StatementPlan plan = await catalog.PlanAsync(
            "CREATE TABLE public.t (id INT32 NOT NULL, name STRING)");

        Assert.IsType<TablePlan>(plan);
        Assert.Equal("CreateTable", plan.ExplainTree.OperatorName);
        Assert.False(catalog.HasTable("public.t"),
            "PlanAsync must not register the table — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(catalog.HasTable("public.t"));
    }

    [Fact]
    public async Task PlanAsync_CreateTempTable_DoesNotRegisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        StatementPlan plan = await catalog.PlanAsync(
            "CREATE TEMP TABLE t (id INT32 NOT NULL)");

        Assert.IsType<TablePlan>(plan);
        Assert.False(catalog.HasTable("public.t"));

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(catalog.HasTable("public.t"));
    }

    [Fact]
    public async Task PlanAsync_DropTable_DoesNotUnregisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");
        Assert.True(catalog.HasTable("public.t"));

        StatementPlan plan = await catalog.PlanAsync("DROP TABLE public.t");

        Assert.IsType<TablePlan>(plan);
        Assert.Equal("DropTable", plan.ExplainTree.OperatorName);
        Assert.True(catalog.HasTable("public.t"),
            "PlanAsync must not unregister the table — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.False(catalog.HasTable("public.t"));
    }

    [Fact]
    public async Task TablePlan_DoubleExecute_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        StatementPlan plan = await catalog.PlanAsync(
            "CREATE TABLE public.t (id INT32 NOT NULL)");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in catalog.ExecuteAsync(plan)) { }
        });
    }

    private static async Task Drain(TableCatalog catalog, string sql)
    {
        StatementPlan plan = await catalog.PlanAsync(sql);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }
    }
}
