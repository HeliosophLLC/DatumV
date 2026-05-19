using System.Buffers.Binary;
using System.IO.Compression;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.TableValued;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Functions.TableValued;

/// <summary>
/// <c>open_idx_images(path)</c> table-valued function: opens an MNIST-style
/// IDX image file (uint8, rank ≥ 2) and yields one row per item with an
/// <c>index</c> column and a PNG-encoded <c>image</c> column. Covers schema
/// declaration, end-to-end round-trip on a synthetic IDX file (uncompressed
/// and gzipped), and the shape-validation errors for non-image inputs.
/// </summary>
public sealed class OpenIdxImagesFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_DeclaresIndexAndImageSchema()
    {
        OpenIdxImagesFunction fn = new();

        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.String]);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("idx", schema.Columns[0].Name);
        Assert.Equal(DataKind.Int64, schema.Columns[0].Kind);
        Assert.False(schema.Columns[0].Nullable);

        Assert.Equal("image", schema.Columns[1].Name);
        Assert.Equal(DataKind.Image, schema.Columns[1].Kind);
        Assert.False(schema.Columns[1].Nullable);
    }

    [Fact]
    public void ValidateArguments_RejectsNonStringPath()
    {
        OpenIdxImagesFunction fn = new();

        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments([DataKind.Int64]));
    }

    [Fact]
    public void ValidateArguments_RejectsWrongArgCount()
    {
        OpenIdxImagesFunction fn = new();

        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments([]));
        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public async Task Open_OnUbyteRank3_YieldsOneRowPerItemWithImagePopulated()
    {
        // Three 2×2 grayscale "images" with deterministic gradients so we can
        // verify the index column counts up and the image cell carries non-empty
        // PNG bytes. Pixel-equality round-trip is exercised in IdxValueReader
        // tests upstream; this test owns the TVF-shape contract.
        byte[] idx = BuildIdxImagesFile(itemCount: 3, height: 2, width: 2, gradientStart: 10);
        string path = TempPath(".idx");
        await File.WriteAllBytesAsync(path, idx);
        try
        {
            OpenIdxImagesFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

            Assert.Equal(3, rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                Assert.Equal(i, rows[i]["idx"].AsInt64());
                Assert.Equal(DataKind.Image, rows[i]["image"].Kind);
                Assert.False(rows[i]["image"].IsNull);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Open_OnGzippedUbyteRank3_TransparentlyDecompressesAndYieldsRows()
    {
        byte[] idx = BuildIdxImagesFile(itemCount: 2, height: 2, width: 2, gradientStart: 0);
        string gzPath = TempPath(".gz");
        await using (FileStream fs = new(gzPath, FileMode.Create, FileAccess.Write))
        await using (GZipStream gz = new(fs, CompressionLevel.Optimal))
        {
            await gz.WriteAsync(idx);
        }

        try
        {
            OpenIdxImagesFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(gzPath)], ctx), ctx);

            Assert.Equal(2, rows.Count);
            Assert.Equal(0L, rows[0]["idx"].AsInt64());
            Assert.Equal(1L, rows[1]["idx"].AsInt64());
        }
        finally
        {
            File.Delete(gzPath);
        }
    }

    [Fact]
    public async Task Open_OnLabelFile_ThrowsHelpfulError()
    {
        byte[] idx = BuildIdxLabelsFile(itemCount: 4);
        string path = TempPath(".idx");
        await File.WriteAllBytesAsync(path, idx);
        try
        {
            OpenIdxImagesFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();

            InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await foreach (RowBatch _ in ((ITableValuedFunction)fn)
                    .ExecuteAsync([ValueRef.FromString(path)], ctx))
                { }
            });

            Assert.Contains("open_idx_images", ex.Message);
            Assert.Contains("open_idx_labels", ex.Message);
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
        Path.Combine(Path.GetTempPath(), $"open-idx-images-test-{Guid.NewGuid():N}{ext}");

    internal static byte[] BuildIdxImagesFile(int itemCount, int height, int width, int gradientStart)
    {
        // IDX magic: 0x00 0x00 <type=0x08 ubyte> <dim=3>
        // Dimensions: [itemCount, height, width] big-endian int32
        // Data: itemCount * height * width bytes
        int dataBytes = itemCount * height * width;
        byte[] file = new byte[4 + 3 * sizeof(int) + dataBytes];
        file[0] = 0x00; file[1] = 0x00; file[2] = 0x08; file[3] = 0x03;
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(4, 4), itemCount);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(8, 4), height);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(12, 4), width);

        int pixelsPerItem = height * width;
        for (int i = 0; i < itemCount; i++)
        {
            for (int p = 0; p < pixelsPerItem; p++)
            {
                file[16 + i * pixelsPerItem + p] = (byte)((gradientStart + i * 7 + p * 3) & 0xFF);
            }
        }
        return file;
    }

    internal static byte[] BuildIdxLabelsFile(int itemCount)
    {
        // IDX magic: 0x00 0x00 <type=0x08 ubyte> <dim=1>
        // Dimensions: [itemCount] big-endian int32
        // Data: itemCount bytes (one label per item)
        byte[] file = new byte[4 + sizeof(int) + itemCount];
        file[0] = 0x00; file[1] = 0x00; file[2] = 0x08; file[3] = 0x01;
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(4, 4), itemCount);
        for (int i = 0; i < itemCount; i++)
        {
            file[8 + i] = (byte)(i % 10);
        }
        return file;
    }
}
