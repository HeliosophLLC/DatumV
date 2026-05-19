using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Pins the side-effect-free contract of <see cref="ViewPlan"/>:
/// <see cref="TableCatalog.PlanAsync(string)"/> for CREATE/DROP VIEW
/// must not mutate the view registry until the returned plan is
/// iterated.
/// </summary>
public sealed class ViewPlanTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public ViewPlanTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-view-plan-tests-{Guid.NewGuid():N}");
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
    public async Task PlanAsync_CreateView_DoesNotRegisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");

        StatementPlan plan = await catalog.PlanAsync(
            "CREATE VIEW public.v AS SELECT id FROM public.t");

        Assert.IsType<ViewPlan>(plan);
        Assert.Equal("CreateView", plan.ExplainTree.OperatorName);
        Assert.False(catalog.Views.TryGet(new QualifiedName("public", "v"), out _),
            "PlanAsync must not register the view — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(catalog.Views.TryGet(new QualifiedName("public", "v"), out _));
    }

    [Fact]
    public async Task PlanAsync_DropView_DoesNotUnregisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");
        await Drain(catalog, "CREATE VIEW public.v AS SELECT id FROM public.t");
        Assert.True(catalog.Views.TryGet(new QualifiedName("public", "v"), out _));

        StatementPlan plan = await catalog.PlanAsync("DROP VIEW public.v");

        Assert.IsType<ViewPlan>(plan);
        Assert.Equal("DropView", plan.ExplainTree.OperatorName);
        Assert.True(catalog.Views.TryGet(new QualifiedName("public", "v"), out _),
            "PlanAsync must not unregister the view — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.False(catalog.Views.TryGet(new QualifiedName("public", "v"), out _));
    }

    [Fact]
    public async Task ViewPlan_DoubleExecute_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");

        StatementPlan plan = await catalog.PlanAsync(
            "CREATE VIEW public.v AS SELECT id FROM public.t");

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
