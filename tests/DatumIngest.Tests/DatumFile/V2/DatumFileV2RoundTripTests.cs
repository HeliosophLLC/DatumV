using DatumIngest.DatumFile.V2;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.DatumFile.V2;

/// <summary>
/// End-to-end round-trip tests for the v2 columnar format. Each test
/// builds one or more <see cref="RowBatch"/>es via <see cref="Pool"/>,
/// writes them via <see cref="DatumFileWriterV2"/>, reads them back via
/// <see cref="DatumFileReaderV2"/> + page decoders, and asserts that
/// every value round-trips intact.
/// </summary>
public sealed class DatumFileV2RoundTripTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_v2_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public void RoundTrip_FixedWidth_Int32_NoNulls()
    {
        ColumnDescriptorV2 column = new(
            Name: "v",
            Kind: DataKind.Int32,
            Encoder: EncoderKind.FixedWidth,
            IsNullable: false);

        DataValue[] values = [
            DataValue.FromInt32(1),
            DataValue.FromInt32(2),
            DataValue.FromInt32(-42),
            DataValue.FromInt32(int.MaxValue),
            DataValue.FromInt32(int.MinValue),
        ];

        DataValue[] decoded = WriteAndRead("int32_no_nulls.datum", [column], values);

        Assert.Equal(values.Length, decoded.Length);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.False(decoded[i].IsNull);
            Assert.Equal(values[i].AsInt32(), decoded[i].AsInt32());
        }
    }

    [Fact]
    public void RoundTrip_FixedWidth_Int64_WithNulls()
    {
        ColumnDescriptorV2 column = new(
            Name: "v",
            Kind: DataKind.Int64,
            Encoder: EncoderKind.FixedWidth,
            IsNullable: true);

        DataValue[] values = [
            DataValue.FromInt64(10L),
            DataValue.Null(DataKind.Int64),
            DataValue.FromInt64(long.MinValue),
            DataValue.FromInt64(long.MaxValue),
        ];

        DataValue[] decoded = WriteAndRead("int64_nullable.datum", [column], values);

        Assert.True(decoded[0].AsInt64() == 10L);
        Assert.True(decoded[1].IsNull);
        Assert.Equal(long.MinValue, decoded[2].AsInt64());
        Assert.Equal(long.MaxValue, decoded[3].AsInt64());
    }

    [Fact]
    public void RoundTrip_Float64()
    {
        ColumnDescriptorV2 column = new(
            Name: "x",
            Kind: DataKind.Float64,
            Encoder: EncoderKind.FixedWidth,
            IsNullable: false);

        DataValue[] values = [
            DataValue.FromFloat64(0.0),
            DataValue.FromFloat64(1.0),
            DataValue.FromFloat64(-3.14),
            DataValue.FromFloat64(double.MaxValue),
            DataValue.FromFloat64(double.MinValue),
        ];

        DataValue[] decoded = WriteAndRead("f64.datum", [column], values);

        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i].AsFloat64(), decoded[i].AsFloat64());
        }
    }

    [Fact]
    public void RoundTrip_Boolean_BitPacked_Mixed()
    {
        ColumnDescriptorV2 column = new(
            Name: "b",
            Kind: DataKind.Boolean,
            Encoder: EncoderKind.BitPackedBoolean,
            IsNullable: true);

        DataValue[] values = [
            DataValue.FromBoolean(true),
            DataValue.FromBoolean(false),
            DataValue.Null(DataKind.Boolean),
            DataValue.FromBoolean(true),
            DataValue.FromBoolean(true),
            DataValue.Null(DataKind.Boolean),
            DataValue.FromBoolean(false),
            DataValue.FromBoolean(true),
            DataValue.FromBoolean(false),
        ];

        DataValue[] decoded = WriteAndRead("bool.datum", [column], values);

        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i].IsNull, decoded[i].IsNull);
            if (!values[i].IsNull)
            {
                Assert.Equal(values[i].AsBoolean(), decoded[i].AsBoolean());
            }
        }
    }

    [Fact]
    public void RoundTrip_Uuid()
    {
        ColumnDescriptorV2 column = new(
            Name: "id",
            Kind: DataKind.Uuid,
            Encoder: EncoderKind.FixedWidth,
            IsNullable: false);

        Guid[] guids = [Guid.NewGuid(), Guid.NewGuid(), Guid.Empty];
        DataValue[] values = guids.Select(DataValue.FromUuid).ToArray();

        DataValue[] decoded = WriteAndRead("uuid.datum", [column], values);

        for (int i = 0; i < guids.Length; i++)
        {
            Assert.Equal(guids[i], decoded[i].AsUuid());
        }
    }

    [Fact]
    public void RoundTrip_String_AllInline()
    {
        ColumnDescriptorV2 column = new(
            Name: "s",
            Kind: DataKind.String,
            Encoder: EncoderKind.VariableSlot,
            IsNullable: true);

        // All strings 0-15 UTF-8 bytes → all inline (no sidecar required).
        DataValue[] values = [
            DataValue.FromString(""),
            DataValue.FromString("hi"),
            DataValue.Null(DataKind.String),
            DataValue.FromString("hello"),
            DataValue.FromString("0123456789abcde"), // exactly 15 bytes
        ];

        DataValue[] decoded = WriteAndRead("string_inline.datum", [column], values);

        Assert.Equal("", decoded[0].AsString());
        Assert.Equal("hi", decoded[1].AsString());
        Assert.True(decoded[2].IsNull);
        Assert.Equal("hello", decoded[3].AsString());
        Assert.Equal("0123456789abcde", decoded[4].AsString());
    }

    [Fact]
    public void RoundTrip_MultipleColumns_HeterogeneousKinds()
    {
        ColumnDescriptorV2 idCol = new("id", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 nameCol = new("name", DataKind.String, EncoderKind.VariableSlot, IsNullable: false);
        ColumnDescriptorV2 activeCol = new("active", DataKind.Boolean, EncoderKind.BitPackedBoolean, IsNullable: false);

        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["id", "name", "active"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 3, arena: arena);

        DataValue[] r0 = pool.RentDataValues(3);
        r0[0] = DataValue.FromInt32(1); r0[1] = DataValue.FromString("alice"); r0[2] = DataValue.FromBoolean(true);
        batch.Add(r0);

        DataValue[] r1 = pool.RentDataValues(3);
        r1[0] = DataValue.FromInt32(2); r1[1] = DataValue.FromString("bob"); r1[2] = DataValue.FromBoolean(false);
        batch.Add(r1);

        DataValue[] r2 = pool.RentDataValues(3);
        r2[0] = DataValue.FromInt32(3); r2[1] = DataValue.FromString("carol"); r2[2] = DataValue.FromBoolean(true);
        batch.Add(r2);

        string datumPath = Path.Combine(_tempDir, "multi.datum");
        using (DatumFileWriterV2 writer = new(datumPath, sidecarPath: null))
        {
            writer.Initialize([idCol, nameCol, activeCol]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        Assert.Equal(3L, reader.TotalRowCount);
        Assert.Equal(3, reader.Footer.Columns.Count);

        IPageDecoderV2 idDec = reader.OpenPageDecoder(0, 0);
        IPageDecoderV2 nameDec = reader.OpenPageDecoder(1, 0);
        IPageDecoderV2 activeDec = reader.OpenPageDecoder(2, 0);

        Assert.Equal(1, idDec.ReadValue(0).AsInt32());
        Assert.Equal("alice", nameDec.ReadValue(0).AsString());
        Assert.True(activeDec.ReadValue(0).AsBoolean());

        Assert.Equal(2, idDec.ReadValue(1).AsInt32());
        Assert.Equal("bob", nameDec.ReadValue(1).AsString());
        Assert.False(activeDec.ReadValue(1).AsBoolean());

        Assert.Equal(3, idDec.ReadValue(2).AsInt32());
        Assert.Equal("carol", nameDec.ReadValue(2).AsString());
        Assert.True(activeDec.ReadValue(2).AsBoolean());
    }

    [Fact]
    public void RoundTrip_MultiPage_ExceedsPageSize()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        // Use a small page size so we hit page boundaries quickly.
        const int pageSize = 32;
        const int rowCount = 100;

        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["v"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: rowCount, arena: arena);
        for (int i = 0; i < rowCount; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromInt32(i);
            batch.Add(row);
        }

        string datumPath = Path.Combine(_tempDir, "multipage.datum");
        using (DatumFileWriterV2 writer = new(datumPath, sidecarPath: null))
        {
            writer.SetPageSize(pageSize);
            writer.Initialize([column]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        Assert.Equal(rowCount, reader.TotalRowCount);

        // 100 rows / 32 per page = 4 pages (32 + 32 + 32 + 4).
        Assert.Equal(4, reader.Footer.Columns[0].Pages.Count);
        Assert.Equal(32, reader.Footer.Columns[0].Pages[0].RowCount);
        Assert.Equal(32, reader.Footer.Columns[0].Pages[1].RowCount);
        Assert.Equal(32, reader.Footer.Columns[0].Pages[2].RowCount);
        Assert.Equal(4, reader.Footer.Columns[0].Pages[3].RowCount);

        // Read all values back.
        int rowIndex = 0;
        for (int p = 0; p < reader.Footer.Columns[0].Pages.Count; p++)
        {
            IPageDecoderV2 dec = reader.OpenPageDecoder(0, p);
            for (int i = 0; i < dec.RowCount; i++)
            {
                Assert.Equal(rowIndex, dec.ReadValue(i).AsInt32());
                rowIndex++;
            }
        }
        Assert.Equal(rowCount, rowIndex);
    }

    [Fact]
    public void RoundTrip_ZoneMaps_CapturePageMinMax()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        // 10 rows: values 1..10. Zone map should report min=1, max=10.
        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["v"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 10, arena: arena);
        for (int i = 1; i <= 10; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromInt32(i);
            batch.Add(row);
        }

        string datumPath = Path.Combine(_tempDir, "zonemap.datum");
        using (DatumFileWriterV2 writer = new(datumPath, sidecarPath: null))
        {
            writer.Initialize([column]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        var page = reader.Footer.Columns[0].Pages[0];
        Assert.NotNull(page.ZoneMap);
        Assert.True(page.ZoneMap.HasMinMax);
        Assert.Equal(1, (int)page.ZoneMap.Minimum!);
        Assert.Equal(10, (int)page.ZoneMap.Maximum!);
        Assert.Equal(0u, page.ZoneMap.NullCount);

        // One chapter aggregating one page.
        Assert.Single(reader.Footer.Columns[0].ChapterZoneMaps);
        var chapter = reader.Footer.Columns[0].ChapterZoneMaps[0];
        Assert.True(chapter.HasMinMax);
        Assert.Equal(1, (int)chapter.Minimum!);
        Assert.Equal(10, (int)chapter.Maximum!);
    }

    [Fact]
    public void RoundTrip_Struct_Sidecar()
    {
        ColumnDescriptorV2 column = new("s", DataKind.Struct, EncoderKind.VariableSlot, IsNullable: true);

        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["s"]);
        Arena writeArena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 3, arena: writeArena);

        // Row 0: struct with three fields (Int32, String, Boolean).
        DataValue[] r0 = pool.RentDataValues(1);
        DataValue[] f0 = [
            DataValue.FromInt32(42),
            DataValue.FromString("hello world is more than fifteen bytes", writeArena),
            DataValue.FromBoolean(true),
        ];
        r0[0] = DataValue.FromStruct((short)f0.Length, f0, writeArena);
        batch.Add(r0);

        // Row 1: null struct.
        DataValue[] r1 = pool.RentDataValues(1);
        r1[0] = DataValue.NullStruct(0);
        batch.Add(r1);

        // Row 2: struct with one Float64 field.
        DataValue[] r2 = pool.RentDataValues(1);
        DataValue[] f2 = [DataValue.FromFloat64(3.14)];
        r2[0] = DataValue.FromStruct((short)f2.Length, f2, writeArena);
        batch.Add(r2);

        string datumPath = Path.Combine(_tempDir, "struct.datum");
        string sidecarPath = Path.Combine(_tempDir, "struct.datum-blob");

        // Construct the SidecarWriteStore separately so the test can both
        // capture its Fingerprint (set at construction) and dispose it
        // *before* opening the read side, ensuring the file is fully
        // flushed.
        ulong fingerprint;
        DatumIngest.DatumFile.Sidecar.SidecarWriteStore sidecar = new(sidecarPath);
        try
        {
            fingerprint = sidecar.Fingerprint;
            using (FileStream fs = new(datumPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (DatumFileWriterV2 writer = new(fs, sidecar))
            {
                writer.Initialize([column]);
                writer.WriteRowBatch(batch);
                writer.FinalizeWriter();
            }
        }
        finally
        {
            sidecar.Dispose();
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        using DatumIngest.DatumFile.Sidecar.SidecarReadStore sidecarSource = new(sidecarPath, fingerprint);
        Arena readArena = new();

        var decoder = reader.OpenPageDecoder(
            columnIndex: 0,
            pageIndex: 0,
            sidecarStoreId: 0,
            sidecarSource: sidecarSource,
            eagerStore: readArena);

        DataValue v0 = decoder.ReadValue(0);
        Assert.False(v0.IsNull);
        DataValue[] g0 = v0.AsStruct(readArena);
        Assert.Equal(3, g0.Length);
        Assert.Equal(42, g0[0].AsInt32());
        Assert.Equal("hello world is more than fifteen bytes", g0[1].AsString(readArena));
        Assert.True(g0[2].AsBoolean());

        DataValue v1 = decoder.ReadValue(1);
        Assert.True(v1.IsNull);

        DataValue v2 = decoder.ReadValue(2);
        Assert.False(v2.IsNull);
        DataValue[] g2 = v2.AsStruct(readArena);
        Assert.Single(g2);
        Assert.Equal(3.14, g2[0].AsFloat64());
    }

    // ──────────────────── Test helper ────────────────────

    /// <summary>
    /// Single-column round-trip helper. Builds a RowBatch with one column,
    /// writes via <see cref="DatumFileWriterV2"/>, reads via the page
    /// decoder, and returns the materialized DataValues.
    /// </summary>
    private DataValue[] WriteAndRead(string fileName, ColumnDescriptorV2[] columns, DataValue[] values)
    {
        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new([columns[0].Name]);
        Arena arena = new();

        RowBatch batch = pool.RentRowBatch(lookup, capacity: values.Length, arena: arena);
        for (int i = 0; i < values.Length; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = values[i];
            batch.Add(row);
        }

        string datumPath = Path.Combine(_tempDir, fileName);
        using (DatumFileWriterV2 writer = new(datumPath, sidecarPath: null))
        {
            writer.Initialize(columns);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        Assert.Equal(values.Length, reader.TotalRowCount);
        Assert.Single(reader.Footer.Columns);
        Assert.Single(reader.Footer.Columns[0].Pages);

        IPageDecoderV2 decoder = reader.OpenPageDecoder(0, 0);
        DataValue[] decoded = new DataValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            decoded[i] = decoder.ReadValue(i);
        }
        return decoded;
    }
}
