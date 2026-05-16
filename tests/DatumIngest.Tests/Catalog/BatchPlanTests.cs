using DatumIngest.Catalog;
using DatumIngest.Catalog.Plans;
using DatumIngest.Data;
using DatumIngest.Execution;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// End-to-end tests for <see cref="StatementBatch"/> and the
/// auto-detecting <see cref="TableCatalog.PrepareAsync(string)"/> entry.
/// Multi-statement scripts that mix state-creation with state-reads
/// (<c>CREATE; INSERT; SELECT</c>) are the central case — the batch
/// plans each child against catalog state that already reflects all
/// prior children's iteration.
/// </summary>
public sealed class BatchPlanTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public BatchPlanTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-batch-plan-tests-{Guid.NewGuid():N}");
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
    public async Task PrepareAsync_SingleStatement_ReturnsStatementPlan()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        PreparedSql prepared = await catalog.PrepareAsync(
            "CREATE TABLE public.t (id INT32 NOT NULL)");

        Assert.IsAssignableFrom<StatementPlan>(prepared);
        Assert.IsNotType<StatementBatch>(prepared);
    }

    [Fact]
    public async Task PrepareAsync_MultiStatement_ReturnsStatementBatch()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        PreparedSql prepared = await catalog.PrepareAsync(
            "CREATE TABLE public.t (id INT32 NOT NULL); " +
            "CREATE FUNCTION public.dbl(x INT32) RETURNS INT32 AS x * 2");

        StatementBatch batch = Assert.IsType<StatementBatch>(prepared);
        Assert.Equal(2, batch.Entries.Count);
    }

    [Fact]
    public async Task PrepareAsync_EmptyInput_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        await Assert.ThrowsAsync<ArgumentException>(() => catalog.PrepareAsync(""));
    }

    [Fact]
    public async Task StatementBatch_AppliesChildrenInSourceOrderWithStateDependencies()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        PreparedSql prepared = await catalog.PrepareAsync(
            "CREATE TABLE public.t (id INT32 NOT NULL); " +
            "INSERT INTO public.t VALUES (1), (2), (3); " +
            "SELECT id FROM public.t ORDER BY id");

        StatementBatch batch = Assert.IsType<StatementBatch>(prepared);

        using InProcessDatumDbConnection connection = new(catalog);
        using DatumIngest.Execution.ExecutionContext ctx = catalog.CreateExecutionContext();
        ctx.Accountant.StartProfiling();
        await using InProcessDatumDbReader reader = await InProcessDatumDbReader_OpenForTest(
            batch, ctx);

        // Step over result sets until we hit the SELECT (third statement).
        // CREATE / INSERT have no rows; SELECT does.
        List<int> finalRows = [];
        do
        {
            while (await reader.ReadAsync())
            {
                finalRows.Add(reader.GetInt32(0));
            }
        }
        while (await reader.NextResultAsync());

        Assert.Equal([1, 2, 3], finalRows);
        Assert.True(catalog.HasTable("public.t"));
    }

    [Fact]
    public async Task StatementBatch_NextResultAsync_AdvancesBetweenStatements()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        // Two SELECTs back-to-back so both yield rows; tests that
        // NextResultAsync properly advances to a fresh result set with a
        // potentially different schema.
        await Seed(catalog,
            "CREATE TABLE public.a (id INT32 NOT NULL)",
            "INSERT INTO public.a VALUES (1), (2)",
            "CREATE TABLE public.b (name STRING NOT NULL)",
            "INSERT INTO public.b VALUES ('x'), ('y'), ('z')");

        PreparedSql prepared = await catalog.PrepareAsync(
            "SELECT id FROM public.a ORDER BY id; " +
            "SELECT name FROM public.b ORDER BY name");
        StatementBatch batch = Assert.IsType<StatementBatch>(prepared);

        using DatumIngest.Execution.ExecutionContext ctx = catalog.CreateExecutionContext();
        ctx.Accountant.StartProfiling();
        await using InProcessDatumDbReader reader = await InProcessDatumDbReader_OpenForTest(
            batch, ctx);

        List<int> first = [];
        while (await reader.ReadAsync()) first.Add(reader.GetInt32(0));
        Assert.Equal([1, 2], first);

        Assert.True(await reader.NextResultAsync());

        List<string> second = [];
        Assert.Equal("name", reader.GetName(0));
        while (await reader.ReadAsync()) second.Add(reader.GetString(0));
        Assert.Equal(["x", "y", "z"], second);

        Assert.False(await reader.NextResultAsync());
    }

    [Fact]
    public async Task StatementBatch_ExecuteNonQueryAsync_DrainsAllChildren()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "CREATE TABLE public.t (id INT32 NOT NULL); " +
            "INSERT INTO public.t VALUES (10), (20), (30)");

        await command.ExecuteNonQueryAsync();

        Assert.True(catalog.HasTable("public.t"));

        using InProcessDatumDbCommand readback = connection.CreateCommand(
            "SELECT id FROM public.t ORDER BY id");
        List<int> ids = [];
        await using InProcessDatumDbReader reader = await readback.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
        Assert.Equal([10, 20, 30], ids);
    }

    [Fact]
    public async Task StatementBatch_ExplainTree_ListsChildrenWithOperatorLabels()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        PreparedSql prepared = await catalog.PrepareAsync(
            "CREATE TABLE public.t (id INT32 NOT NULL); " +
            "DROP TABLE public.t");
        StatementBatch batch = Assert.IsType<StatementBatch>(prepared);

        Assert.Equal("Batch", batch.ExplainTree.OperatorName);
        Assert.Equal("2 statement(s)", batch.ExplainTree.Details);
        Assert.Equal(2, batch.ExplainTree.Children.Count);
        Assert.Equal("CreateTable", batch.ExplainTree.Children[0].OperatorName);
        Assert.Equal("DropTable", batch.ExplainTree.Children[1].OperatorName);
    }

    private static Task<InProcessDatumDbReader> InProcessDatumDbReader_OpenForTest(
        PreparedSql prepared, DatumIngest.Execution.ExecutionContext ctx)
        => InProcessDatumDbReader.OpenAsync(prepared, ctx, ownsContext: false, CancellationToken.None);

    private static async Task Seed(TableCatalog catalog, params string[] sqls)
    {
        foreach (string sql in sqls)
        {
            StatementPlan plan = await catalog.PlanAsync(sql);
            await catalog.ExecuteAsync(plan).DrainAsync();
        }
    }
}
