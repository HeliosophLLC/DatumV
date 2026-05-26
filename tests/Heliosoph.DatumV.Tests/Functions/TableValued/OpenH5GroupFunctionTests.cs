using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.TableValued;
using Heliosoph.DatumV.Model;
using PureHDF;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Functions.TableValued;

/// <summary>
/// <c>open_h5_group(path, group_path)</c> table-valued function: opens an
/// HDF5 group and yields a single row with one column per direct-child
/// dataset, full dataset shape preserved per cell. Covers the LIGO
/// <c>/quality/simple</c>-style "bag of related datasets" pattern, the
/// 10x Genomics parallel-array pattern, mixed-rank groups, and the
/// silent-skip rules for sub-groups and unsupported dtypes.
/// </summary>
public sealed class OpenH5GroupFunctionTests : ServiceTestBase, IDisposable
{
    private readonly ByteArrayValueStore _constantStore = new();
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"open-h5-group-{Guid.NewGuid():N}");

    public OpenH5GroupFunctionTests()
    {
        Directory.CreateDirectory(_scratchDir);
    }

    public new void Dispose()
    {
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
        base.Dispose();
    }

    private DataValue Const(string s) => DataValue.FromString(s, _constantStore);
    private string TempH5(string name) => Path.Combine(_scratchDir, name);

    // ───────────────────── Plan-time schema peek ─────────────────────

    [Fact]
    public void ValidateArguments_OnLigoShape_DeclaresOneColumnPerChildDataset()
    {
        // Mirrors LIGO's /quality/simple group: three 1-D arrays (two String,
        // one UInt32) plus a scalar String. open_h5_group should declare four
        // columns at plan time, in declaration order.
        string path = TempH5("ligo-shape.h5");
        new H5File
        {
            ["quality"] = new H5Group
            {
                ["simple"] = new H5Group
                {
                    ["DQDescriptions"] = new string[] { "data", "cbc", "burst", "stoch", "cw", "calib", "good", "no-injection", "no-veto" },
                    ["DQShortnames"] = new string[] { "DATA", "CBC", "BURST", "STOCH", "CW", "CAL", "GOOD", "NOINJ", "NOVETO" },
                    ["DQmask"] = new uint[4096],
                    ["GWOSCmeta"] = "version-1.0",
                },
            },
        }.Write(path);

        OpenH5GroupFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String, DataKind.String],
            constantArguments: [Const(path), Const("/quality/simple")],
            constantStore: _constantStore,
            cancellationToken: default);

        Assert.Equal(4, schema.Columns.Count);

        Assert.Equal("DQDescriptions", schema.Columns[0].Name);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.True(schema.Columns[0].IsArray);

        Assert.Equal("DQShortnames", schema.Columns[1].Name);
        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
        Assert.True(schema.Columns[1].IsArray);

        Assert.Equal("DQmask", schema.Columns[2].Name);
        Assert.Equal(DataKind.UInt32, schema.Columns[2].Kind);
        Assert.True(schema.Columns[2].IsArray);

        Assert.Equal("GWOSCmeta", schema.Columns[3].Name);
        Assert.Equal(DataKind.String, schema.Columns[3].Kind);
        Assert.False(schema.Columns[3].IsArray); // scalar
    }

    [Fact]
    public void ValidateArguments_OnMultiDimChild_DeclaresMultiDimColumnWithFullShape()
    {
        // 2-D child → one column with FixedShape = [R, C], the full dataset
        // shape (not [C], like open_h5_dataset slices).
        string path = TempH5("multidim-child.h5");
        new H5File
        {
            ["bag"] = new H5Group
            {
                ["matrix"] = new float[,] { { 1, 2, 3 }, { 4, 5, 6 } },
            },
        }.Write(path);

        OpenH5GroupFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String, DataKind.String],
            constantArguments: [Const(path), Const("/bag")],
            constantStore: _constantStore,
            cancellationToken: default);

        Assert.Single(schema.Columns);
        ColumnInfo column = schema.Columns[0];
        Assert.Equal("matrix", column.Name);
        Assert.Equal(DataKind.Float32, column.Kind);
        Assert.True(column.IsArray);
        Assert.True(column.IsMultiDim);
        Assert.Equal(new int[] { 2, 3 }, column.FixedShape);
    }

    [Fact]
    public void ValidateArguments_OnPathToDataset_Throws()
    {
        // Path resolves to a dataset, not a group → error.
        string path = TempH5("dataset-not-group.h5");
        new H5File { ["leaf"] = new int[] { 1, 2, 3 } }.Write(path);

        OpenH5GroupFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String, DataKind.String],
                constantArguments: [Const(path), Const("/leaf")],
                constantStore: _constantStore,
                cancellationToken: default));
        Assert.Contains("not a group", ex.Message);
    }

    [Fact]
    public void ValidateArguments_OnMissingPath_Throws()
    {
        string path = TempH5("nope.h5");
        new H5File { ["x"] = new int[] { 0 } }.Write(path);

        OpenH5GroupFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String, DataKind.String],
                constantArguments: [Const(path), Const("/missing")],
                constantStore: _constantStore,
                cancellationToken: default));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void ValidateArguments_OnNonConstantArgs_Throws()
    {
        OpenH5GroupFunction fn = new();
        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String, DataKind.String],
                constantArguments: [null, Const("/g")],
                constantStore: _constantStore,
                cancellationToken: default));
    }

    [Fact]
    public void ValidateArguments_RootGroupAccess_WorksWithSlashOrEmpty()
    {
        string path = TempH5("root.h5");
        new H5File
        {
            ["alpha"] = new int[] { 1 },
            ["beta"] = new double[] { 2.5 },
        }.Write(path);

        OpenH5GroupFunction fn = new();

        Schema schema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String, DataKind.String],
            constantArguments: [Const(path), Const("/")],
            constantStore: _constantStore,
            cancellationToken: default);

        // Both child datasets surface; order is declaration order.
        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("alpha", schema.Columns[0].Name);
        Assert.Equal("beta", schema.Columns[1].Name);
    }

    // ───────────────────── Sub-group / unsupported child handling ─────────────────────

    [Fact]
    public void ValidateArguments_SubGroupsAreSilentlySkipped()
    {
        // Two direct-child datasets surrounded by a sub-group. The sub-group
        // contributes nothing to the column list.
        string path = TempH5("with-subgroup.h5");
        new H5File
        {
            ["outer"] = new H5Group
            {
                ["before"] = new int[] { 1 },
                ["nested"] = new H5Group
                {
                    ["deep"] = new int[] { 999 },
                },
                ["after"] = new double[] { 2.5 },
            },
        }.Write(path);

        OpenH5GroupFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String, DataKind.String],
            constantArguments: [Const(path), Const("/outer")],
            constantStore: _constantStore,
            cancellationToken: default);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("before", schema.Columns[0].Name);
        Assert.Equal("after", schema.Columns[1].Name);
    }

    // ───────────────────── Runtime row decode ─────────────────────

    [Fact]
    public async Task Open_ParallelArrayGroup_YieldsOneRowWithEachDatasetAsAColumn()
    {
        // 10x Genomics /matrix/features-style: three same-length 1-D arrays.
        string path = TempH5("parallel.h5");
        new H5File
        {
            ["features"] = new H5Group
            {
                ["id"] = new string[] { "ENSG001", "ENSG002", "ENSG003" },
                ["name"] = new string[] { "GENE_A", "GENE_B", "GENE_C" },
                ["feature_type"] = new string[] { "Gene", "Gene", "Gene" },
            },
        }.Write(path);

        OpenH5GroupFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync(
                [ValueRef.FromString(path), ValueRef.FromString("/features")], ctx), ctx);

        Assert.Single(rows);

        string[] ids = rows[0]["id"].AsStringArray(ctx.Store);
        Assert.Equal(new string[] { "ENSG001", "ENSG002", "ENSG003" }, ids);

        string[] names = rows[0]["name"].AsStringArray(ctx.Store);
        Assert.Equal(new string[] { "GENE_A", "GENE_B", "GENE_C" }, names);

        string[] types = rows[0]["feature_type"].AsStringArray(ctx.Store);
        Assert.Equal(new string[] { "Gene", "Gene", "Gene" }, types);
    }

    [Fact]
    public async Task Open_MixedRankGroup_YieldsScalarOneDAndMultiDimCells()
    {
        // Verify all three rank tiers in a single row.
        string path = TempH5("mixed.h5");
        new H5File
        {
            ["bag"] = new H5Group
            {
                ["scalar_val"] = 42,
                ["flat_array"] = new double[] { 1.0, 2.0, 3.0 },
                ["matrix"] = new float[,] { { 1f, 2f }, { 3f, 4f }, { 5f, 6f } },
            },
        }.Write(path);

        OpenH5GroupFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync(
                [ValueRef.FromString(path), ValueRef.FromString("/bag")], ctx), ctx);

        Assert.Single(rows);

        // Scalar cell.
        Assert.Equal(42, rows[0]["scalar_val"].AsInt32());
        Assert.False(rows[0]["scalar_val"].IsMultiDim);

        // Flat 1-D cell.
        ReadOnlySpan<double> flat = rows[0]["flat_array"].AsArraySpan<double>(ctx.Store);
        Assert.Equal(new double[] { 1.0, 2.0, 3.0 }, flat.ToArray());
        Assert.False(rows[0]["flat_array"].IsMultiDim);

        // Multi-dim 2-D cell with shape [3, 2].
        Assert.True(rows[0]["matrix"].IsMultiDim);
        ReadOnlySpan<int> matrixShape = rows[0]["matrix"].GetShape(ctx.Store);
        Assert.Equal(new int[] { 3, 2 }, matrixShape.ToArray());
        ReadOnlySpan<float> matrixFlat = rows[0]["matrix"].AsArraySpan<float>(ctx.Store);
        Assert.Equal(new float[] { 1f, 2f, 3f, 4f, 5f, 6f }, matrixFlat.ToArray());
    }

    [Fact]
    public async Task Open_EmptyGroup_YieldsNoRows()
    {
        // A group containing only sub-groups, no datasets, yields no rows.
        string path = TempH5("only-subgroups.h5");
        new H5File
        {
            ["wrapper"] = new H5Group
            {
                ["nested"] = new H5Group
                {
                    ["unreachable"] = new int[] { 0 },
                },
            },
        }.Write(path);

        OpenH5GroupFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync(
                [ValueRef.FromString(path), ValueRef.FromString("/wrapper")], ctx), ctx);

        Assert.Empty(rows);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static async Task<List<Row>> CollectAsync(IAsyncEnumerable<RowBatch> batches, ExecutionContext ctx)
    {
        List<Row> rows = [];
        await foreach (RowBatch batch in batches)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row source = batch[i];
                DataValue[] stabilized = new DataValue[source.FieldCount];
                for (int f = 0; f < source.FieldCount; f++)
                {
                    stabilized[f] = DataValueRetention.Stabilize(source[f], batch.Arena, ctx.Store);
                }
                rows.Add(new Row(source.ColumnLookup, stabilized));
            }
        }
        return rows;
    }
}
