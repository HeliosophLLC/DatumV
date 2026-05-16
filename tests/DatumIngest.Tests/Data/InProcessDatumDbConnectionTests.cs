using DatumIngest.Catalog;
using DatumIngest.Data;
using DatumIngest.Model;
using DatumIngest.Tests.Data;

namespace DatumIngest.Tests.Data;

/// <summary>
/// End-to-end tests for the ADO.NET-style in-process surface
/// (<see cref="InProcessDatumDbConnection"/> / <see cref="InProcessDatumDbCommand"/>
/// / <see cref="InProcessDatumDbReader"/>). Covers the three execute verbs,
/// parameter binding, schema introspection, and disposal contracts.
/// </summary>
public sealed class InProcessDatumDbConnectionTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public InProcessDatumDbConnectionTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-inprocess-tests-{Guid.NewGuid():N}");
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
    public async Task ExecuteNonQueryAsync_CreateTable_AppliesSideEffect()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "CREATE TABLE public.t (id INT32 NOT NULL)");

        int affected = await command.ExecuteNonQueryAsync();

        Assert.Equal(-1, affected);
        Assert.True(catalog.HasTable("public.t"));
    }

    [Fact]
    public async Task ExecuteReaderAsync_Select_YieldsRows()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Seed(catalog,
            "CREATE TABLE public.t (id INT32 NOT NULL, name STRING)",
            "INSERT INTO public.t VALUES (1, 'a'), (2, 'b'), (3, 'c')");

        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SELECT id, name FROM public.t ORDER BY id");

        await using InProcessDatumDbReader reader = await command.ExecuteReaderAsync();

        Assert.True(reader.HasRows);
        Assert.Equal(2, reader.FieldCount);
        Assert.Equal("id", reader.GetName(0));
        Assert.Equal("name", reader.GetName(1));

        List<(int, string)> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        Assert.Equal([(1, "a"), (2, "b"), (3, "c")], rows);
    }

    [Fact]
    public async Task ExecuteReaderAsync_EmptyResultSet_HasRowsFalse()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Seed(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");

        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SELECT id FROM public.t");

        await using InProcessDatumDbReader reader = await command.ExecuteReaderAsync();

        Assert.False(reader.HasRows);
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task ExecuteScalarAsync_ReturnsFirstCell()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Seed(catalog,
            "CREATE TABLE public.t (id INT32 NOT NULL)",
            "INSERT INTO public.t VALUES (42), (43)");

        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SELECT id FROM public.t ORDER BY id");

        DataValue? value = await command.ExecuteScalarAsync();

        Assert.NotNull(value);
        Assert.Equal(42, value!.Value.AsInt32());
    }

    [Fact]
    public async Task ExecuteScalarAsync_EmptyResult_ReturnsNull()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Seed(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");

        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SELECT id FROM public.t");

        DataValue? value = await command.ExecuteScalarAsync();

        Assert.Null(value);
    }

    [Fact]
    public async Task Parameters_BindToPlaceholders()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Seed(catalog,
            "CREATE TABLE public.t (id INT32 NOT NULL, name STRING)",
            "INSERT INTO public.t VALUES (1, 'apple'), (2, 'banana'), (3, 'cherry')");

        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SELECT name FROM public.t WHERE id = $id");
        command.Parameters.AddInt32("id", 2);

        DataValue? scalar = await command.ExecuteScalarAsync();

        Assert.NotNull(scalar);
        Assert.Equal("banana", scalar!.Value.AsString());
    }

    [Fact]
    public async Task ExecuteReader_SyncExtensions_StreamsRowsAndDisposesCleanly()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Seed(catalog,
            "CREATE TABLE public.t (id INT32 NOT NULL)",
            "INSERT INTO public.t VALUES (10), (20), (30)");

        // ExecuteReader / Read are sync wrappers exposed only in the test
        // assembly via InProcessDatumDbSyncExtensions; production code
        // uses the async forms. Reader is IAsyncDisposable only, so the
        // sync `using` doesn't apply — async dispose stays explicit.
        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SELECT id FROM public.t ORDER BY id");

        List<int> rows = [];
        await using (InProcessDatumDbReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(reader.GetInt32(0));
            }
        }

        Assert.Equal([10, 20, 30], rows);
    }

    [Fact]
    public async Task ExecuteReaderAsync_AccessorBeforeRead_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Seed(catalog,
            "CREATE TABLE public.t (id INT32 NOT NULL)",
            "INSERT INTO public.t VALUES (1)");

        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SELECT id FROM public.t");

        await using InProcessDatumDbReader reader = await command.ExecuteReaderAsync();

        // Before ReadAsync, no current row — accessor must throw, even
        // though HasRows is true and FieldCount answers schema queries.
        Assert.True(reader.HasRows);
        Assert.Equal(1, reader.FieldCount);
        Assert.Throws<InvalidOperationException>(() => reader.GetInt32(0));
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_Insert_AppliesAndYieldsNoRows()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Seed(catalog, "CREATE TABLE public.t (id INT32 NOT NULL)");

        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "INSERT INTO public.t VALUES (1), (2), (3)");

        await command.ExecuteNonQueryAsync();

        // Read back to confirm the side effect actually ran.
        using InProcessDatumDbCommand readback = connection.CreateCommand(
            "SELECT id FROM public.t ORDER BY id");
        await using InProcessDatumDbReader reader = await readback.ExecuteReaderAsync();
        List<int> ids = [];
        while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
        Assert.Equal([1, 2, 3], ids);
    }

    [Fact]
    public async Task NextResult_AlwaysFalse_InV1()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Seed(catalog,
            "CREATE TABLE public.t (id INT32 NOT NULL)",
            "INSERT INTO public.t VALUES (1)");

        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SELECT id FROM public.t");

        await using InProcessDatumDbReader reader = await command.ExecuteReaderAsync();
        Assert.False(reader.NextResult());
    }

    [Fact]
    public async Task GetOrdinal_ResolvesByName()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await Seed(catalog,
            "CREATE TABLE public.t (id INT32 NOT NULL, name STRING)",
            "INSERT INTO public.t VALUES (1, 'x')");

        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "SELECT id, name FROM public.t");
        await using InProcessDatumDbReader reader = await command.ExecuteReaderAsync();

        Assert.Equal(0, reader.GetOrdinal("id"));
        Assert.Equal(1, reader.GetOrdinal("name"));
        Assert.Throws<ArgumentException>(() => reader.GetOrdinal("ghost"));
    }

    [Fact]
    public async Task PrepareAsync_ReturnsPlanWithoutExecuting()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand(
            "CREATE TABLE public.t (id INT32 NOT NULL)");

        StatementPlan plan = await command.PrepareAsync();

        Assert.False(catalog.HasTable("public.t"),
            "PrepareAsync must not execute the plan — only the Execute* verbs apply side effects.");
        Assert.Equal("CreateTable", plan.ExplainTree.OperatorName);
    }

    [Fact]
    public async Task Command_WithoutCommandTextOrStatement_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        using InProcessDatumDbConnection connection = new(catalog);
        using InProcessDatumDbCommand command = connection.CreateCommand();

        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteNonQueryAsync());
    }

    private static async Task Seed(TableCatalog catalog, params string[] sqls)
    {
        foreach (string sql in sqls)
        {
            StatementPlan plan = await catalog.PlanAsync(sql);
            await catalog.ExecuteAsync(plan).DrainAsync();
        }
    }
}
