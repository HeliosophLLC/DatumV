using System.Buffers.Binary;
using System.Text;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.TableValued;
using DatumIngest.Model;
using DatumIngest.Tests.Serialization.Fits;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Functions.TableValued;

/// <summary>
/// <c>open_fits_table(path, ext)</c> table-valued function: opens a FITS
/// binary-table HDU and yields its rows with one column per TFORMn.
/// Covers the constant-args validation hook (plan-time file peek
/// surfaces real typed columns), per-TFORM big-endian decoding for the
/// supported scalar / array forms, EXTNAME-based extension selection,
/// and the explicit failure modes (missing file, missing EXTNAME, wrong
/// HDU kind, non-constant arguments).
/// </summary>
public sealed class OpenFitsTableFunctionTests : ServiceTestBase
{
    private readonly ByteArrayValueStore _constantStore = new();

    private DataValue Const(string s) => DataValue.FromString(s, _constantStore);

    // ───────────────────── Plan-time schema peek ─────────────────────

    [Fact]
    public void ValidateArguments_OnConstantArgs_PeeksFileAndReturnsRealSchema()
    {
        string path = WriteSampleCatalog();
        try
        {
            OpenFitsTableFunction fn = new();
            DataValue?[] constants = [Const(path), Const("CATALOG")];
            Schema schema = ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String, DataKind.String],
                constantArguments: constants,
                constantStore: _constantStore,
                cancellationToken: default);

            // Catalog: TARGETID (K), RA (D), DEC (D), TYPE (8A), MAG (3E)
            Assert.Equal(5, schema.Columns.Count);
            Assert.Equal("TARGETID", schema.Columns[0].Name);
            Assert.Equal(DataKind.Int64, schema.Columns[0].Kind);
            Assert.False(schema.Columns[0].IsArray);

            Assert.Equal("RA", schema.Columns[1].Name);
            Assert.Equal(DataKind.Float64, schema.Columns[1].Kind);

            Assert.Equal("TYPE", schema.Columns[3].Name);
            Assert.Equal(DataKind.String, schema.Columns[3].Kind);

            Assert.Equal("MAG", schema.Columns[4].Name);
            Assert.Equal(DataKind.Float32, schema.Columns[4].Kind);
            Assert.True(schema.Columns[4].IsArray);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ValidateArguments_OnNonConstantPath_Throws()
    {
        OpenFitsTableFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String, DataKind.String],
                constantArguments: [null, Const("CATALOG")],
                    constantStore: _constantStore,
                    cancellationToken: default));
        Assert.Contains("must be a constant", ex.Message);
    }

    [Fact]
    public void ValidateArguments_OnNonConstantExt_Throws()
    {
        OpenFitsTableFunction fn = new();
        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String, DataKind.String],
                constantArguments: [Const("/x.fits"), null],
                    constantStore: _constantStore,
                    cancellationToken: default));
    }

    [Fact]
    public void ValidateArguments_OnMissingFile_Throws()
    {
        OpenFitsTableFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String, DataKind.String],
                constantArguments: [Const("/no/such.fits"), Const("CATALOG")],
                    constantStore: _constantStore,
                    cancellationToken: default));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void ValidateArguments_OnMissingExtName_Throws()
    {
        string path = WriteSampleCatalog();
        try
        {
            OpenFitsTableFunction fn = new();
            FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
                ((ITableValuedFunction)fn).ValidateArguments(
                    argumentKinds: [DataKind.String, DataKind.String],
                    constantArguments: [Const(path), Const("NOPE")],
                    constantStore: _constantStore,
                    cancellationToken: default));
            Assert.Contains("EXTNAME", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ValidateArguments_OnWrongHduKind_Throws()
    {
        // Primary HDU is not a BINTABLE.
        string path = WriteSampleCatalog();
        try
        {
            OpenFitsTableFunction fn = new();
            FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
                ((ITableValuedFunction)fn).ValidateArguments(
                    argumentKinds: [DataKind.String, DataKind.Int64],
                    constantArguments: [Const(path), DataValue.FromInt64(0)],
                    constantStore: _constantStore,
                    cancellationToken: default));
            Assert.Contains("not BINTABLE", ex.Message);
        }
        finally { File.Delete(path); }
    }

    // ───────────────────── Runtime row decode ─────────────────────

    [Fact]
    public async Task Open_DecodesEveryColumnTypeIntoCorrectDataValues()
    {
        // Two rows in the sample catalog — verify each cell decodes to the
        // expected value, exercising K/D/D/8A/3E in one shot.
        string path = WriteSampleCatalog();
        try
        {
            OpenFitsTableFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync(
                    [ValueRef.FromString(path), ValueRef.FromString("CATALOG")], ctx), ctx);

            Assert.Equal(2, rows.Count);

            // Row 0
            Assert.Equal(101L, rows[0]["TARGETID"].AsInt64());
            Assert.Equal(10.5, rows[0]["RA"].AsFloat64());
            Assert.Equal(-5.25, rows[0]["DEC"].AsFloat64());
            Assert.Equal("STAR", rows[0]["TYPE"].AsString());
            ReadOnlySpan<float> mag0 = rows[0]["MAG"].AsArraySpan<float>(ctx.Store);
            Assert.Equal(3, mag0.Length);
            Assert.Equal(20.1f, mag0[0]);
            Assert.Equal(19.4f, mag0[1]);
            Assert.Equal(18.9f, mag0[2]);

            // Row 1
            Assert.Equal(102L, rows[1]["TARGETID"].AsInt64());
            Assert.Equal(11.0, rows[1]["RA"].AsFloat64());
            Assert.Equal(-6.75, rows[1]["DEC"].AsFloat64());
            Assert.Equal("GALAXY", rows[1]["TYPE"].AsString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Open_ByHduIndex_FindsTheCorrectExtension()
    {
        // Same file, accessed by index 1 (the BINTABLE) instead of EXTNAME.
        string path = WriteSampleCatalog();
        try
        {
            OpenFitsTableFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync(
                    [ValueRef.FromString(path), ValueRef.FromInt64(1)], ctx), ctx);
            Assert.Equal(2, rows.Count);
        }
        finally { File.Delete(path); }
    }

    // ───────────────────────── Fixtures + helpers ─────────────────────────

    /// <summary>
    /// Builds a tiny FITS file with a header-only primary + one BINTABLE
    /// extension named CATALOG containing the columns TARGETID (K),
    /// RA (D), DEC (D), TYPE (8A), MAG (3E) and two test rows.
    /// </summary>
    private static string WriteSampleCatalog()
    {
        // Two rows, each with K + D + D + 8A + 3*E = 8 + 8 + 8 + 8 + 12 = 44 bytes
        const int rowBytes = 44;
        byte[] data = new byte[2 * rowBytes];

        WriteRow(data.AsSpan(0, rowBytes), targetId: 101, ra: 10.5, dec: -5.25, type: "STAR", mag: [20.1f, 19.4f, 18.9f]);
        WriteRow(data.AsSpan(rowBytes, rowBytes), targetId: 102, ra: 11.0, dec: -6.75, type: "GALAXY", mag: [21.0f, 20.5f, 19.8f]);

        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
                .Int("BITPIX", 8)
                .Int("NAXIS", 0)
                .Bool("EXTEND", true)
            .EndHdu()
            .BeginExtension("BINTABLE")
                .Int("BITPIX", 8)
                .Int("NAXIS", 2)
                .Int("NAXIS1", rowBytes)
                .Int("NAXIS2", 2)
                .Int("PCOUNT", 0)
                .Int("GCOUNT", 1)
                .Int("TFIELDS", 5)
                .QuotedString("EXTNAME", "CATALOG")
                .QuotedString("TTYPE1", "TARGETID")
                .QuotedString("TFORM1", "K")
                .QuotedString("TTYPE2", "RA")
                .QuotedString("TFORM2", "D")
                .QuotedString("TTYPE3", "DEC")
                .QuotedString("TFORM3", "D")
                .QuotedString("TTYPE4", "TYPE")
                .QuotedString("TFORM4", "8A")
                .QuotedString("TTYPE5", "MAG")
                .QuotedString("TFORM5", "3E")
            .EndHdu()
            .AppendData(data)
            .Build();

        string path = Path.Combine(Path.GetTempPath(), $"open-fits-table-test-{Guid.NewGuid():N}.fits");
        File.WriteAllBytes(path, file);
        return path;
    }

    private static void WriteRow(Span<byte> dest, long targetId, double ra, double dec, string type, float[] mag)
    {
        int offset = 0;
        BinaryPrimitives.WriteInt64BigEndian(dest.Slice(offset, 8), targetId); offset += 8;
        BinaryPrimitives.WriteDoubleBigEndian(dest.Slice(offset, 8), ra); offset += 8;
        BinaryPrimitives.WriteDoubleBigEndian(dest.Slice(offset, 8), dec); offset += 8;

        byte[] typeBytes = Encoding.ASCII.GetBytes(type.PadRight(8));
        typeBytes.AsSpan(0, 8).CopyTo(dest.Slice(offset, 8));
        offset += 8;

        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteSingleBigEndian(dest.Slice(offset, 4), mag[i]);
            offset += 4;
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
