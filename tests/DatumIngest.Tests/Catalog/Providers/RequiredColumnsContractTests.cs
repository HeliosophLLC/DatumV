using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.V2;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Catalog.Providers;

/// <summary>
/// Verifies the projection-pushdown contract: <c>requiredColumns</c> shipped to a
/// table provider must be a subset of that provider's schema. The planner upholds
/// the contract by filtering planner-introduced names (LET bindings, output aliases,
/// destructure names) out of <see cref="QueryPlanner.CollectAllReferencedColumns"/>;
/// the provider asserts the contract in <see cref="DatumFileTableProviderV2.ResolveProjection"/>.
/// </summary>
public sealed class RequiredColumnsContractTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"v2_contract_{Guid.NewGuid():N}");

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

    // ──────────────── Provider-level contract ────────────────

    /// <summary>
    /// Proper subset of schema columns — the provider's projection accepts every
    /// requested name and the scan succeeds.
    /// </summary>
    [Fact]
    public async Task RequiredColumns_ProperSubsetOfSchema_Succeeds()
    {
        string path = WriteSimpleFile("subset.datum");

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));

        HashSet<string> required = new(StringComparer.OrdinalIgnoreCase) { "id" };

        int rowsRead = await CountRowsAsync(provider, required);
        Assert.Equal(4, rowsRead);
    }

    /// <summary>
    /// Empty <c>requiredColumns</c>=null requests every column. Equivalent to SELECT *.
    /// </summary>
    [Fact]
    public async Task RequiredColumns_Null_ReadsAllColumns()
    {
        string path = WriteSimpleFile("nullreq.datum");

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));

        int rowsRead = await CountRowsAsync(provider, requiredColumns: null);
        Assert.Equal(4, rowsRead);
    }

    /// <summary>
    /// A name in <c>requiredColumns</c> that doesn't exist in the schema is a
    /// contract violation. The provider must surface it explicitly with a
    /// diagnostic that points at the planner, not silently drop the column or
    /// crash deep inside <c>ColumnLookup</c> with a null dictionary key.
    /// </summary>
    [Fact]
    public async Task RequiredColumns_NameNotInSchema_ThrowsContractError()
    {
        string path = WriteSimpleFile("contract.datum");

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));

        HashSet<string> required = new(StringComparer.OrdinalIgnoreCase) { "id", "phantom" };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CountRowsAsync(provider, required));

        Assert.Contains("phantom", exception.Message);
        Assert.Contains("planner bug", exception.Message);
    }

    // ──────────────── Planner-level contract (end-to-end) ────────────────

    /// <summary>
    /// LET binding names look like <see cref="ColumnReference"/> nodes in
    /// expressions. <see cref="QueryPlanner.CollectAllReferencedColumns"/> must
    /// strip them before shipping <c>requiredColumns</c> down to the scan, or
    /// the V2 provider's contract assertion fires. This is the regression test
    /// for the LET-binding-leakage class.
    /// </summary>
    [Fact]
    public async Task PlannerEnd2End_LetBindingNames_AreNotShippedToScan()
    {
        string path = WriteSimpleFile("let_e2e.datum");

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        catalog.Add(new TableDescriptor("t", path));

        QueryExpression query = SqlParser.Parse(
            "SELECT LET capital = upper(name), LET tagged = concat('hi ', capital), tagged FROM t");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = planner.Plan(query);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> rows = await plan.CollectRowsAsync(context);

        Assert.Equal(4, rows.Count);
    }

    /// <summary>
    /// Chained LET — each binding references an earlier one. Without the LET-name
    /// filter in the planner, the chain's intermediate names (<c>capital</c>,
    /// <c>tagged</c>) leak into <c>requiredColumns</c> alongside the real source
    /// column <c>name</c>, tripping the V2 provider's contract assertion.
    /// </summary>
    [Fact]
    public async Task PlannerEnd2End_ChainedLetBindings_DoNotLeakToScan()
    {
        string path = WriteSimpleFile("let_chained.datum");

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        catalog.Add(new TableDescriptor("t", path));

        QueryExpression query = SqlParser.Parse(
            "SELECT LET capital = upper(name), " +
                  "LET tagged = concat('hi ', capital), " +
                  "LET shouted = concat(tagged, '!') " +
                  ", shouted FROM t");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = planner.Plan(query);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> rows = await plan.CollectRowsAsync(context);

        Assert.Equal(4, rows.Count);
    }

    // ──────────────── Helpers ────────────────

    /// <summary>
    /// Writes a two-column ("id" Int64, "name" String) v2 .datum file with
    /// four rows. Smallest fixture that exercises the contract: enough columns
    /// to project subsets, enough rows to confirm the scan ran end-to-end.
    /// </summary>
    private string WriteSimpleFile(string fileName)
    {
        string path = Path.Combine(_tempDir, fileName);

        ColumnDescriptorV2 idColumn = new("id", DataKind.Int64, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 nameColumn = new("name", DataKind.String, EncoderKind.VariableSlot, IsNullable: false);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["id", "name"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 4, arena: arena);
        string[] names = ["alpha", "beta", "gamma", "delta"];
        for (int i = 0; i < 4; i++)
        {
            DataValue[] row = pool.RentDataValues(2);
            row[0] = DataValue.FromInt64(i);
            row[1] = DataValue.FromString(names[i], arena);
            batch.Add(row);
        }

        using DatumFileWriterV2 writer = new(path, sidecarPath: null);
        writer.Initialize([idColumn, nameColumn]);
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();

        return path;
    }

    private static async Task<int> CountRowsAsync(
        ITableProvider provider, IReadOnlySet<string>? requiredColumns)
    {
        int total = 0;
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: requiredColumns,
            filterHint: null,
            targetArena: null,
            cancellationToken: default))
        {
            total += batch.Count;
        }
        return total;
    }
}
