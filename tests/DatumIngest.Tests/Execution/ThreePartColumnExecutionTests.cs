using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// S6: end-to-end execution of <c>schema.table.column</c> column
/// references. Three-part qualification has to round-trip through the
/// parser, the planner's column resolver, and the runtime evaluator.
/// </summary>
public sealed class ThreePartColumnExecutionTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public ThreePartColumnExecutionTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-3part-{Guid.NewGuid():N}");
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
    public async Task ThreePartColumn_UnaliasedTable_ResolvesAtRuntime()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE users (id Int32, name String)");

        IQueryPlan plan = catalog.Plan("SELECT public.users.id FROM public.users");
        await foreach (RowBatch _ in plan.ExecuteAsync(CancellationToken.None)) { }
        // No exception = success criterion. Empty table → zero batches.
    }
}
