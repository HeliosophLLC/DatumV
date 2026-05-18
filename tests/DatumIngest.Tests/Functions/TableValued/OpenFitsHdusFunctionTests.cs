using System.Text.Json;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Json;
using DatumIngest.Functions.TableValued;
using DatumIngest.Model;
using DatumIngest.Tests.Serialization.Fits;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Functions.TableValued;

/// <summary>
/// <c>open_fits_hdus(path)</c> table-valued function: opens a FITS file and
/// yields one row per HDU with its parsed metadata. Covers schema
/// declaration, end-to-end shape on a multi-HDU file (primary + image
/// extension + binary-table extension), and the JSON-encoded header column.
/// </summary>
public sealed class OpenFitsHdusFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_DeclaresFullMetadataSchema()
    {
        OpenFitsHdusFunction fn = new();

        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.String]);

        // 10 metadata columns in fixed order.
        Assert.Equal(10, schema.Columns.Count);
        Assert.Equal("hdu_index", schema.Columns[0].Name);
        Assert.Equal(DataKind.Int64, schema.Columns[0].Kind);
        Assert.False(schema.Columns[0].Nullable);

        Assert.Equal("kind", schema.Columns[1].Name);
        Assert.Equal(DataKind.String, schema.Columns[1].Kind);

        Assert.Equal("extname", schema.Columns[2].Name);
        Assert.True(schema.Columns[2].Nullable);

        Assert.Equal("naxisn", schema.Columns[6].Name);
        Assert.True(schema.Columns[6].IsArray);

        Assert.Equal("header", schema.Columns[9].Name);
        Assert.Equal(DataKind.Json, schema.Columns[9].Kind);
    }

    [Fact]
    public void ValidateArguments_RejectsNonStringPath()
    {
        OpenFitsHdusFunction fn = new();

        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments([DataKind.Int64]));
    }

    [Fact]
    public void ValidateArguments_RejectsWrongArgCount()
    {
        OpenFitsHdusFunction fn = new();
        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments([]));
        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public async Task Open_ThreeHduFile_YieldsOneRowPerHduWithCorrectMetadata()
    {
        byte[] file = new FitsTestBuilder()
            // Primary: header-only container.
            .BeginPrimary()
                .Int("BITPIX", 8)
                .Int("NAXIS", 0)
                .Bool("EXTEND", true)
            .EndHdu()
            // Image extension: 4×4 Float32 with EXTNAME = "SCI".
            .BeginExtension("IMAGE")
                .Int("BITPIX", -32)
                .Int("NAXIS", 2)
                .Int("NAXIS1", 4)
                .Int("NAXIS2", 4)
                .Int("PCOUNT", 0)
                .Int("GCOUNT", 1)
                .QuotedString("EXTNAME", "SCI")
                .Int("EXTVER", 1)
            .EndHdu()
            .AppendData(new byte[4 * 4 * 4])
            // Binary-table extension: 16-byte rows, 100 rows, 3 columns.
            .BeginExtension("BINTABLE")
                .Int("BITPIX", 8)
                .Int("NAXIS", 2)
                .Int("NAXIS1", 16)
                .Int("NAXIS2", 100)
                .Int("PCOUNT", 0)
                .Int("GCOUNT", 1)
                .Int("TFIELDS", 3)
                .QuotedString("EXTNAME", "CATALOG")
            .EndHdu()
            .AppendData(new byte[16 * 100])
            .Build();

        string path = TempPath(".fits");
        await File.WriteAllBytesAsync(path, file);
        try
        {
            OpenFitsHdusFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

            Assert.Equal(3, rows.Count);

            // Row 0 — primary
            Assert.Equal(0, rows[0]["hdu_index"].AsInt64());
            Assert.Equal("primary", rows[0]["kind"].AsString());
            Assert.True(rows[0]["extname"].IsNull);
            Assert.Equal(8, rows[0]["bitpix"].AsInt32());
            Assert.Equal(0, rows[0]["naxis"].AsInt32());
            Assert.True(rows[0]["nrows"].IsNull);
            Assert.True(rows[0]["ncols"].IsNull);

            // Row 1 — image SCI extension
            Assert.Equal(1, rows[1]["hdu_index"].AsInt64());
            Assert.Equal("image", rows[1]["kind"].AsString());
            Assert.Equal("SCI", rows[1]["extname"].AsString());
            Assert.Equal(1, rows[1]["extver"].AsInt32());
            Assert.Equal(-32, rows[1]["bitpix"].AsInt32());
            Assert.Equal(2, rows[1]["naxis"].AsInt32());
            Assert.True(rows[1]["nrows"].IsNull);
            Assert.True(rows[1]["ncols"].IsNull);

            // Row 2 — bintable CATALOG extension
            Assert.Equal(2, rows[2]["hdu_index"].AsInt64());
            Assert.Equal("bintable", rows[2]["kind"].AsString());
            Assert.Equal("CATALOG", rows[2]["extname"].AsString());
            Assert.Equal(100L, rows[2]["nrows"].AsInt64());
            Assert.Equal(3, rows[2]["ncols"].AsInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Open_NaxisnColumn_CarriesPerDimensionSizes()
    {
        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
                .Int("BITPIX", 16)
                .Int("NAXIS", 3)
                .Int("NAXIS1", 32)
                .Int("NAXIS2", 16)
                .Int("NAXIS3", 4)
            .EndHdu()
            .AppendData(new byte[32 * 16 * 4 * 2])
            .Build();

        string path = TempPath(".fits");
        await File.WriteAllBytesAsync(path, file);
        try
        {
            OpenFitsHdusFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

            Assert.Single(rows);
            ReadOnlySpan<int> dims = rows[0]["naxisn"].AsArraySpan<int>(ctx.Store);
            Assert.Equal(3, dims.Length);
            Assert.Equal(32, dims[0]);
            Assert.Equal(16, dims[1]);
            Assert.Equal(4, dims[2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Open_HeaderColumn_IsJsonArrayOfKeyValueComment()
    {
        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
                .Int("BITPIX", 8)
                .Int("NAXIS", 0)
                .QuotedString("OBJECT", "Andromeda")
            .EndHdu()
            .Build();

        string path = TempPath(".fits");
        await File.WriteAllBytesAsync(path, file);
        try
        {
            OpenFitsHdusFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

            // The header column carries canonical CBOR — the storage contract
            // for DataKind.Json. Decode it back to JSON text to verify the
            // logical shape downstream consumers (renderer, json_* functions)
            // will see.
            ReadOnlySpan<byte> headerCbor = rows[0]["header"].AsByteSpan(ctx.Store);
            string headerJson = CborJsonCodec.DecodeToJsonText(headerCbor);
            using JsonDocument doc = JsonDocument.Parse(headerJson);
            JsonElement root = doc.RootElement;
            Assert.Equal(JsonValueKind.Array, root.ValueKind);

            // Find OBJECT card. Order = file order (SIMPLE, BITPIX, NAXIS, OBJECT).
            bool foundObject = false;
            foreach (JsonElement card in root.EnumerateArray())
            {
                if (card.GetProperty("key").GetString() == "OBJECT")
                {
                    Assert.Equal("Andromeda", card.GetProperty("value").GetString());
                    foundObject = true;
                }
            }
            Assert.True(foundObject, "OBJECT card missing from JSON header");
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
        Path.Combine(Path.GetTempPath(), $"open-fits-hdus-test-{Guid.NewGuid():N}{ext}");
}
