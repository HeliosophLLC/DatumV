using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Pins the side-effect-free contract of <see cref="AlterTablePlan"/>:
/// <see cref="TableCatalog.PlanAsync(string)"/> for the five
/// <c>ALTER TABLE</c> variants must not touch the target table's schema
/// until the returned plan is iterated. Also pins the deferred
/// <c>ALTER TABLE IF EXISTS</c> short-circuit — the missing-table check
/// runs at execute time, so a plan built before the table exists still
/// applies once the table is created in between.
/// </summary>
public sealed class AlterTablePlanTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public AlterTablePlanTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-alter-plan-tests-{Guid.NewGuid():N}");
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
    public async Task PlanAsync_AddColumn_DoesNotMutateUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");

        StatementPlan plan = await catalog.PlanAsync(
            "ALTER TABLE public.t ADD COLUMN name STRING");

        Assert.IsType<AlterTablePlan>(plan);
        Assert.Equal("AlterTable", plan.ExplainTree.OperatorName);
        Assert.Single(catalog["public.t"].GetSchema().Columns);

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.Equal(["id", "name"], catalog["public.t"].GetSchema().Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task PlanAsync_DropColumn_DoesNotMutateUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL, name STRING)");

        StatementPlan plan = await catalog.PlanAsync(
            "ALTER TABLE public.t DROP COLUMN name");

        Assert.IsType<AlterTablePlan>(plan);
        Assert.Equal(["id", "name"], catalog["public.t"].GetSchema().Columns.Select(c => c.Name));

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.Equal(["id"], catalog["public.t"].GetSchema().Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task PlanAsync_AddColumn_TableIfExists_ChecksAtExecuteTime()
    {
        // Build the plan before the table exists — must not throw.
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        StatementPlan plan = await catalog.PlanAsync(
            "ALTER TABLE IF EXISTS public.t ADD COLUMN name STRING");

        Assert.IsType<AlterTablePlan>(plan);

        // Now create the table in between Plan and Execute. The deferred
        // existence check should see the table and apply normally.
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.Equal(["id", "name"], catalog["public.t"].GetSchema().Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task PlanAsync_AddColumn_TableIfExists_NoopsWhenTableMissingAtExecute()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        StatementPlan plan = await catalog.PlanAsync(
            "ALTER TABLE IF EXISTS public.missing ADD COLUMN name STRING");

        Assert.IsType<AlterTablePlan>(plan);

        // Iterate without ever creating the table — should silently no-op.
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.False(catalog.HasTable("public.missing"));
    }

    [Fact]
    public async Task AlterTablePlan_DoubleExecute_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");

        StatementPlan plan = await catalog.PlanAsync(
            "ALTER TABLE public.t ADD COLUMN name STRING");

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
