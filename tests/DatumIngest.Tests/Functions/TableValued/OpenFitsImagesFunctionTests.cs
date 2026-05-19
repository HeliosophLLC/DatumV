using System.Buffers.Binary;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.TableValued;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Tests.Serialization.Fits;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Functions.TableValued;

/// <summary>
/// <c>open_fits_images(path)</c> table-valued function: opens a FITS file
/// and yields one row per image HDU with a Float32 pixel array (BSCALE/BZERO
/// applied) and an optional PNG preview. Covers schema declaration, the
/// dual sci/image column population rules, big-endian decoding for each
/// supported BITPIX value, BSCALE/BZERO physical scaling, and the
/// skip-non-image-HDU walk behaviour.
/// </summary>
public sealed class OpenFitsImagesFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_DeclaresImageRowSchema()
    {
        OpenFitsImagesFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.String]);

        Assert.Equal(5, schema.Columns.Count);
        Assert.Equal("hdu_index", schema.Columns[0].Name);
        Assert.Equal("extname", schema.Columns[1].Name);

        Assert.Equal("image", schema.Columns[2].Name);
        Assert.Equal(DataKind.Image, schema.Columns[2].Kind);
        Assert.True(schema.Columns[2].Nullable);

        Assert.Equal("sci", schema.Columns[3].Name);
        Assert.Equal(DataKind.Float32, schema.Columns[3].Kind);
        Assert.True(schema.Columns[3].IsArray);
        Assert.True(schema.Columns[3].Nullable);

        Assert.Equal("header", schema.Columns[4].Name);
        Assert.Equal(DataKind.Json, schema.Columns[4].Kind);
    }

    [Fact]
    public async Task Open_Float32Image_2D_ProducesSciAndPngPreview()
    {
        // A 4×4 Float32 image with a deterministic gradient so we can verify
        // exact pixel values flow through unchanged when BSCALE=1, BZERO=0.
        const int width = 4;
        const int height = 4;
        float[] expected = new float[width * height];
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < expected.Length; i++)
        {
            expected[i] = i * 0.25f;
            BinaryPrimitives.WriteSingleBigEndian(pixels.AsSpan(i * 4, 4), expected[i]);
        }

        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
                .Int("BITPIX", -32)
                .Int("NAXIS", 2)
                .Int("NAXIS1", width)
                .Int("NAXIS2", height)
            .EndHdu()
            .AppendData(pixels)
            .Build();

        await RunWithFile(file, async (fn, ctx) =>
        {
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(TempFile)], ctx), ctx);

            Assert.Single(rows);
            Assert.Equal(0, rows[0]["hdu_index"].AsInt64());
            Assert.True(rows[0]["extname"].IsNull);

            ReadOnlySpan<float> sci = rows[0]["sci"].AsArraySpan<float>(ctx.Store);
            Assert.Equal(expected.Length, sci.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], sci[i]);
            }

            // image must be populated for NAXIS==2 and carry the PNG signature.
            Assert.Equal(DataKind.Image, rows[0]["image"].Kind);
            Assert.False(rows[0]["image"].IsNull);
            ReadOnlySpan<byte> png = rows[0]["image"].AsByteSpan(ctx.Store);
            Assert.True(png.Length > 0);
            Assert.Equal(0x89, png[0]);
            Assert.Equal((byte)'P', png[1]);
            Assert.Equal((byte)'N', png[2]);
            Assert.Equal((byte)'G', png[3]);
        });
    }

    [Fact]
    public async Task Open_Int16ImageWithBScaleBZero_AppliesPhysicalScalingToSci()
    {
        // 2×2 Int16 image with BSCALE=2.5, BZERO=-1024 — a hot/cold mock.
        // Raw values: [0, 1, 100, -100]
        // Physical:   [-1024, -1021.5, -774, -1274]
        const int width = 2;
        const int height = 2;
        short[] raw = [0, 1, 100, -100];
        byte[] pixels = new byte[raw.Length * 2];
        for (int i = 0; i < raw.Length; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(pixels.AsSpan(i * 2, 2), raw[i]);
        }

        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
                .Int("BITPIX", 16)
                .Int("NAXIS", 2)
                .Int("NAXIS1", width)
                .Int("NAXIS2", height)
                .Double("BSCALE", 2.5)
                .Double("BZERO", -1024)
            .EndHdu()
            .AppendData(pixels)
            .Build();

        await RunWithFile(file, async (fn, ctx) =>
        {
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(TempFile)], ctx), ctx);

            Assert.Single(rows);
            ReadOnlySpan<float> sci = rows[0]["sci"].AsArraySpan<float>(ctx.Store);
            Assert.Equal(4, sci.Length);
            Assert.Equal(-1024f, sci[0]);
            Assert.Equal(-1021.5f, sci[1]);
            Assert.Equal(-774f, sci[2]);
            Assert.Equal(-1274f, sci[3]);

            Assert.False(rows[0]["image"].IsNull);
        });
    }

    [Fact]
    public async Task Open_OneDSpectrum_HasSciButImageIsNull()
    {
        // NAXIS == 1 — a spectrum. Float32, 8 channels.
        const int channels = 8;
        byte[] pixels = new byte[channels * 4];
        for (int i = 0; i < channels; i++)
        {
            BinaryPrimitives.WriteSingleBigEndian(pixels.AsSpan(i * 4, 4), i);
        }

        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
                .Int("BITPIX", -32)
                .Int("NAXIS", 1)
                .Int("NAXIS1", channels)
            .EndHdu()
            .AppendData(pixels)
            .Build();

        await RunWithFile(file, async (fn, ctx) =>
        {
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(TempFile)], ctx), ctx);

            Assert.Single(rows);
            Assert.True(rows[0]["image"].IsNull); // NAXIS != 2 → no preview
            Assert.False(rows[0]["sci"].IsNull);
            Assert.Equal(channels, rows[0]["sci"].AsArraySpan<float>(ctx.Store).Length);
        });
    }

    [Fact]
    public async Task Open_ThreeDCube_HasSciButImageIsNull()
    {
        const int w = 4, h = 4, d = 2;
        byte[] pixels = new byte[w * h * d * 4];
        // Values irrelevant for this shape-only test.
        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
                .Int("BITPIX", -32)
                .Int("NAXIS", 3)
                .Int("NAXIS1", w)
                .Int("NAXIS2", h)
                .Int("NAXIS3", d)
            .EndHdu()
            .AppendData(pixels)
            .Build();

        await RunWithFile(file, async (fn, ctx) =>
        {
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(TempFile)], ctx), ctx);

            Assert.Single(rows);
            Assert.True(rows[0]["image"].IsNull);
            Assert.Equal(w * h * d, rows[0]["sci"].AsArraySpan<float>(ctx.Store).Length);
        });
    }

    [Fact]
    public async Task Open_SkipsBintableAndHeaderOnlyHdus()
    {
        // File layout: primary header-only (skipped) + image extension
        // (kept) + bintable extension (skipped). Should yield exactly one row.
        const int w = 2, h = 2;
        byte[] imagePixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            BinaryPrimitives.WriteSingleBigEndian(imagePixels.AsSpan(i * 4, 4), i);
        }

        byte[] file = new FitsTestBuilder()
            // Primary: NAXIS=0, no data — skipped.
            .BeginPrimary()
                .Int("BITPIX", 8)
                .Int("NAXIS", 0)
                .Bool("EXTEND", true)
            .EndHdu()
            // Image extension — kept.
            .BeginExtension("IMAGE")
                .Int("BITPIX", -32)
                .Int("NAXIS", 2)
                .Int("NAXIS1", w)
                .Int("NAXIS2", h)
                .Int("PCOUNT", 0)
                .Int("GCOUNT", 1)
                .QuotedString("EXTNAME", "SCI")
            .EndHdu()
            .AppendData(imagePixels)
            // Bintable extension — skipped.
            .BeginExtension("BINTABLE")
                .Int("BITPIX", 8)
                .Int("NAXIS", 2)
                .Int("NAXIS1", 8)
                .Int("NAXIS2", 5)
                .Int("PCOUNT", 0)
                .Int("GCOUNT", 1)
                .Int("TFIELDS", 1)
            .EndHdu()
            .AppendData(new byte[8 * 5])
            .Build();

        await RunWithFile(file, async (fn, ctx) =>
        {
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(TempFile)], ctx), ctx);

            Assert.Single(rows);
            Assert.Equal(1, rows[0]["hdu_index"].AsInt64()); // hdu_index preserved across skip
            Assert.Equal("SCI", rows[0]["extname"].AsString());
        });
    }

    // ───────────────────────── Plumbing ─────────────────────────

    private string TempFile { get; set; } = "";

    private async Task RunWithFile(byte[] file, Func<OpenFitsImagesFunction, ExecutionContext, Task> body)
    {
        TempFile = Path.Combine(Path.GetTempPath(), $"open-fits-images-test-{Guid.NewGuid():N}.fits");
        await File.WriteAllBytesAsync(TempFile, file);
        try
        {
            OpenFitsImagesFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            await body(fn, ctx);
        }
        finally
        {
            try { File.Delete(TempFile); } catch (IOException) { }
        }
    }

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
