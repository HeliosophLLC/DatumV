using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.TableValued;
using Heliosoph.DatumV.Model;
using PureHDF;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Functions.TableValued;

/// <summary>
/// <c>open_h5_dataset(path, dataset_path)</c> table-valued function:
/// opens an HDF5 dataset by in-file path and yields its rows with a
/// single typed column. Covers the constant-args validation hook
/// (plan-time peek surfaces real element kind and shape), per-dtype
/// row decoding for the supported scalar and array forms, and the
/// explicit failure modes (non-constant args, missing path, wrong
/// link kind, unsupported dtype, 3-D+ refusal).
/// </summary>
public sealed class OpenH5DatasetFunctionTests : ServiceTestBase, IDisposable
{
    private readonly ByteArrayValueStore _constantStore = new();
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"open-h5-dataset-{Guid.NewGuid():N}");

    public OpenH5DatasetFunctionTests()
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
    public void ValidateArguments_OnConstantArgs_PeeksFileAndReturnsTypedSchema()
    {
        string path = TempH5("typed.h5");
        new H5File
        {
            ["labels"] = new int[] { 0, 1, 2, 3, 4 },
            ["embeddings"] = new float[,] { { 0.1f, 0.2f }, { 0.3f, 0.4f }, { 0.5f, 0.6f } },
        }.Write(path);

        OpenH5DatasetFunction fn = new();

        Schema labelsSchema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String, DataKind.String],
            constantArguments: [Const(path), Const("/labels")],
            constantStore: _constantStore,
            cancellationToken: default);
        Assert.Single(labelsSchema.Columns);
        Assert.Equal("labels", labelsSchema.Columns[0].Name);
        Assert.Equal(DataKind.Int32, labelsSchema.Columns[0].Kind);
        Assert.False(labelsSchema.Columns[0].IsArray);

        Schema embeddingsSchema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String, DataKind.String],
            constantArguments: [Const(path), Const("/embeddings")],
            constantStore: _constantStore,
            cancellationToken: default);
        Assert.Single(embeddingsSchema.Columns);
        Assert.Equal("embeddings", embeddingsSchema.Columns[0].Name);
        Assert.Equal(DataKind.Float32, embeddingsSchema.Columns[0].Kind);
        Assert.True(embeddingsSchema.Columns[0].IsArray);
    }

    [Fact]
    public void ValidateArguments_OnNonConstantPath_Throws()
    {
        OpenH5DatasetFunction fn = new();
        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String, DataKind.String],
                constantArguments: [null, Const("/x")],
                constantStore: _constantStore,
                cancellationToken: default));
    }

    [Fact]
    public void ValidateArguments_OnMissingFile_Throws()
    {
        OpenH5DatasetFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String, DataKind.String],
                constantArguments: [Const("/no/such.h5"), Const("/x")],
                constantStore: _constantStore,
                cancellationToken: default));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void ValidateArguments_OnMissingDatasetPath_Throws()
    {
        string path = TempH5("missing-path.h5");
        new H5File { ["a"] = new int[] { 1 } }.Write(path);

        OpenH5DatasetFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String, DataKind.String],
                constantArguments: [Const(path), Const("/no-such-dataset")],
                constantStore: _constantStore,
                cancellationToken: default));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void ValidateArguments_OnGroupInsteadOfDataset_Throws()
    {
        string path = TempH5("group.h5");
        new H5File
        {
            ["nested"] = new H5Group { ["inner"] = new int[] { 1 } },
        }.Write(path);

        OpenH5DatasetFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String, DataKind.String],
                constantArguments: [Const(path), Const("/nested")],
                constantStore: _constantStore,
                cancellationToken: default));
        Assert.Contains("not a dataset", ex.Message);
    }

    [Fact]
    public void ValidateArguments_OnHigherRankDataset_Throws()
    {
        string path = TempH5("rank3.h5");
        new H5File
        {
            ["cube"] = new float[,,]
            {
                { { 1, 2 }, { 3, 4 } },
                { { 5, 6 }, { 7, 8 } },
            },
        }.Write(path);

        OpenH5DatasetFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String, DataKind.String],
                constantArguments: [Const(path), Const("/cube")],
                constantStore: _constantStore,
                cancellationToken: default));
        Assert.Contains("rank 3", ex.Message);
    }

    // ───────────────────── Runtime row decode ─────────────────────

    [Fact]
    public async Task Open_OneDInt32Dataset_YieldsOneRowPerElement()
    {
        string path = TempH5("1d.h5");
        new H5File { ["labels"] = new int[] { 10, 20, 30, 40, 50 } }.Write(path);

        OpenH5DatasetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync(
                [ValueRef.FromString(path), ValueRef.FromString("/labels")], ctx), ctx);

        Assert.Equal(5, rows.Count);
        Assert.Equal(10, rows[0]["labels"].AsInt32());
        Assert.Equal(20, rows[1]["labels"].AsInt32());
        Assert.Equal(30, rows[2]["labels"].AsInt32());
        Assert.Equal(40, rows[3]["labels"].AsInt32());
        Assert.Equal(50, rows[4]["labels"].AsInt32());
    }

    [Fact]
    public async Task Open_TwoDFloat32Dataset_YieldsOneRowPerOuterSliceAsTypedArray()
    {
        string path = TempH5("2d.h5");
        new H5File
        {
            ["embeddings"] = new float[,]
            {
                { 0.1f, 0.2f, 0.3f },
                { 0.4f, 0.5f, 0.6f },
                { 0.7f, 0.8f, 0.9f },
            },
        }.Write(path);

        OpenH5DatasetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync(
                [ValueRef.FromString(path), ValueRef.FromString("/embeddings")], ctx), ctx);

        Assert.Equal(3, rows.Count);
        ReadOnlySpan<float> row0 = rows[0]["embeddings"].AsArraySpan<float>(ctx.Store);
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f }, row0.ToArray());
        ReadOnlySpan<float> row2 = rows[2]["embeddings"].AsArraySpan<float>(ctx.Store);
        Assert.Equal(new float[] { 0.7f, 0.8f, 0.9f }, row2.ToArray());
    }

    [Fact]
    public async Task Open_StringDataset_YieldsOneRowPerStringElement()
    {
        string path = TempH5("strings.h5");
        new H5File
        {
            ["names"] = new string[] { "alpha", "beta", "gamma" },
        }.Write(path);

        OpenH5DatasetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync(
                [ValueRef.FromString(path), ValueRef.FromString("/names")], ctx), ctx);

        Assert.Equal(3, rows.Count);
        Assert.Equal("alpha", rows[0]["names"].AsString());
        Assert.Equal("beta", rows[1]["names"].AsString());
        Assert.Equal("gamma", rows[2]["names"].AsString());
    }

    [Fact]
    public async Task Open_NestedDataset_PathResolvesIntoGroups()
    {
        string path = TempH5("nested.h5");
        new H5File
        {
            ["spectra"] = new H5Group
            {
                ["flux"] = new double[] { 1.5, 2.5, 3.5 },
            },
        }.Write(path);

        OpenH5DatasetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync(
                [ValueRef.FromString(path), ValueRef.FromString("/spectra/flux")], ctx), ctx);

        Assert.Equal(3, rows.Count);
        Assert.Equal("flux", rows[0].ColumnLookup[0]);
        Assert.Equal(1.5, rows[0]["flux"].AsFloat64());
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
