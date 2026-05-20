using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Catalog.Registries;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Pins the side-effect-free contract of <see cref="RoutinePlan"/>:
/// <see cref="TableCatalog.PlanAsync(string)"/> for CREATE/DROP FUNCTION
/// and CREATE/DROP PROCEDURE must not mutate the routine registries
/// until the returned plan is iterated.
/// </summary>
public sealed class RoutinePlanTests : ServiceTestBase
{
    [Fact]
    public async Task PlanAsync_CreateFunction_DoesNotRegisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog();

        StatementPlan plan = await catalog.PlanAsync(
            "CREATE FUNCTION public.dbl(x INT32) RETURNS INT32 AS x * 2");

        Assert.IsType<RoutinePlan>(plan);
        Assert.Equal("CreateFunction", plan.ExplainTree.OperatorName);
        Assert.False(catalog.Udfs.TryGet("dbl", out _),
            "PlanAsync must not register the UDF — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(catalog.Udfs.TryGet("dbl", out _));
    }

    [Fact]
    public async Task PlanAsync_DropFunction_DoesNotUnregisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog();
        await Drain(catalog, "CREATE FUNCTION public.dbl(x INT32) RETURNS INT32 AS x * 2");
        Assert.True(catalog.Udfs.TryGet("dbl", out _));

        StatementPlan plan = await catalog.PlanAsync("DROP FUNCTION public.dbl");

        Assert.IsType<RoutinePlan>(plan);
        Assert.Equal("DropFunction", plan.ExplainTree.OperatorName);
        Assert.True(catalog.Udfs.TryGet("dbl", out _),
            "PlanAsync must not unregister the UDF — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.False(catalog.Udfs.TryGet("dbl", out _));
    }

    [Fact]
    public async Task PlanAsync_CreateProcedure_DoesNotRegisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog();

        StatementPlan plan = await catalog.PlanAsync(
            "CREATE PROCEDURE public.noop() AS BEGIN SELECT 1 END");

        Assert.IsType<RoutinePlan>(plan);
        Assert.Equal("CreateProcedure", plan.ExplainTree.OperatorName);
        Assert.False(catalog.Procedures.TryGet("noop", out _),
            "PlanAsync must not register the procedure — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(catalog.Procedures.TryGet("noop", out _));
    }

    [Fact]
    public async Task PlanAsync_DropProcedure_DoesNotUnregisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog();
        await Drain(catalog, "CREATE PROCEDURE public.noop() AS BEGIN SELECT 1 END");
        Assert.True(catalog.Procedures.TryGet("noop", out _));

        StatementPlan plan = await catalog.PlanAsync("DROP PROCEDURE public.noop");

        Assert.IsType<RoutinePlan>(plan);
        Assert.Equal("DropProcedure", plan.ExplainTree.OperatorName);
        Assert.True(catalog.Procedures.TryGet("noop", out _),
            "PlanAsync must not unregister the procedure — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.False(catalog.Procedures.TryGet("noop", out _));
    }

    [Fact]
    public async Task RoutinePlan_DoubleExecute_Throws()
    {
        using TableCatalog catalog = CreateCatalog();

        StatementPlan plan = await catalog.PlanAsync(
            "CREATE FUNCTION public.dbl(x INT32) RETURNS INT32 AS x * 2");

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
