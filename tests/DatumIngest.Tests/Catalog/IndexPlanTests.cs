using DatumIngest.Catalog;
using DatumIngest.Catalog.Plans;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Pins the side-effect-free contract of <see cref="IndexPlan"/>:
/// <see cref="TableCatalog.PlanAsync(string)"/> for CREATE/DROP INDEX,
/// REINDEX, and ANALYZE must not touch the table's acceleration
/// sidecars until the returned plan is iterated.
/// </summary>
public sealed class IndexPlanTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public IndexPlanTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-index-plan-tests-{Guid.NewGuid():N}");
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
    public async Task PlanAsync_CreateIndex_DoesNotRegisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL, name STRING)");

        StatementPlan plan = await catalog.PlanAsync(
            "CREATE INDEX ix_t_id ON public.t (id)");

        Assert.IsType<IndexPlan>(plan);
        Assert.Equal("CreateIndex", plan.ExplainTree.OperatorName);
        Assert.False(HasIndex(catalog, "public.t", "ix_t_id"),
            "PlanAsync must not register the index — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(HasIndex(catalog, "public.t", "ix_t_id"));
    }

    [Fact]
    public async Task PlanAsync_DropIndex_DoesNotUnregisterUntilIterated()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");
        await Drain(catalog, "CREATE INDEX ix_t_id ON public.t (id)");
        Assert.True(HasIndex(catalog, "public.t", "ix_t_id"));

        StatementPlan plan = await catalog.PlanAsync("DROP INDEX ix_t_id");

        Assert.IsType<IndexPlan>(plan);
        Assert.Equal("DropIndex", plan.ExplainTree.OperatorName);
        Assert.True(HasIndex(catalog, "public.t", "ix_t_id"),
            "PlanAsync must not unregister the index — the side effect belongs to ExecuteAsync.");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.False(HasIndex(catalog, "public.t", "ix_t_id"));
    }

    [Fact]
    public async Task PlanAsync_Reindex_PlansWithoutFiring()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");

        StatementPlan plan = await catalog.PlanAsync("REINDEX TABLE public.t");

        Assert.IsType<IndexPlan>(plan);
        Assert.Equal("Reindex", plan.ExplainTree.OperatorName);

        // Iterating must not throw — REINDEX is valid on the .datum table.
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }
    }

    [Fact]
    public async Task PlanAsync_Analyze_PlansWithoutFiring()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");

        StatementPlan plan = await catalog.PlanAsync("ANALYZE public.t");

        Assert.IsType<IndexPlan>(plan);
        Assert.Equal("Analyze", plan.ExplainTree.OperatorName);

        // Iterating must not throw.
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }
    }

    [Fact]
    public async Task IndexPlan_DoubleExecute_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Drain(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");

        StatementPlan plan = await catalog.PlanAsync(
            "CREATE INDEX ix_t_id ON public.t (id)");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in catalog.ExecuteAsync(plan)) { }
        });
    }

    private static bool HasIndex(TableCatalog catalog, string tableName, string indexName)
    {
        IReadOnlyList<IndexDescriptor>? indexes = catalog.GetTableIndexes(tableName);
        return indexes is not null && indexes.Any(ix =>
            string.Equals(ix.Name, indexName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task Drain(TableCatalog catalog, string sql)
    {
        StatementPlan plan = await catalog.PlanAsync(sql);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }
    }
}
