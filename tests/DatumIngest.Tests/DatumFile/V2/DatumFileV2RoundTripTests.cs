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
public sealed class DatumFileV2RoundTripTests : ServiceTestBase, IAsyncLifetime
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
    public void RoundTrip_Point2D()
    {
        ColumnDescriptorV2 column = new(
            Name: "pt",
            Kind: DataKind.Point2D,
            Encoder: EncoderKind.FixedWidth,
            IsNullable: true);

        DataValue[] values = [
            DataValue.FromPoint2D(0f, 0f),
            DataValue.FromPoint2D(1.5f, -2.25f),
            DataValue.Null(DataKind.Point2D),
            DataValue.FromPoint2D(float.MaxValue, float.MinValue),
        ];

        DataValue[] decoded = WriteAndRead("point2d.datum", [column], values);

        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i].IsNull, decoded[i].IsNull);
            if (!values[i].IsNull)
            {
                Assert.Equal(values[i].AsPoint2D(), decoded[i].AsPoint2D());
            }
        }
    }

    [Fact]
    public void RoundTrip_Point3D()
    {
        ColumnDescriptorV2 column = new(
            Name: "pt",
            Kind: DataKind.Point3D,
            Encoder: EncoderKind.FixedWidth,
            IsNullable: true);

        DataValue[] values = [
            DataValue.FromPoint3D(0f, 0f, 0f),
            DataValue.FromPoint3D(1.5f, -2.25f, 3.75f),
            DataValue.Null(DataKind.Point3D),
            DataValue.FromPoint3D(float.MaxValue, float.MinValue, 1f),
        ];

        DataValue[] decoded = WriteAndRead("point3d.datum", [column], values);

        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i].IsNull, decoded[i].IsNull);
            if (!values[i].IsNull)
            {
                Assert.Equal(values[i].AsPoint3D(), decoded[i].AsPoint3D());
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

        Pool pool = CreatePool();
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

        Pool pool = CreatePool();
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
        Pool pool = CreatePool();
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

        Pool pool = CreatePool();
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
        r0[0] = DataValue.FromUntypedStruct(f0, writeArena);
        batch.Add(r0);

        // Row 1: null struct.
        DataValue[] r1 = pool.RentDataValues(1);
        r1[0] = DataValue.NullUntypedStruct();
        batch.Add(r1);

        // Row 2: struct with one Float64 field.
        DataValue[] r2 = pool.RentDataValues(1);
        DataValue[] f2 = [DataValue.FromFloat64(3.14)];
        r2[0] = DataValue.FromUntypedStruct(f2, writeArena);
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

    [Fact]
    public void RoundTrip_StringArray_Sidecar()
    {
        // Array<String> column. Each row's array can have any length; the
        // encoder writes per-element strings and a slot block to the sidecar,
        // the decoder eagerly materialises into the read arena.
        ColumnDescriptorV2 column = new(
            Name: "tags",
            Kind: DataKind.String,
            Encoder: EncoderKind.VariableSlot,
            IsNullable: true,
            IsArray: true);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["tags"]);
        Arena writeArena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 4, arena: writeArena);

        // Row 0: empty array
        DataValue[] r0 = pool.RentDataValues(1);
        r0[0] = DataValue.FromStringArray([], writeArena);
        batch.Add(r0);

        // Row 1: one-element array with a long string (forces non-trivial slot offsets).
        DataValue[] r1 = pool.RentDataValues(1);
        r1[0] = DataValue.FromStringArray(["a single tag value, longer than sixteen bytes"], writeArena);
        batch.Add(r1);

        // Row 2: multi-element array
        DataValue[] r2 = pool.RentDataValues(1);
        r2[0] = DataValue.FromStringArray(["alpha", "beta", "gamma"], writeArena);
        batch.Add(r2);

        // Row 3: null array
        DataValue[] r3 = pool.RentDataValues(1);
        r3[0] = DataValue.Null(DataKind.String);
        batch.Add(r3);

        WriteAndReadArrayBatch("string_array.datum", column, batch, (decoder, readArena, registry) =>
        {
            DataValue v0 = decoder.ReadValue(0);
            Assert.False(v0.IsNull);
            // Decoded reference arrays stay sidecar-resident — proves the
            // decoder didn't eagerly copy bytes into the read arena.
            Assert.True(v0.IsInSidecar);
            Assert.True(v0.IsArray);
            Assert.Empty(v0.AsStringArray(readArena, registry));

            DataValue v1 = decoder.ReadValue(1);
            Assert.False(v1.IsNull);
            Assert.True(v1.IsInSidecar);
            Assert.Equal(["a single tag value, longer than sixteen bytes"], v1.AsStringArray(readArena, registry));

            DataValue v2 = decoder.ReadValue(2);
            Assert.False(v2.IsNull);
            Assert.True(v2.IsInSidecar);
            Assert.Equal(["alpha", "beta", "gamma"], v2.AsStringArray(readArena, registry));

            DataValue v3 = decoder.ReadValue(3);
            Assert.True(v3.IsNull);
        });
    }

    [Fact]
    public void RoundTrip_ImageArray_Sidecar()
    {
        ColumnDescriptorV2 column = new(
            Name: "thumbs",
            Kind: DataKind.Image,
            Encoder: EncoderKind.VariableSlot,
            IsNullable: false,
            IsArray: true);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["thumbs"]);
        Arena writeArena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: writeArena);

        DataValue[] r0 = pool.RentDataValues(1);
        r0[0] = DataValue.FromImageArray(
            [
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
                [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00],
            ],
            writeArena);
        batch.Add(r0);

        DataValue[] r1 = pool.RentDataValues(1);
        r1[0] = DataValue.FromImageArray([[0x47, 0x49, 0x46, 0x38]], writeArena);
        batch.Add(r1);

        WriteAndReadArrayBatch("image_array.datum", column, batch, (decoder, readArena, registry) =>
        {
            DataValue v0 = decoder.ReadValue(0);
            Assert.True(v0.IsInSidecar);
            byte[][] images0 = v0.AsImageArray(readArena, registry);
            Assert.Equal(2, images0.Length);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, images0[0]);
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00 }, images0[1]);

            DataValue v1 = decoder.ReadValue(1);
            Assert.True(v1.IsInSidecar);
            byte[][] images1 = v1.AsImageArray(readArena, registry);
            Assert.Single(images1);
            Assert.Equal(new byte[] { 0x47, 0x49, 0x46, 0x38 }, images1[0]);
        });
    }

    [Fact]
    public void RoundTrip_StructArray_Sidecar()
    {
        ColumnDescriptorV2 column = new(
            Name: "boxes",
            Kind: DataKind.Struct,
            Encoder: EncoderKind.VariableSlot,
            IsNullable: false,
            IsArray: true);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["boxes"]);
        Arena writeArena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: writeArena);

        // Row 0: array of two struct elements, each with (label: String, score: Float32) fields.
        DataValue[] r0 = pool.RentDataValues(1);
        DataValue[] s0 = [DataValue.FromString("cat", writeArena), DataValue.FromFloat32(0.95f)];
        DataValue[] s1 = [DataValue.FromString("dog with a longer label here", writeArena), DataValue.FromFloat32(0.78f)];
        r0[0] = DataValue.FromUntypedStructArray([s0, s1], writeArena);
        batch.Add(r0);

        // Row 1: empty array
        DataValue[] r1 = pool.RentDataValues(1);
        r1[0] = DataValue.FromUntypedStructArray(ReadOnlySpan<DataValue[]>.Empty, writeArena);
        batch.Add(r1);

        WriteAndReadArrayBatch("struct_array.datum", column, batch, (decoder, readArena, registry) =>
        {
            DataValue v0 = decoder.ReadValue(0);
            Assert.True(v0.IsInSidecar);
            DataValue[] structs0 = v0.AsStructArray(readArena, registry);
            Assert.Equal(2, structs0.Length);
            DataValue[] s0 = structs0[0].AsStruct(readArena);
            DataValue[] s1 = structs0[1].AsStruct(readArena);
            Assert.Equal("cat", s0[0].AsString(readArena));
            Assert.Equal(0.95f, s0[1].AsFloat32());
            Assert.Equal("dog with a longer label here", s1[0].AsString(readArena));
            Assert.Equal(0.78f, s1[1].AsFloat32());

            DataValue v1 = decoder.ReadValue(1);
            Assert.True(v1.IsInSidecar);
            Assert.Empty(v1.AsStructArray(readArena, registry));
        });
    }

    /// <summary>
    /// Multi-row write + read for a single reference-array column. Hands the
    /// caller a page decoder + read arena via <paramref name="assertions"/>; all
    /// disposables (writer file handle, sidecar write/read stores, reader) are
    /// scoped via using-blocks so the temp directory cleans up cleanly.
    /// </summary>
    private void WriteAndReadArrayBatch(
        string fileName,
        ColumnDescriptorV2 column,
        RowBatch batch,
        Action<IPageDecoderV2, Arena, DatumIngest.DatumFile.Sidecar.SidecarRegistry> assertions)
    {
        string datumPath = Path.Combine(_tempDir, fileName);
        string sidecarPath = datumPath + "-blob";

        ulong fingerprint;
        using (DatumIngest.DatumFile.Sidecar.SidecarWriteStore sidecar = new(sidecarPath))
        {
            fingerprint = sidecar.Fingerprint;
            using FileStream fs = new(datumPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using DatumFileWriterV2 writer = new(fs, sidecar);
            writer.Initialize([column]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        using DatumIngest.DatumFile.Sidecar.SidecarReadStore sidecarSource = new(sidecarPath, fingerprint);
        DatumIngest.DatumFile.Sidecar.SidecarRegistry registry = new();
        // Register returns the assigned storeId; first registration gets 0,
        // matching the sidecarStoreId we pass to OpenPageDecoder below.
        registry.Register(sidecarSource);
        Arena readArena = new();
        IPageDecoderV2 decoder = reader.OpenPageDecoder(
            columnIndex: 0,
            pageIndex: 0,
            sidecarStoreId: 0,
            sidecarSource: sidecarSource,
            eagerStore: readArena);

        assertions(decoder, readArena, registry);
    }

    // ──────────────────── Test helper ────────────────────

    /// <summary>
    /// Single-column round-trip helper. Builds a RowBatch with one column,
    /// writes via <see cref="DatumFileWriterV2"/>, reads via the page
    /// decoder, and returns the materialized DataValues.
    /// </summary>
    private DataValue[] WriteAndRead(string fileName, ColumnDescriptorV2[] columns, DataValue[] values)
    {
        Pool pool = CreatePool();
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
