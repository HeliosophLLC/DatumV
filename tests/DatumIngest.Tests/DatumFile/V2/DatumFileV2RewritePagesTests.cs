using DatumIngest.DatumFile;
using DatumIngest.DatumFile.V2;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.DatumFile.V2;

/// <summary>
/// PR11b tests for the page-COW rewrite primitive
/// (<see cref="DatumFileWriterV2.RewritePages"/>). Each test builds a
/// <c>.datum</c> file via <see cref="DatumFileWriterV2"/>, calls
/// <see cref="DatumFileWriterV2.RewritePages"/> to mutate specific rows,
/// re-opens the file via <see cref="DatumFileReaderV2"/>, and asserts the
/// rewritten values are visible (and untouched rows are intact).
/// </summary>
public sealed class DatumFileV2RewritePagesTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_rewrite_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    // ──────────────────── single-page, fixed-width ────────────────────

    [Fact]
    public void Rewrite_Int32_SingleRow_VisibleOnReopen()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = WriteFile("int32.datum", [column], BuildSingleColumnRows(
            DataValue.FromInt32(10),
            DataValue.FromInt32(20),
            DataValue.FromInt32(30)));

        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] = [new RowUpdate(RowInPage: 1, ColumnValues: new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(99) })],
        };
        DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates);

        DataValue[] decoded = ReadAllRows(path, columnIndex: 0, pageIndex: 0);
        Assert.Equal(10, decoded[0].AsInt32());
        Assert.Equal(99, decoded[1].AsInt32());
        Assert.Equal(30, decoded[2].AsInt32());
    }

    [Fact]
    public void Rewrite_Int32_MultipleRowsInSamePage()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = WriteFile("multi_row.datum", [column], BuildSingleColumnRows(
            DataValue.FromInt32(1),
            DataValue.FromInt32(2),
            DataValue.FromInt32(3),
            DataValue.FromInt32(4),
            DataValue.FromInt32(5)));

        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] =
            [
                new RowUpdate(0, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(100) }),
                new RowUpdate(2, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(300) }),
                new RowUpdate(4, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(500) }),
            ],
        };
        DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates);

        DataValue[] decoded = ReadAllRows(path, columnIndex: 0, pageIndex: 0);
        Assert.Equal(100, decoded[0].AsInt32());
        Assert.Equal(2, decoded[1].AsInt32());
        Assert.Equal(300, decoded[2].AsInt32());
        Assert.Equal(4, decoded[3].AsInt32());
        Assert.Equal(500, decoded[4].AsInt32());
    }

    [Fact]
    public void Rewrite_NoOp_WhenUpdatesDictionaryEmpty()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = WriteFile("noop.datum", [column], BuildSingleColumnRows(
            DataValue.FromInt32(1),
            DataValue.FromInt32(2)));

        long fileLengthBefore = new FileInfo(path).Length;

        DatumFileWriterV2.RewritePages(
            path,
            sidecarPath: null,
            updatesByPage: new Dictionary<int, IReadOnlyList<RowUpdate>>());

        long fileLengthAfter = new FileInfo(path).Length;
        Assert.Equal(fileLengthBefore, fileLengthAfter);

        DataValue[] decoded = ReadAllRows(path, columnIndex: 0, pageIndex: 0);
        Assert.Equal(1, decoded[0].AsInt32());
        Assert.Equal(2, decoded[1].AsInt32());
    }

    // ──────────────────── multi-column ────────────────────

    [Fact]
    public void Rewrite_DifferentColumnsInDifferentRows()
    {
        ColumnDescriptorV2 idCol = new("id", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 scoreCol = new("score", DataKind.Float64, EncoderKind.FixedWidth, IsNullable: false);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["id", "score"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 3, arena: arena);
        AddRow(pool, batch, DataValue.FromInt32(1), DataValue.FromFloat64(0.1));
        AddRow(pool, batch, DataValue.FromInt32(2), DataValue.FromFloat64(0.2));
        AddRow(pool, batch, DataValue.FromInt32(3), DataValue.FromFloat64(0.3));

        string path = Path.Combine(_tempDir, "multi_col.datum");
        using (DatumFileWriterV2 writer = new(path, sidecarPath: null))
        {
            writer.Initialize([idCol, scoreCol]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        // Update id at row 0; update score at row 2; row 1 untouched.
        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] =
            [
                new RowUpdate(0, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(100) }),
                new RowUpdate(2, new Dictionary<int, DataValue> { [1] = DataValue.FromFloat64(9.99) }),
            ],
        };
        DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        IPageDecoderV2 idDec = reader.OpenPageDecoder(0, 0);
        IPageDecoderV2 scoreDec = reader.OpenPageDecoder(1, 0);

        Assert.Equal(100, idDec.ReadValue(0).AsInt32());
        Assert.Equal(2, idDec.ReadValue(1).AsInt32());
        Assert.Equal(3, idDec.ReadValue(2).AsInt32());

        Assert.Equal(0.1, scoreDec.ReadValue(0).AsFloat64());
        Assert.Equal(0.2, scoreDec.ReadValue(1).AsFloat64());
        Assert.Equal(9.99, scoreDec.ReadValue(2).AsFloat64());
    }

    // ──────────────────── multi-page ────────────────────

    [Fact]
    public void Rewrite_RowsInDifferentPages()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        // Force two pages by setting page size to 4 and writing 7 rows.
        Pool pool = CreatePool();
        ColumnLookup lookup = new(["v"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 7, arena: arena);
        for (int i = 0; i < 7; i++) AddRow(pool, batch, DataValue.FromInt32(i + 1));

        string path = Path.Combine(_tempDir, "multi_page.datum");
        using (DatumFileWriterV2 writer = new(path, sidecarPath: null))
        {
            writer.SetPageSize(4);
            writer.Initialize([column]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        // Sanity: two pages of 4 + 3 rows.
        using (DatumFileReaderV2 reader = DatumFileReaderV2.Open(path))
        {
            Assert.Equal(2, reader.Footer.Columns[0].Pages.Count);
            Assert.Equal(4, reader.Footer.Columns[0].Pages[0].RowCount);
            Assert.Equal(3, reader.Footer.Columns[0].Pages[1].RowCount);
        }

        // Update row 1 of page 0 and row 2 of page 1.
        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] = [new RowUpdate(1, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(20) })],
            [1] = [new RowUpdate(2, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(70) })],
        };
        DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates);

        using DatumFileReaderV2 readerAfter = DatumFileReaderV2.Open(path);
        IPageDecoderV2 page0 = readerAfter.OpenPageDecoder(0, 0);
        IPageDecoderV2 page1 = readerAfter.OpenPageDecoder(0, 1);
        Assert.Equal(1, page0.ReadValue(0).AsInt32());
        Assert.Equal(20, page0.ReadValue(1).AsInt32());
        Assert.Equal(3, page0.ReadValue(2).AsInt32());
        Assert.Equal(4, page0.ReadValue(3).AsInt32());
        Assert.Equal(5, page1.ReadValue(0).AsInt32());
        Assert.Equal(6, page1.ReadValue(1).AsInt32());
        Assert.Equal(70, page1.ReadValue(2).AsInt32());
    }

    // ──────────────────── nullable column ────────────────────

    [Fact]
    public void Rewrite_Int32_NullableColumn_SetToNull()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: true);
        string path = WriteFile("nullable.datum", [column], BuildSingleColumnRows(
            DataValue.FromInt32(1),
            DataValue.FromInt32(2),
            DataValue.FromInt32(3)));

        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] = [new RowUpdate(1, new Dictionary<int, DataValue> { [0] = DataValue.Null(DataKind.Int32) })],
        };
        DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates);

        DataValue[] decoded = ReadAllRows(path, columnIndex: 0, pageIndex: 0);
        Assert.False(decoded[0].IsNull);
        Assert.True(decoded[1].IsNull);
        Assert.False(decoded[2].IsNull);
    }

    [Fact]
    public void Rewrite_Int32_NullableColumn_SetFromNullToValue()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: true);
        string path = WriteFile("denullify.datum", [column], BuildSingleColumnRows(
            DataValue.FromInt32(1),
            DataValue.Null(DataKind.Int32),
            DataValue.FromInt32(3)));

        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] = [new RowUpdate(1, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(42) })],
        };
        DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates);

        DataValue[] decoded = ReadAllRows(path, columnIndex: 0, pageIndex: 0);
        Assert.False(decoded[1].IsNull);
        Assert.Equal(42, decoded[1].AsInt32());
    }

    // ──────────────────── boolean (BitPackedBoolean) ────────────────────

    [Fact]
    public void Rewrite_Boolean_RoundTrip()
    {
        ColumnDescriptorV2 column = new("flag", DataKind.Boolean, EncoderKind.BitPackedBoolean, IsNullable: false);
        string path = WriteFile("bool.datum", [column], BuildSingleColumnRows(
            DataValue.FromBoolean(true),
            DataValue.FromBoolean(false),
            DataValue.FromBoolean(true),
            DataValue.FromBoolean(false)));

        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] =
            [
                new RowUpdate(0, new Dictionary<int, DataValue> { [0] = DataValue.FromBoolean(false) }),
                new RowUpdate(3, new Dictionary<int, DataValue> { [0] = DataValue.FromBoolean(true) }),
            ],
        };
        DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates);

        DataValue[] decoded = ReadAllRows(path, columnIndex: 0, pageIndex: 0);
        Assert.False(decoded[0].AsBoolean());
        Assert.False(decoded[1].AsBoolean());
        Assert.True(decoded[2].AsBoolean());
        Assert.True(decoded[3].AsBoolean());
    }

    // ──────────────────── descriptor + zone-map effects ────────────────────

    [Fact]
    public void Rewrite_PageDescriptor_OffsetChanges()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = WriteFile("offset.datum", [column], BuildSingleColumnRows(
            DataValue.FromInt32(1),
            DataValue.FromInt32(2)));

        long oldOffset;
        using (DatumFileReaderV2 reader = DatumFileReaderV2.Open(path))
        {
            oldOffset = reader.Footer.Columns[0].Pages[0].PageOffset;
        }

        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] = [new RowUpdate(0, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(99) })],
        };
        DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates);

        using DatumFileReaderV2 readerAfter = DatumFileReaderV2.Open(path);
        long newOffset = readerAfter.Footer.Columns[0].Pages[0].PageOffset;
        Assert.NotEqual(oldOffset, newOffset);
        Assert.True(newOffset > oldOffset, "New page must land past the old footer (later in the file).");
    }

    [Fact]
    public void Rewrite_ZoneMap_RefreshesMinMax()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = WriteFile("zonemap.datum", [column], BuildSingleColumnRows(
            DataValue.FromInt32(10),
            DataValue.FromInt32(20),
            DataValue.FromInt32(30)));

        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] = [new RowUpdate(2, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(500) })],
        };
        DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        DatumZoneMap? zoneMap = reader.Footer.Columns[0].Pages[0].ZoneMap;
        Assert.NotNull(zoneMap);
        Assert.Equal(10, (int)zoneMap.Minimum!);
        Assert.Equal(500, (int)zoneMap.Maximum!);
    }

    [Fact]
    public void Rewrite_BumpsGeneration()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = WriteFile("gen.datum", [column], BuildSingleColumnRows(DataValue.FromInt32(1)));

        ulong genBefore;
        using (DatumFileReaderV2 reader = DatumFileReaderV2.Open(path))
        {
            genBefore = reader.Footer.Prologue.Generation;
        }

        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] = [new RowUpdate(0, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(2) })],
        };
        DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates);

        using DatumFileReaderV2 readerAfter = DatumFileReaderV2.Open(path);
        Assert.Equal(genBefore + 1, readerAfter.Footer.Prologue.Generation);
        Assert.Equal(genBefore, readerAfter.Footer.Prologue.BaseGeneration);
    }

    // ──────────────────── crash-safety smoke ────────────────────

    [Fact]
    public void Rewrite_TornTail_RecoversToLastClean()
    {
        // Writer commits a page, then RewritePages partially executes
        // (page bytes appended, but footer/tail never written) — simulate
        // by truncating after the appended page bytes and before any new
        // tail. The next open via OpenForAppend / RewritePages must
        // recover and see the original commit's data, not the partial
        // rewrite.
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = WriteFile("torn.datum", [column], BuildSingleColumnRows(
            DataValue.FromInt32(1),
            DataValue.FromInt32(2)));

        long lengthAfterFirstCommit = new FileInfo(path).Length;

        // Append garbage past the clean tail to simulate a torn write.
        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Position = fs.Length;
            fs.Write(new byte[] { 0x55, 0x66, 0x77 });
        }

        // The rewriter's RecoverIfTorn must scan back to the last clean
        // tail, then proceed with the (now no-op-ish) rewrite. Apply a
        // real update so we can verify it lands on top of the recovered
        // baseline.
        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] = [new RowUpdate(0, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(99) })],
        };
        DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates);

        DataValue[] decoded = ReadAllRows(path, columnIndex: 0, pageIndex: 0);
        Assert.Equal(99, decoded[0].AsInt32());
        Assert.Equal(2, decoded[1].AsInt32());
    }

    // ──────────────────── input validation ────────────────────

    [Fact]
    public void Rewrite_OutOfRangePage_Throws()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = WriteFile("oor_page.datum", [column], BuildSingleColumnRows(DataValue.FromInt32(1)));

        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [42] = [new RowUpdate(0, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(2) })],
        };
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates));
        Assert.Contains("page index 42", ex.Message);
    }

    [Fact]
    public void Rewrite_OutOfRangeRow_Throws()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = WriteFile("oor_row.datum", [column], BuildSingleColumnRows(DataValue.FromInt32(1)));

        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] = [new RowUpdate(99, new Dictionary<int, DataValue> { [0] = DataValue.FromInt32(2) })],
        };
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates));
        Assert.Contains("row-in-page 99", ex.Message);
    }

    [Fact]
    public void Rewrite_OutOfRangeColumn_Throws()
    {
        ColumnDescriptorV2 column = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = WriteFile("oor_col.datum", [column], BuildSingleColumnRows(DataValue.FromInt32(1)));

        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] = [new RowUpdate(0, new Dictionary<int, DataValue> { [42] = DataValue.FromInt32(2) })],
        };
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates));
        Assert.Contains("column index 42", ex.Message);
    }

    [Fact]
    public void Rewrite_VariableSlot_WithoutSidecarPath_Throws()
    {
        ColumnDescriptorV2 column = new("s", DataKind.String, EncoderKind.VariableSlot, IsNullable: false);
        string path = WriteFile("vs.datum", [column], BuildSingleColumnRows(
            DataValue.FromString("a"),
            DataValue.FromString("b")));

        Dictionary<int, IReadOnlyList<RowUpdate>> updates = new()
        {
            [0] = [new RowUpdate(0, new Dictionary<int, DataValue> { [0] = DataValue.FromString("c") })],
        };
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => DatumFileWriterV2.RewritePages(path, sidecarPath: null, updates));
        Assert.Contains("VariableSlot", ex.Message);
        Assert.Contains("sidecarPath", ex.Message);
    }

    // ──────────────────── helpers ────────────────────

    private static IReadOnlyList<RowSpec> BuildSingleColumnRows(params DataValue[] values)
    {
        RowSpec[] rows = new RowSpec[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            rows[i] = new RowSpec(values[i]);
        }
        return rows;
    }

    private static void AddRow(Pool pool, RowBatch batch, params DataValue[] values)
    {
        DataValue[] row = pool.RentDataValues(values.Length);
        for (int i = 0; i < values.Length; i++) row[i] = values[i];
        batch.Add(row);
    }

    private string WriteFile(string fileName, ColumnDescriptorV2[] columns, IReadOnlyList<RowSpec> rows)
    {
        Pool pool = CreatePool();
        ColumnLookup lookup = new(columns.Select(c => c.Name).ToArray());
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: rows.Count, arena: arena);
        for (int i = 0; i < rows.Count; i++)
        {
            DataValue[] row = pool.RentDataValues(columns.Length);
            for (int c = 0; c < columns.Length; c++) row[c] = rows[i].Values[c];
            batch.Add(row);
        }

        string path = Path.Combine(_tempDir, fileName);
        using (DatumFileWriterV2 writer = new(path, sidecarPath: null))
        {
            writer.Initialize(columns);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }
        return path;
    }

    private static DataValue[] ReadAllRows(string path, int columnIndex, int pageIndex)
    {
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        IPageDecoderV2 decoder = reader.OpenPageDecoder(columnIndex, pageIndex);
        int rowCount = reader.Footer.Columns[columnIndex].Pages[pageIndex].RowCount;
        DataValue[] values = new DataValue[rowCount];
        for (int i = 0; i < rowCount; i++) values[i] = decoder.ReadValue(i);
        return values;
    }

    private sealed record RowSpec(params DataValue[] Values);
}
