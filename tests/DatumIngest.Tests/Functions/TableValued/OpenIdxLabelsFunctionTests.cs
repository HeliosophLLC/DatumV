using System.IO.Compression;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.TableValued;
using DatumIngest.Manifest;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Functions.TableValued;

/// <summary>
/// <c>open_idx_labels(path)</c> table-valued function: opens an MNIST-style
/// IDX label file (uint8, rank 0) and yields one row per item with an
/// <c>index</c> column and a <c>label</c> UInt8 column. Covers schema
/// declaration, the rank-0-ubyte round-trip, and the shape-validation error
/// for non-label inputs.
/// </summary>
public sealed class OpenIdxLabelsFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_DeclaresIndexAndLabelSchema()
    {
        OpenIdxLabelsFunction fn = new();

        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.String]);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("idx", schema.Columns[0].Name);
        Assert.Equal(DataKind.Int64, schema.Columns[0].Kind);

        Assert.Equal("label", schema.Columns[1].Name);
        Assert.Equal(DataKind.UInt8, schema.Columns[1].Kind);
        Assert.False(schema.Columns[1].Nullable);
    }

    [Fact]
    public void ValidateArguments_RejectsNonStringPath()
    {
        OpenIdxLabelsFunction fn = new();

        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments([DataKind.Int64]));
    }

    [Fact]
    public async Task Open_OnUbyteRank1_YieldsOneRowPerItemWithLabelValues()
    {
        byte[] idx = OpenIdxImagesFunctionTests.BuildIdxLabelsFile(itemCount: 5);
        string path = TempPath(".idx");
        await File.WriteAllBytesAsync(path, idx);
        try
        {
            OpenIdxLabelsFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

            Assert.Equal(5, rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                Assert.Equal(i, rows[i]["idx"].AsInt64());
                Assert.Equal((byte)(i % 10), rows[i]["label"].AsUInt8());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Open_OnGzippedUbyteRank1_TransparentlyDecompressesAndYieldsRows()
    {
        byte[] idx = OpenIdxImagesFunctionTests.BuildIdxLabelsFile(itemCount: 3);
        string gzPath = TempPath(".gz");
        await using (FileStream fs = new(gzPath, FileMode.Create, FileAccess.Write))
        await using (GZipStream gz = new(fs, CompressionLevel.Optimal))
        {
            await gz.WriteAsync(idx);
        }

        try
        {
            OpenIdxLabelsFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(gzPath)], ctx), ctx);

            Assert.Equal(3, rows.Count);
            Assert.Equal((byte)0, rows[0]["label"].AsUInt8());
            Assert.Equal((byte)1, rows[1]["label"].AsUInt8());
            Assert.Equal((byte)2, rows[2]["label"].AsUInt8());
        }
        finally
        {
            File.Delete(gzPath);
        }
    }

    [Fact]
    public async Task Open_OnImageFile_ThrowsHelpfulError()
    {
        byte[] idx = OpenIdxImagesFunctionTests.BuildIdxImagesFile(itemCount: 2, height: 2, width: 2, gradientStart: 0);
        string path = TempPath(".idx");
        await File.WriteAllBytesAsync(path, idx);
        try
        {
            OpenIdxLabelsFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();

            InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await foreach (RowBatch _ in ((ITableValuedFunction)fn)
                    .ExecuteAsync([ValueRef.FromString(path)], ctx))
                { }
            });

            Assert.Contains("open_idx_labels", ex.Message);
            Assert.Contains("open_idx_images", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
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

    private static string TempPath(string ext) =>
        Path.Combine(Path.GetTempPath(), $"open-idx-labels-test-{Guid.NewGuid():N}{ext}");
}
