using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.V2;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.DatumFile.V2;

/// <summary>
/// PR6 tests for ALTER TABLE ADD COLUMN with all-null backfill. Cover
/// the round-trip (add → re-open → schema includes the new column with
/// nulls for existing rows), mixed kinds, then-append (writes values
/// for the newly added column), name collision rejection,
/// non-nullable rejection, and partial-page alignment.
/// </summary>
public sealed class DatumFileV2AddColumnTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_v4_addcol_{Guid.NewGuid():N}");

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
    public void AddColumn_BackfillsExistingRowsWithNull()
    {
        // 100-row file with one column "v". Add a nullable Int64
        // column "score". Existing 100 rows should read null for
        // "score"; the new column appears in the live schema.
        string path = WriteOneColumnFile("addcol_basic.datum", rowCount: 100);

        ColumnDescriptorV2 newCol = new("score", DataKind.Int64, EncoderKind.FixedWidth, IsNullable: true);
        DatumFileWriterV2.AddColumn(path, newCol);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(2, reader.Columns.Count);
        Assert.Equal("v", reader.Columns[0].Name);
        Assert.Equal("score", reader.Columns[1].Name);
        Assert.Equal(100L, reader.TotalRowCount);

        // Read every row of the new column — every value must be null.
        IPageDecoderV2 decoder = reader.OpenPageDecoder(columnIndex: 1, pageIndex: 0);
        for (int i = 0; i < decoder.RowCount; i++)
        {
            Assert.True(decoder.ReadValue(i).IsNull,
                $"new column value at row {i} should be null (all-null backfill)");
        }
    }

    [Fact]
    public void AddColumn_ExistingColumnDataIntact()
    {
        // After an AddColumn, the existing column's pages are
        // untouched — page descriptors for the original column carry
        // forward verbatim and reads return the original values.
        string path = WriteOneColumnFile("addcol_existing_intact.datum", rowCount: 50);

        ColumnDescriptorV2 newCol = new("flag", DataKind.Boolean, EncoderKind.BitPackedBoolean, IsNullable: true);
        DatumFileWriterV2.AddColumn(path, newCol);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        IPageDecoderV2 vDec = reader.OpenPageDecoder(columnIndex: 0, pageIndex: 0);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(i, vDec.ReadValue(i).AsInt32());
        }
    }

    [Fact]
    public void AddColumn_GenerationCounter_BumpsByOne()
    {
        string path = WriteOneColumnFile("addcol_gen.datum", rowCount: 10);

        ulong gen0;
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(path))
        {
            gen0 = r.Footer.Prologue.Generation;
        }

        ColumnDescriptorV2 newCol = new("c", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: true);
        DatumFileWriterV2.AddColumn(path, newCol);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(gen0 + 1, reader.Footer.Prologue.Generation);
        Assert.Equal(gen0, reader.Footer.Prologue.BaseGeneration);
        Assert.Equal(2, reader.Footer.Prologue.ColumnCount);
    }

    [Fact]
    public void AddColumn_ThenAppend_NewColumnReceivesValues()
    {
        // Add a column, then append more rows in the same session.
        // The new rows must include values for the new column.
        // The result: original 50 rows null for new column, appended
        // 20 rows have values.
        string path = WriteOneColumnFile("addcol_then_append.datum", rowCount: 50);

        using (DatumFileWriterV2 writer = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null))
        {
            writer.AddColumn(new ColumnDescriptorV2("score", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: true));

            // Now writer has 2 columns. Append 20 new rows providing
            // values for both.
            Pool pool = new(new PoolBacking());
            ColumnLookup lookup = new(["v", "score"]);
            Arena arena = new();
            RowBatch batch = pool.RentRowBatch(lookup, capacity: 20, arena: arena);
            for (int i = 50; i < 70; i++)
            {
                DataValue[] row = pool.RentDataValues(2);
                row[0] = DataValue.FromInt32(i);
                row[1] = DataValue.FromInt32(i * 1000);
                batch.Add(row);
            }
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(70L, reader.TotalRowCount);

        // Read column 0 (v): 0..69
        // Read column 1 (score): null for 0..49, then 50000..69000
        ReadAllRows(reader, 0, out int[] vAll, out bool[] vNulls);
        ReadAllRows(reader, 1, out int[] scoreAll, out bool[] scoreNulls);

        Assert.Equal(70, vAll.Length);
        for (int i = 0; i < 70; i++) Assert.Equal(i, vAll[i]);

        Assert.Equal(70, scoreAll.Length);
        for (int i = 0; i < 50; i++) Assert.True(scoreNulls[i], $"score row {i} should be null");
        for (int i = 50; i < 70; i++)
        {
            Assert.False(scoreNulls[i]);
            Assert.Equal(i * 1000, scoreAll[i]);
        }
    }

    [Fact]
    public void AddColumn_DuplicateName_Throws()
    {
        string path = WriteOneColumnFile("addcol_dup.datum", rowCount: 5);

        // Existing column "v" — adding another "v" must fail.
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => DatumFileWriterV2.AddColumn(path, new ColumnDescriptorV2(
                "v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: true)));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void AddColumn_DuplicateNameOfTombstoned_Throws()
    {
        // Even after dropping a column, the same name can't be re-used
        // (PR6 conservative rule — undrop / compact are the recovery
        // paths).
        string path = WriteOneColumnFile("addcol_tombstoned_collision.datum", rowCount: 5);

        // Need a second column to drop, since we can't drop the only
        // remaining column.
        DatumFileWriterV2.AddColumn(path, new ColumnDescriptorV2(
            "x", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: true));
        DatumFileWriterV2.DropColumn(path, "x");

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => DatumFileWriterV2.AddColumn(path, new ColumnDescriptorV2(
                "x", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: true)));
        Assert.Contains("already exists", ex.Message);
        Assert.Contains("IsTombstoned = True", ex.Message);
    }

    [Fact]
    public void AddColumn_NonNullable_Throws()
    {
        string path = WriteOneColumnFile("addcol_nonnullable.datum", rowCount: 5);

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => DatumFileWriterV2.AddColumn(path, new ColumnDescriptorV2(
                "x", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false)));
        Assert.Contains("nullable", ex.Message);
    }

    [Fact]
    public void AddColumn_BatchInOneCommit()
    {
        // AddColumns adds multiple columns in a single commit.
        // Generation bumps by exactly 1.
        string path = WriteOneColumnFile("addcol_batch.datum", rowCount: 10);

        ulong gen0;
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(path))
        {
            gen0 = r.Footer.Prologue.Generation;
        }

        DatumFileWriterV2.AddColumns(path, [
            new ColumnDescriptorV2("a", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: true),
            new ColumnDescriptorV2("b", DataKind.Float64, EncoderKind.FixedWidth, IsNullable: true),
            new ColumnDescriptorV2("c", DataKind.String, EncoderKind.VariableSlot, IsNullable: true),
        ]);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(4, reader.Columns.Count);
        Assert.Equal(["v", "a", "b", "c"], reader.Columns.Select(c => c.Name));
        Assert.Equal(gen0 + 1, reader.Footer.Prologue.Generation);
    }

    [Fact]
    public void AddColumn_PartialLastPage_AlignmentPreserved()
    {
        // File has rows that don't divide evenly by pageSize, so the
        // last page is partial. New column's pages must end with a
        // partial page of the same size, so the format's
        // pageIndex = startRow / pageSize math stays valid.
        const int pageSize = 32;
        string path = Path.Combine(_tempDir, "addcol_partial.datum");
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["v"]);
        Arena arena = new();
        const int rowCount = 100;  // 3 full pages of 32 + 1 partial of 4
        RowBatch batch = pool.RentRowBatch(lookup, capacity: rowCount, arena: arena);
        for (int i = 0; i < rowCount; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromInt32(i);
            batch.Add(row);
        }
        using (DatumFileWriterV2 writer = new(path, sidecarPath: null))
        {
            writer.SetPageSize(pageSize);
            writer.Initialize([col]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        DatumFileWriterV2.AddColumn(path, new ColumnDescriptorV2(
            "extra", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: true));

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);

        // Both columns must have 4 pages with row counts [32, 32, 32, 4].
        var vPages = reader.Footer.Columns[0].Pages;
        var extraPages = reader.Footer.Columns[1].Pages;
        Assert.Equal(4, vPages.Count);
        Assert.Equal(4, extraPages.Count);
        for (int p = 0; p < 4; p++)
        {
            Assert.Equal(vPages[p].RowCount, extraPages[p].RowCount);
        }
        Assert.Equal(4, vPages[3].RowCount);
        Assert.Equal(4, extraPages[3].RowCount);
    }

    [Fact]
    public void AddColumn_StringKind_VariableSlotEncoder()
    {
        // String columns use the VariableSlot encoder. Verify the
        // backfill produces all-null pages correctly for that
        // encoder kind too (exercises a different code path than
        // FixedWidth nulls).
        string path = WriteOneColumnFile("addcol_string.datum", rowCount: 30);

        DatumFileWriterV2.AddColumn(path, new ColumnDescriptorV2(
            "label", DataKind.String, EncoderKind.VariableSlot, IsNullable: true));

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        IPageDecoderV2 dec = reader.OpenPageDecoder(columnIndex: 1, pageIndex: 0);
        for (int i = 0; i < 30; i++)
        {
            Assert.True(dec.ReadValue(i).IsNull);
        }
    }

    [Fact]
    public void AddColumn_AfterWriteRowBatch_AlignsCorrectly()
    {
        // AddColumn after a WriteRowBatch must work — pumping
        // _totalRowsWritten nulls aligns the new column with whatever
        // lockstep state the existing columns share, regardless of
        // whether rows have been appended in this session yet.
        string path = WriteOneColumnFile("addcol_after_write.datum", rowCount: 5);

        using (DatumFileWriterV2 writer = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null))
        {
            // Append one row first (1 column).
            Pool pool = new(new PoolBacking());
            ColumnLookup lookup = new(["v"]);
            Arena arena = new();
            RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromInt32(99);
            batch.Add(row);
            writer.WriteRowBatch(batch);

            // Now add a column — backfills nulls for all 6 rows
            // currently in flight (5 original + 1 just appended).
            writer.AddColumn(new ColumnDescriptorV2(
                "extra", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: true));

            writer.FinalizeWriter();
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(6L, reader.TotalRowCount);
        Assert.Equal(2, reader.Columns.Count);

        // All 6 rows of the "extra" column must be null (backfilled
        // for the original 5 + the append-then-add one).
        ReadAllRows(reader, columnIndex: 1, out _, out bool[] extraNulls);
        for (int i = 0; i < 6; i++)
        {
            Assert.True(extraNulls[i], $"row {i} of 'extra' should be null");
        }
    }

    [Fact]
    public void AddColumn_ProviderSchemaIncludesNewColumn()
    {
        // The DatumFileTableProviderV2's engine-facing schema must
        // include the newly added column so SQL planners see it on
        // SELECT *.
        string path = WriteOneColumnFile("addcol_provider.datum", rowCount: 10);
        DatumFileWriterV2.AddColumn(path, new ColumnDescriptorV2(
            "added", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: true));

        TableDescriptor descriptor = new("t", path);
        using DatumFileTableProviderV2 provider = new(descriptor, new Pool(new PoolBacking()));
        Schema schema = provider.GetSchema();
        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal(["v", "added"], schema.Columns.Select(c => c.Name));
    }

    // ──────────────────── Helpers ────────────────────

    private string WriteOneColumnFile(string fileName, int rowCount)
    {
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = Path.Combine(_tempDir, fileName);

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
        using DatumFileWriterV2 writer = new(path, sidecarPath: null);
        writer.Initialize([col]);
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();
        return path;
    }

    private static void ReadAllRows(DatumFileReaderV2 reader, int columnIndex, out int[] values, out bool[] nulls)
    {
        long total = reader.TotalRowCount;
        values = new int[total];
        nulls = new bool[total];
        int outIndex = 0;
        var pages = reader.Footer.Columns[columnIndex].Pages;
        for (int p = 0; p < pages.Count; p++)
        {
            IPageDecoderV2 dec = reader.OpenPageDecoder(columnIndex, p);
            for (int i = 0; i < dec.RowCount; i++)
            {
                DataValue v = dec.ReadValue(i);
                if (v.IsNull)
                {
                    nulls[outIndex] = true;
                }
                else
                {
                    values[outIndex] = v.AsInt32();
                }
                outIndex++;
            }
        }
    }
}
