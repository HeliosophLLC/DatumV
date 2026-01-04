using DatumIngest.DatumFile.V2;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.DatumFile.V2;

/// <summary>
/// PR4 tests for soft-drop column. Cover the round-trip (drop → re-open
/// → reader's filtered schema doesn't include the column),
/// idempotency, batched drops in one commit, sticky sidecar references
/// across drop, and that the dropped column's data stays in the footer
/// (still addressable for compaction-time reclamation in a future PR).
/// </summary>
public sealed class DatumFileV2DropColumnTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_v4_drop_{Guid.NewGuid():N}");

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
    public void DropColumn_HidesFromFilteredSchema_ButPreservesFooterBlock()
    {
        // Three-column file: a, b, c. Drop b. The filtered schema
        // surfaces only a and c, but the footer's column blocks all
        // three (with b's Tombstoned bit set) so a future compaction
        // can still find it.
        string path = WriteThreeColumnFile("drop_basic.datum");

        DatumFileWriterV2.DropColumn(path, "b");

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);

        // Filtered schema view skips tombstoned columns.
        Assert.Equal(2, reader.Columns.Count);
        Assert.Equal(["a", "c"], reader.Columns.Select(c => c.Name));

        // Raw footer view still has all three blocks.
        Assert.Equal(3, reader.Footer.Columns.Count);
        Assert.False(reader.Footer.Columns[0].Descriptor.IsTombstoned);
        Assert.True(reader.Footer.Columns[1].Descriptor.IsTombstoned);
        Assert.False(reader.Footer.Columns[2].Descriptor.IsTombstoned);

        // Header / footer-prologue ColumnCount continues to count the
        // tombstoned slot — it represents footer-block count, not live
        // schema width. The reader's filtered Columns property is the
        // live-schema accessor.
        Assert.Equal(3, reader.Footer.Prologue.ColumnCount);
    }

    [Fact]
    public void DropColumn_LiveColumnsRoundTripData()
    {
        // After dropping middle column b, the surviving columns a and c
        // still produce correct data via the page decoders. The footer
        // still references each column's pages by absolute file offset,
        // so the reader's per-column page reads are unaffected by the
        // tombstone bit.
        string path = WriteThreeColumnFile("drop_data_intact.datum");
        DatumFileWriterV2.DropColumn(path, "b");

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);

        // The page decoders address Footer.Columns[..] directly (not
        // the filtered view), so we walk the footer indices: a=0, c=2.
        // The filtered schema's view of "column 0" is a, "column 1" is
        // c — but at the page-decoder level we use footer indices.
        IPageDecoderV2 aDec = reader.OpenPageDecoder(columnIndex: 0, pageIndex: 0);
        IPageDecoderV2 cDec = reader.OpenPageDecoder(columnIndex: 2, pageIndex: 0);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(i, aDec.ReadValue(i).AsInt32());
            Assert.Equal(i * 100, cDec.ReadValue(i).AsInt32());
        }
    }

    [Fact]
    public void DropColumn_IsIdempotent()
    {
        string path = WriteThreeColumnFile("drop_idempotent.datum");

        DatumFileWriterV2.DropColumn(path, "b");
        ulong gen1;
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(path))
        {
            gen1 = r.Footer.Prologue.Generation;
        }

        // Dropping the same column again still increments generation
        // (we still committed a new footer) but doesn't change the
        // visible state. The "no-op" is at the bit-flip level, not the
        // commit level — that's fine; future PRs could add a "skip
        // commit when nothing changed" optimization.
        DatumFileWriterV2.DropColumn(path, "b");

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(2, reader.Columns.Count);
        Assert.True(reader.Footer.Columns[1].Descriptor.IsTombstoned);
        // Idempotent at the schema level even if commit did happen.
        Assert.Equal(["a", "c"], reader.Columns.Select(c => c.Name));
    }

    [Fact]
    public void DropColumn_MissingName_Throws()
    {
        string path = WriteThreeColumnFile("drop_missing.datum");

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => DatumFileWriterV2.DropColumn(path, "doesnotexist"));
        Assert.Contains("doesnotexist", ex.Message);
        Assert.Contains("a, b, c", ex.Message);
    }

    [Fact]
    public void DropColumns_BatchInOneCommit()
    {
        // DropColumns drops multiple columns in a single commit.
        // Generation bumps by exactly 1 (single tail flip), not by the
        // count of dropped columns.
        string path = WriteThreeColumnFile("drop_batch.datum");

        ulong genBefore;
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(path))
        {
            genBefore = r.Footer.Prologue.Generation;
        }

        DatumFileWriterV2.DropColumns(path, ["a", "c"]);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Single(reader.Columns);
        Assert.Equal("b", reader.Columns[0].Name);
        Assert.Equal(genBefore + 1, reader.Footer.Prologue.Generation);
    }

    [Fact]
    public void DropColumn_ProviderSchemaFiltersTombstoned()
    {
        // The DatumFileTableProviderV2 layer also filters tombstoned
        // columns out of its engine-facing Schema, which is what query
        // planners see. SELECT * against a file with a dropped column
        // should not include the dropped column.
        string path = WriteThreeColumnFile("drop_provider.datum");
        DatumFileWriterV2.DropColumn(path, "b");

        DatumIngest.Catalog.TableDescriptor descriptor = new("t", path);
        using DatumIngest.Catalog.Providers.DatumFileTableProviderV2 provider = new(descriptor, new Pool(new PoolBacking()));

        DatumIngest.Model.Schema schema = provider.GetSchema();
        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal(["a", "c"], schema.Columns.Select(c => c.Name));
    }

    [Fact]
    public void DropColumn_PreservesSidecarReferences()
    {
        // File with a String column that spilled to sidecar. Drop a
        // different (non-spilled) column. Sidecar fingerprint and data
        // must remain intact, and HasSidecarReferences stays set
        // because the surviving String column still references the
        // sidecar.
        string datumPath = Path.Combine(_tempDir, "drop_with_sidecar.datum");
        string sidecarPath = datumPath + DatumIngest.DatumFile.Sidecar.SidecarConstants.FileExtension;

        ColumnDescriptorV2 idCol = new("id", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 nameCol = new("name", DataKind.String, EncoderKind.VariableSlot, IsNullable: false);
        ColumnDescriptorV2 dropMeCol = new("drop_me", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["id", "name", "drop_me"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 3, arena: arena);
        for (int i = 0; i < 3; i++)
        {
            DataValue[] row = pool.RentDataValues(3);
            row[0] = DataValue.FromInt32(i);
            row[1] = DataValue.FromString($"long-string-row-{i}-pads-past-sixteen-bytes", arena);
            row[2] = DataValue.FromInt32(i * 999);
            batch.Add(row);
        }

        using (DatumIngest.DatumFile.Sidecar.SidecarWriteStore sidecar = new(sidecarPath))
        {
            using FileStream fs = new(datumPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            using DatumFileWriterV2 writer = new(fs, sidecar);
            writer.Initialize([idCol, nameCol, dropMeCol]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        DatumFileWriterV2.DropColumn(datumPath, "drop_me");

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        Assert.Equal(2, reader.Columns.Count);
        Assert.True((reader.Header.Flags & DatumFileFlagsV2.HasSidecarReferences) != 0,
            "HasSidecarReferences should remain set — surviving String column still references the sidecar");

        // Sidecar file must still exist with the same payload (we only
        // wrote a new .datum footer; the .datum-blob is untouched).
        Assert.True(File.Exists(sidecarPath));

        using DatumIngest.DatumFile.Sidecar.SidecarReadStore sidecarSource =
            DatumIngest.DatumFile.Sidecar.SidecarReadStore.OpenWithoutFingerprintCheck(sidecarPath);
        DatumIngest.DatumFile.Sidecar.SidecarRegistry registry = new();
        registry.Register(sidecarSource);

        // Read the surviving String column at footer index 1.
        Arena readArena = new();
        IPageDecoderV2 nameDec = reader.OpenPageDecoder(
            columnIndex: 1, pageIndex: 0,
            sidecarStoreId: 0, sidecarSource: sidecarSource, eagerStore: readArena);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal($"long-string-row-{i}-pads-past-sixteen-bytes",
                nameDec.ReadValue(i).AsString(readArena, registry));
        }
    }

    [Fact]
    public void DropColumn_FilteredSchemaIndexAlignment()
    {
        // Drop column at footer index 0. The remaining live columns
        // are at footer indices 1 and 2 — but the filtered schema
        // presents them as positions 0 and 1. Reader.Columns must
        // surface them in the right order, and the provider's schema
        // must stay consistent.
        string path = WriteThreeColumnFile("drop_first.datum");
        DatumFileWriterV2.DropColumn(path, "a");

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(2, reader.Columns.Count);
        Assert.Equal("b", reader.Columns[0].Name);
        Assert.Equal("c", reader.Columns[1].Name);

        // Drop the LAST footer column too. Filtered schema is just b.
        DatumFileWriterV2.DropColumn(path, "c");
        using DatumFileReaderV2 reader2 = DatumFileReaderV2.Open(path);
        Assert.Single(reader2.Columns);
        Assert.Equal("b", reader2.Columns[0].Name);
    }

    [Fact]
    public void DropColumn_GenerationCounter_BumpsByOne()
    {
        // A drop is just another commit — generation increments by one
        // and baseGeneration tracks the prior commit, identical to
        // append's commit semantics.
        string path = WriteThreeColumnFile("drop_generation.datum");

        ulong gen0;
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(path))
        {
            gen0 = r.Footer.Prologue.Generation;
        }

        DatumFileWriterV2.DropColumn(path, "b");

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(gen0 + 1, reader.Footer.Prologue.Generation);
        Assert.Equal(gen0, reader.Footer.Prologue.BaseGeneration);
    }

    // ──────────────────── Helpers ────────────────────

    private string WriteThreeColumnFile(string fileName)
    {
        ColumnDescriptorV2 colA = new("a", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 colB = new("b", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 colC = new("c", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["a", "b", "c"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 3, arena: arena);
        for (int i = 0; i < 3; i++)
        {
            DataValue[] row = pool.RentDataValues(3);
            row[0] = DataValue.FromInt32(i);
            row[1] = DataValue.FromInt32(i * 10);
            row[2] = DataValue.FromInt32(i * 100);
            batch.Add(row);
        }

        string path = Path.Combine(_tempDir, fileName);
        using DatumFileWriterV2 writer = new(path, sidecarPath: null);
        writer.Initialize([colA, colB, colC]);
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();
        return path;
    }
}
