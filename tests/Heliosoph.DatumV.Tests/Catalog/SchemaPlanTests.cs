using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Pins the side-effect-free contract of <see cref="SchemaPlan"/>:
/// <see cref="TableCatalog.PlanAsync(string)"/> for CREATE/DROP SCHEMA
/// and SET search_path must not mutate the catalog's backend routing or
/// session search path until the returned plan is iterated.
/// </summary>
public sealed class SchemaPlanTests : ServiceTestBase
{
    [Fact]
    public async Task PlanAsync_CreateSchema_DoesNotMountUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog();

        StatementPlan plan = await catalog.PlanAsync("CREATE SCHEMA myapp");

        Assert.IsType<SchemaPlan>(plan);
        Assert.Equal("CreateSchema", plan.ExplainTree.OperatorName);
        Assert.False(catalog.Backends.ContainsKey("myapp"),
            "PlanAsync must not mount the schema — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(catalog.Backends.ContainsKey("myapp"));
    }

    [Fact]
    public async Task PlanAsync_DropSchema_DoesNotUnmountUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog();
        await Drain(catalog, "CREATE SCHEMA myapp");
        Assert.True(catalog.Backends.ContainsKey("myapp"));

        StatementPlan plan = await catalog.PlanAsync("DROP SCHEMA myapp");

        Assert.IsType<SchemaPlan>(plan);
        Assert.Equal("DropSchema", plan.ExplainTree.OperatorName);
        Assert.True(catalog.Backends.ContainsKey("myapp"),
            "PlanAsync must not unmount the schema — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.False(catalog.Backends.ContainsKey("myapp"));
    }

    [Fact]
    public async Task PlanAsync_SetSearchPath_DoesNotMutateUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog();
        await Drain(catalog, "CREATE SCHEMA myapp");
        IReadOnlyList<string> originalSearchPath = catalog.SearchPath;

        StatementPlan plan = await catalog.PlanAsync("SET search_path = myapp, public");

        Assert.IsType<SchemaPlan>(plan);
        Assert.Equal("SetSearchPath", plan.ExplainTree.OperatorName);
        Assert.Equal(originalSearchPath, catalog.SearchPath);

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.Equal(["myapp", "public"], catalog.SearchPath);
    }

    [Fact]
    public async Task SchemaPlan_DoubleExecute_Throws()
    {
        using TableCatalog catalog = CreateCatalog();

        StatementPlan plan = await catalog.PlanAsync("CREATE SCHEMA myapp");

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
