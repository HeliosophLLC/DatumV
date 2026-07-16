using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Wildcard expansion must not surface hoisted window columns. The planner
/// hoists every window call into a <see cref="Heliosoph.DatumV.Execution.Operators.WindowOperator"/>
/// column that sits below the final projection, so a <c>SELECT *</c> in the
/// same statement sees it in the input batch. Those hoists follow the
/// <c>__</c> hidden-name convention and must stay out of the output — with
/// them leaking, <c>CREATE TABLE … AS SELECT row_number() OVER (), *</c>
/// fails the append column-count check because the target schema (resolved
/// from the AST projection) is one column narrower than the runtime batch.
/// </summary>
public sealed class WindowHoistWildcardTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_window_star_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SelectStar_AfterWindowFunctionColumn_DoesNotLeakHoistedColumn()
    {
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["id", "n"],
            [1, 10.0],
            [2, 20.0]);

        (List<string> names, List<float> rowNumbers) = await RunWithColumnNamesAsync(catalog,
            "SELECT row_number() OVER (ORDER BY id) AS rn, * FROM nums",
            (row, _) => row["rn"].AsFloat32());

        Assert.Equal(["rn", "id", "n"], names);
        Assert.Equal([1f, 2f], rowNumbers);
    }

    [Fact]
    public async Task SelectStar_WithQualifyWindowFunction_DoesNotLeakHoistedColumn()
    {
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["id", "n"],
            [1, 10.0],
            [2, 20.0]);

        (List<string> names, List<int> ids) = await RunWithColumnNamesAsync(catalog,
            "SELECT * FROM nums QUALIFY row_number() OVER (ORDER BY id) <= 1",
            (row, _) => row["id"].AsInt32());

        Assert.Equal(["id", "n"], names);
        Assert.Equal([1], ids);
    }

    [Fact]
    public async Task SelectStar_WindowSpecWithQualifiedColumn_DoesNotLeakHoistedColumn()
    {
        // The hoisted column's name embeds the formatted call text; a
        // qualified reference inside the spec puts a dot in that name, which
        // the hidden-column check must still recognise as planner-synthetic.
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["id", "n"],
            [1, 10.0],
            [2, 20.0]);

        (List<string> names, List<float> rowNumbers) = await RunWithColumnNamesAsync(catalog,
            "SELECT row_number() OVER (ORDER BY nums.id) AS rn, * FROM nums",
            (row, _) => row["rn"].AsFloat32());

        Assert.Equal(["rn", "id", "n"], names);
        Assert.Equal([1f, 2f], rowNumbers);
    }

    [Fact]
    public async Task UnaliasedWindowFunction_KeepsFormattedOutputName()
    {
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["id", "n"],
            [1, 10.0]);

        (List<string> names, _) = await RunWithColumnNamesAsync(catalog,
            "SELECT row_number() OVER () FROM nums",
            (row, _) => 0);

        Assert.Equal(["row_number() OVER()"], names);
    }

    [Fact]
    public async Task Ctas_WindowFunctionPlusStar_FromCsvTvf_CreatesAndPopulatesAllColumns()
    {
        string csvPath = Path.Combine(_tempDir, "input.csv").Replace('\\', '/');
        File.WriteAllText(csvPath, "name,age,city\nalice,30,nyc\nbob,25,sf\n");

        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan(
            $"CREATE TABLE prospects AS SELECT row_number() OVER () AS id, * FROM open_csv_typed('{csvPath}')");

        Schema schema = catalog["prospects"].GetSchema();
        Assert.Equal(["id", "name", "age", "city"], schema.Columns.Select(c => c.Name));

        List<long> ids = [];
        StatementPlan plan = catalog.Plan("SELECT id::Int64 AS id FROM prospects ORDER BY id");
        await foreach (RowBatch batch in ExecutePlanAsync(catalog, plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                ids.Add(batch[i]["id"].AsInt64());
            }
        }
        Assert.Equal([1L, 2L], ids);
    }

    private async Task<(List<string> ColumnNames, List<T> Values)> RunWithColumnNamesAsync<T>(
        TableCatalog catalog, string sql, Func<Row, Arena, T> project)
    {
        StatementPlan plan = catalog.Plan(sql);
        List<string> names = [];
        List<T> values = [];
        await foreach (RowBatch batch in ExecutePlanAsync(catalog, plan))
        {
            if (names.Count == 0)
            {
                names.AddRange(batch.ColumnLookup.ColumnNames);
            }
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(project(batch[i], batch.Arena));
            }
        }
        return (names, values);
    }
}
