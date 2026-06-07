using System.Text.Json;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Json;
using Heliosoph.DatumV.Functions.TableValued;
using Heliosoph.DatumV.Model;
using PureHDF;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Functions.TableValued;

/// <summary>
/// <c>open_h5_meta(path)</c> table-valued function: yields one row per
/// group / dataset in an HDF5 file's tree. Covers schema declaration,
/// the end-to-end walk on a nested-group fixture, dataset-row metadata
/// (kind / element_kind / dimensions / is_scalar), and the CBOR-encoded
/// attributes column.
/// </summary>
public sealed class OpenH5MetaFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_DeclaresMetaSchema()
    {
        OpenH5MetaFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.String]);

        Assert.Equal(8, schema.Columns.Count);
        Assert.Equal("path", schema.Columns[0].Name);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.Equal("dimensions", schema.Columns[3].Name);
        Assert.True(schema.Columns[3].IsArray);
        Assert.Equal("attributes", schema.Columns[7].Name);
        Assert.Equal(DataKind.Json, schema.Columns[7].Kind);
    }

    [Fact]
    public void ValidateArguments_RejectsNonStringPath()
    {
        OpenH5MetaFunction fn = new();
        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments([DataKind.Int64]));
    }

    [Fact]
    public async Task Open_NestedFile_YieldsOneRowPerGroupAndDatasetInTreeOrder()
    {
        string path = TempH5();
        try
        {
            WriteFixture(path);

            OpenH5MetaFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

            // Root (/) + spectra group + spectra/flux dataset + matrix dataset + values dataset
            HashSet<string> paths = [.. rows.Select(r => r["path"].AsString())];
            Assert.Contains("/", paths);
            Assert.Contains("/spectra", paths);
            Assert.Contains("/spectra/flux", paths);
            Assert.Contains("/matrix", paths);
            Assert.Contains("/values", paths);

            Row matrixRow = rows.Single(r => r["path"].AsString() == "/matrix");
            Assert.Equal("dataset", matrixRow["kind"].AsString());
            Assert.Equal("Float32", matrixRow["element_kind"].AsString());
            Assert.False(matrixRow["is_scalar"].AsBoolean());
            Assert.True(matrixRow["is_supported"].AsBoolean());
            ReadOnlySpan<long> dims = matrixRow["dimensions"].AsArraySpan<long>(ctx.Store);
            Assert.Equal(new long[] { 2, 3 }, dims.ToArray());

            Row rootRow = rows.Single(r => r["path"].AsString() == "/");
            Assert.Equal("group", rootRow["kind"].AsString());
            Assert.True(rootRow["element_kind"].IsNull);
            Assert.True(rootRow["dimensions"].IsNull);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Open_AttributesColumn_DecodesAsJsonArrayOfNamedKindValueObjects()
    {
        string path = TempH5();
        try
        {
            WriteFixtureWithAttributes(path);

            OpenH5MetaFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

            Row rootRow = rows.Single(r => r["path"].AsString() == "/");
            Assert.Equal(3, rootRow["attribute_count"].AsInt32());

            ReadOnlySpan<byte> cbor = rootRow["attributes"].AsByteSpan(ctx.Store);
            string json = CborJsonCodec.DecodeToJsonText(cbor);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            Assert.Equal(JsonValueKind.Array, root.ValueKind);

            JsonElement description = FindByName(root, "description");
            Assert.Equal("String", description.GetProperty("kind").GetString());
            Assert.Equal("carina-cluster", description.GetProperty("value").GetString());

            JsonElement exposure = FindByName(root, "exposure_seconds");
            Assert.Equal("Float64", exposure.GetProperty("kind").GetString());
            Assert.Equal(1234.5, exposure.GetProperty("value").GetDouble());

            JsonElement filterCount = FindByName(root, "filter_count");
            Assert.Equal("Int32", filterCount.GetProperty("kind").GetString());
            Assert.Equal(7, filterCount.GetProperty("value").GetInt32());
        }
        finally { File.Delete(path); }
    }

    // ───────────────────────── Fixtures ─────────────────────────

    private static void WriteFixture(string path)
    {
        H5File file = new()
        {
            ["values"] = new int[] { 1, 2, 3 },
            ["matrix"] = new float[,] { { 1, 2, 3 }, { 4, 5, 6 } },
            ["spectra"] = new H5Group
            {
                ["flux"] = new double[] { 1.5, 2.5 },
            },
        };
        file.Write(path);
    }

    private static void WriteFixtureWithAttributes(string path)
    {
        H5File file = new()
        {
            Attributes =
            {
                ["description"] = "carina-cluster",
                ["exposure_seconds"] = 1234.5,
                ["filter_count"] = 7,
            },
            ["data"] = new int[] { 0 },
        };
        file.Write(path);
    }

    private static JsonElement FindByName(JsonElement array, string name)
    {
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.GetProperty("name").GetString() == name) return element;
        }
        throw new InvalidOperationException($"Attribute '{name}' not in JSON array.");
    }

    private static string TempH5() =>
        Path.Combine(Path.GetTempPath(), $"open-h5-meta-test-{Guid.NewGuid():N}.h5");

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
