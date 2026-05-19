using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Fits;

namespace Heliosoph.DatumV.Tests.Serialization.Fits;

/// <summary>
/// Integration tests for <see cref="FitsDeserializer"/>: the ingest-path
/// counterpart of the <c>open_fits_hdus</c> TVF. Yields one row per HDU
/// with the same 10-column metadata shape, so <c>datum ingest foo.fits</c>
/// lands a queryable HDU manifest into a .datum file by default.
/// </summary>
public sealed class FitsDeserializerTests : ServiceTestBase
{
    [Fact]
    public async Task Deserialize_MultiHduFile_YieldsOneRowPerHduWithCorrectShape()
    {
        // Same multi-HDU layout the OpenFitsHdusFunction test uses, just
        // routed through the ingest path instead of the TVF path.
        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
                .Int("BITPIX", 8)
                .Int("NAXIS", 0)
                .Bool("EXTEND", true)
            .EndHdu()
            .BeginExtension("IMAGE")
                .Int("BITPIX", -32)
                .Int("NAXIS", 2)
                .Int("NAXIS1", 4)
                .Int("NAXIS2", 4)
                .Int("PCOUNT", 0)
                .Int("GCOUNT", 1)
                .QuotedString("EXTNAME", "SCI")
            .EndHdu()
            .AppendData(new byte[4 * 4 * 4])
            .BeginExtension("BINTABLE")
                .Int("BITPIX", 8)
                .Int("NAXIS", 2)
                .Int("NAXIS1", 16)
                .Int("NAXIS2", 50)
                .Int("PCOUNT", 0)
                .Int("GCOUNT", 1)
                .Int("TFIELDS", 2)
                .QuotedString("EXTNAME", "CATALOG")
            .EndHdu()
            .AppendData(new byte[16 * 50])
            .Build();

        MemoryFileDescriptor descriptor = new(file, "test.fits");
        FitsDeserializer deserializer = new(descriptor);

        Pool pool = CreatePool();
        SerializationContext context = new(pool);

        List<DataValue[]> rows = await CollectRows(deserializer, context);

        Assert.Equal(3, rows.Count);

        // Row 0 — primary
        Assert.Equal(0, rows[0][0].AsInt64());        // hdu_index
        Assert.Equal("primary", rows[0][1].AsString()); // kind
        Assert.True(rows[0][2].IsNull);                // extname

        // Row 1 — image SCI
        Assert.Equal(1, rows[1][0].AsInt64());
        Assert.Equal("image", rows[1][1].AsString());
        Assert.Equal("SCI", rows[1][2].AsString());

        // Row 2 — bintable CATALOG, nrows/ncols populated
        Assert.Equal(2, rows[2][0].AsInt64());
        Assert.Equal("bintable", rows[2][1].AsString());
        Assert.Equal("CATALOG", rows[2][2].AsString());
        Assert.Equal(50L, rows[2][7].AsInt64()); // nrows
        Assert.Equal(2, rows[2][8].AsInt32());   // ncols
    }

    [Fact]
    public async Task Deserialize_RegisteredInFormatRegistry_OpensViaCanHandle()
    {
        // Sanity check that the format wiring works end-to-end: registry
        // resolves a .fits file to FitsDeserializer without us naming it.
        byte[] file = new FitsTestBuilder()
            .BeginPrimary().Int("BITPIX", 8).Int("NAXIS", 0).EndHdu()
            .Build();
        MemoryFileDescriptor descriptor = new(file, "sample.fits");

        FormatRegistry registry = new([new FitsFileFormat()]);
        IFormatDeserializer deserializer = registry.CreateDeserializer(descriptor);

        Assert.IsType<FitsDeserializer>(deserializer);

        Pool pool = CreatePool();
        SerializationContext context = new(pool);
        List<DataValue[]> rows = await CollectRows(deserializer, context);
        Assert.Single(rows);
        Assert.Equal("primary", rows[0][1].AsString());
    }

    private static async Task<List<DataValue[]>> CollectRows(
        IFormatDeserializer deserializer,
        SerializationContext context)
    {
        List<DataValue[]> rows = [];
        await foreach (RowBatch batch in deserializer.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row source = batch[i];
                DataValue[] stabilized = new DataValue[source.FieldCount];
                for (int f = 0; f < source.FieldCount; f++)
                {
                    stabilized[f] = DataValueRetention.Stabilize(source[f], batch.Arena, batch.Arena);
                }
                rows.Add(stabilized);
            }
            batch.Dispose();
        }
        return rows;
    }
}
