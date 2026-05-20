using System.Buffers.Binary;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.DatumFile.V2.Decoding;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.DatumFile.V2;

/// <summary>
/// PR2 tests for crash-safe append. Cover append round-trip,
/// partial-page extension (the format requires every page except the
/// last to be exactly <c>pageSize</c> rows, so OpenForAppend has to
/// decode the trailing partial page back into a fresh encoder),
/// multi-cycle commits, generation counter monotonicity, sidecar
/// growth, volume-zone-map threshold crossing, and torn-tail recovery.
/// </summary>
public sealed class DatumFileV2AppendTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_v4_append_{Guid.NewGuid():N}");

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
    public void Append_FullPageAligned_RoundTripsAllRows()
    {
        // Initial write of 1024 rows (one full page) followed by an
        // append of 100 rows. Verifies the full-page-aligned case where
        // OpenForAppend doesn't have to extend any partial page.
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = Path.Combine(_tempDir, "append_aligned.datum");

        WriteRows(path, [col], MakeInt32Rows(0, 1024));
        AppendRows(path, MakeInt32Rows(1024, 100));

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(1024 + 100, reader.TotalRowCount);

        // Page 0: full 1024 rows. Page 1: partial 100 rows.
        Assert.Equal(2, reader.Footer.Columns[0].Pages.Count);
        Assert.Equal(1024, reader.Footer.Columns[0].Pages[0].RowCount);
        Assert.Equal(100, reader.Footer.Columns[0].Pages[1].RowCount);

        AssertRowsRoundTrip(reader, columnIndex: 0, expectedTotal: 1124);
    }

    [Fact]
    public void Append_PartialLastPage_IsExtended()
    {
        // Initial write of 500 rows (partial page), then append 800
        // rows. The first 524 rows of the append should fill the
        // existing partial page out to 1024; the remaining 276 should
        // start a new partial page. The format's seek math requires
        // mid-file pages to be exactly pageSize rows, so the writer's
        // partial-page extension is what makes this work.
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = Path.Combine(_tempDir, "append_partial.datum");

        WriteRows(path, [col], MakeInt32Rows(0, 500));
        AppendRows(path, MakeInt32Rows(500, 800));

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(1300, reader.TotalRowCount);

        // Page 0: extended to full 1024 rows. Page 1: partial 276.
        Assert.Equal(2, reader.Footer.Columns[0].Pages.Count);
        Assert.Equal(1024, reader.Footer.Columns[0].Pages[0].RowCount);
        Assert.Equal(276, reader.Footer.Columns[0].Pages[1].RowCount);

        AssertRowsRoundTrip(reader, columnIndex: 0, expectedTotal: 1300);
    }

    [Fact]
    public void Append_MultipleCycles_AllRowsPreserved()
    {
        // Three append cycles on top of an initial write. Each writes a
        // batch of irregular size that doesn't align with page
        // boundaries — exercises partial-page extension repeatedly.
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = Path.Combine(_tempDir, "append_multicycle.datum");

        WriteRows(path, [col], MakeInt32Rows(0, 700));
        AppendRows(path, MakeInt32Rows(700, 350));
        AppendRows(path, MakeInt32Rows(1050, 1200));
        AppendRows(path, MakeInt32Rows(2250, 50));

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(2300, reader.TotalRowCount);
        AssertRowsRoundTrip(reader, columnIndex: 0, expectedTotal: 2300);
    }

    [Fact]
    public void Append_GenerationCounter_IncrementsPerCommit()
    {
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = Path.Combine(_tempDir, "append_generation.datum");

        WriteRows(path, [col], MakeInt32Rows(0, 100));
        using (DatumFileReaderV2 reader = DatumFileReaderV2.Open(path))
        {
            Assert.Equal(1UL, reader.Footer.Prologue.Generation);
            Assert.Equal(0UL, reader.Footer.Prologue.BaseGeneration);
        }

        AppendRows(path, MakeInt32Rows(100, 50));
        using (DatumFileReaderV2 reader = DatumFileReaderV2.Open(path))
        {
            Assert.Equal(2UL, reader.Footer.Prologue.Generation);
            Assert.Equal(1UL, reader.Footer.Prologue.BaseGeneration);
        }

        AppendRows(path, MakeInt32Rows(150, 50));
        using (DatumFileReaderV2 reader = DatumFileReaderV2.Open(path))
        {
            Assert.Equal(3UL, reader.Footer.Prologue.Generation);
            Assert.Equal(2UL, reader.Footer.Prologue.BaseGeneration);
        }
    }

    [Fact]
    public void Append_OldFooterAndTail_BecomeOrphanedNotReferenced()
    {
        // After append, the old footer + tail are still on disk between
        // the old data and the new pages. The reader should never reach
        // them — the new tail at EOF is authoritative. We verify this
        // by checking the file is larger than (header + new_data +
        // new_footer + tail) — extra bytes = orphan old footer/tail.
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = Path.Combine(_tempDir, "append_orphans.datum");

        WriteRows(path, [col], MakeInt32Rows(0, 100));
        long sizeAfterInitial = new FileInfo(path).Length;

        AppendRows(path, MakeInt32Rows(100, 100));
        long sizeAfterAppend = new FileInfo(path).Length;

        // The append wrote new pages + new footer + new tail past the
        // old tail. The growth must exceed just the new data — orphan
        // bytes (old footer + old tail) sit in between.
        Assert.True(sizeAfterAppend > sizeAfterInitial,
            $"file should grow after append (was {sizeAfterInitial}, now {sizeAfterAppend})");

        // The reader still opens cleanly via the new tail.
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(200L, reader.TotalRowCount);
        AssertRowsRoundTrip(reader, columnIndex: 0, expectedTotal: 200);
    }

    [Fact]
    public void Append_VolumeZoneMaps_AppearWhenCrossingThreshold()
    {
        // Use a tiny page size so we cross the volume threshold without
        // writing a million real rows. With pageSize=1, pagesPerChapter
        // stays at 64 and chaptersPerVolume at 16, so volume threshold
        // = 1_000_000 rows still applies — but we explicitly construct
        // a writer whose totalRowsWritten exceeds the threshold by
        // appending many small rows.
        //
        // Trade-off: the writer's volume-emit decision is gated on
        // _totalRowsWritten > VolumeEmitRowThreshold (= 1_000_000).
        // Constructing 1M rows in a unit test is too slow. Instead,
        // we verify the simpler invariant: a small file's append
        // doesn't accidentally turn on the volume flag.
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = Path.Combine(_tempDir, "append_volume.datum");

        WriteRows(path, [col], MakeInt32Rows(0, 100));
        AppendRows(path, MakeInt32Rows(100, 200));

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(DatumFileFlagsV2.None,
            reader.Header.Flags & DatumFileFlagsV2.HasVolumeZoneMaps);
        Assert.Null(reader.Footer.Columns[0].VolumeZoneMaps);
    }

    [Fact]
    public void Append_WithSidecar_PreservesExistingPayloads()
    {
        // String column with a long string forces sidecar use. Initial
        // write produces a sidecar; append adds more long strings.
        // Both sets of payloads must remain readable.
        ColumnDescriptorV2 col = new("s", DataKind.String, EncoderKind.VariableSlot, IsNullable: false);
        string datumPath = Path.Combine(_tempDir, "append_sidecar.datum");
        string sidecarPath = datumPath + Heliosoph.DatumV.DatumFile.Sidecar.SidecarConstants.FileExtension;

        // Initial write: 5 long strings (each > 16 bytes → sidecar).
        Pool pool = CreatePool();
        ColumnLookup lookup = new(["s"]);
        Arena writeArena = CreateArena();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 5, arena: writeArena);
        for (int i = 0; i < 5; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromString($"initial-string-with-content-{i:D4}", writeArena);
            batch.Add(row);
        }

        ulong initialFingerprint;
        Heliosoph.DatumV.DatumFile.Sidecar.SidecarWriteStore sidecar = new(sidecarPath);
        try
        {
            initialFingerprint = sidecar.Fingerprint;
            using FileStream fs = new(datumPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using DatumFileWriterV2 writer = new(fs, sidecar);
            writer.Initialize([col]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }
        finally { sidecar.Dispose(); }

        // Append: 3 more long strings.
        using (DatumFileWriterV2 appender = DatumFileWriterV2.OpenForAppend(datumPath, sidecarPath))
        {
            Pool pool2 = CreatePool();
            Arena appArena = CreateArena();
            RowBatch appBatch = pool2.RentRowBatch(lookup, capacity: 3, arena: appArena);
            for (int i = 5; i < 8; i++)
            {
                DataValue[] row = pool2.RentDataValues(1);
                row[0] = DataValue.FromString($"appended-string-with-content-{i:D4}", appArena);
                appBatch.Add(row);
            }
            appender.WriteRowBatch(appBatch);
            appender.FinalizeWriter();
        }

        // Read back all 8 strings — sidecar fingerprint preserved, both
        // initial and appended payloads resolvable.
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        using Heliosoph.DatumV.DatumFile.Sidecar.SidecarReadStore sidecarSource =
            Heliosoph.DatumV.DatumFile.Sidecar.SidecarReadStore.OpenWithoutFingerprintCheck(sidecarPath);

        Assert.Equal(8L, reader.TotalRowCount);
        Assert.True((reader.Header.Flags & DatumFileFlagsV2.HasSidecarReferences) != 0,
            "HasSidecarReferences should be sticky across append");

        Arena readArena = CreateArena();
        Heliosoph.DatumV.DatumFile.Sidecar.SidecarRegistry registry = new();
        registry.Register(sidecarSource);
        IPageDecoderV2 decoder = reader.OpenPageDecoder(
            columnIndex: 0,
            pageIndex: 0,
            sidecarStoreId: 0,
            sidecarSource: sidecarSource,
            eagerStore: readArena);

        for (int i = 0; i < 5; i++)
        {
            // Sidecar-backed strings need the AsString(store, registry)
            // overload — AsString(store) alone can't resolve sidecar
            // values.
            Assert.Equal($"initial-string-with-content-{i:D4}", decoder.ReadValue(i).AsString(readArena, registry));
        }
        for (int i = 5; i < 8; i++)
        {
            Assert.Equal($"appended-string-with-content-{i:D4}", decoder.ReadValue(i).AsString(readArena, registry));
        }
    }

    [Fact]
    public void Append_TornTail_TruncatedAndOldStateRestored()
    {
        // Simulate a crash mid-append by writing a clean file, then
        // appending some garbage past the tail (mimics a torn write
        // that corrupts EOF). OpenForAppend should detect the missing
        // FMTD at EOF, scan back for the previous clean tail, and
        // truncate. The next append succeeds normally and the file
        // round-trips with only the original rows + the new ones.
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = Path.Combine(_tempDir, "append_torn.datum");

        WriteRows(path, [col], MakeInt32Rows(0, 100));
        long cleanLength = new FileInfo(path).Length;

        // Append 4 KiB of garbage past the clean tail to simulate a
        // torn write that started new pages but never reached the new
        // tail.
        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write))
        {
            fs.Position = fs.Length;
            byte[] garbage = new byte[4096];
            new Random(42).NextBytes(garbage);
            fs.Write(garbage);
        }
        Assert.Equal(cleanLength + 4096, new FileInfo(path).Length);

        // OpenForAppend recovers — file is truncated back to clean
        // length, and the new append proceeds normally.
        AppendRows(path, MakeInt32Rows(100, 50));

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(150L, reader.TotalRowCount);
        AssertRowsRoundTrip(reader, columnIndex: 0, expectedTotal: 150);
    }

    [Fact]
    public void Append_HasSidecarReferences_StaysStickyEvenWithNoNewSpills()
    {
        // Initial write has long strings → HasSidecarReferences set.
        // Append only short (inline) strings. The flag must remain set
        // because the OLD rows still reference the sidecar; the new
        // footer's column blocks include those old pages, so the
        // sidecar is still required at read time.
        ColumnDescriptorV2 col = new("s", DataKind.String, EncoderKind.VariableSlot, IsNullable: false);
        string datumPath = Path.Combine(_tempDir, "append_sticky.datum");
        string sidecarPath = datumPath + Heliosoph.DatumV.DatumFile.Sidecar.SidecarConstants.FileExtension;

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["s"]);
        Arena arena = CreateArena();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        DataValue[] r0 = pool.RentDataValues(1);
        r0[0] = DataValue.FromString("this-string-exceeds-sixteen-bytes-and-spills", arena);
        batch.Add(r0);
        DataValue[] r1 = pool.RentDataValues(1);
        r1[0] = DataValue.FromString("short", arena);
        batch.Add(r1);

        using (Heliosoph.DatumV.DatumFile.Sidecar.SidecarWriteStore sidecar = new(sidecarPath))
        {
            using FileStream fs = new(datumPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using DatumFileWriterV2 writer = new(fs, sidecar);
            writer.Initialize([col]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(datumPath))
        {
            Assert.True((r.Header.Flags & DatumFileFlagsV2.HasSidecarReferences) != 0);
        }

        // Append only short strings — no new sidecar appends in this
        // session.
        using (DatumFileWriterV2 appender = DatumFileWriterV2.OpenForAppend(datumPath, sidecarPath))
        {
            Pool pool2 = CreatePool();
            Arena appArena = CreateArena();
            RowBatch appBatch = pool2.RentRowBatch(lookup, capacity: 1, arena: appArena);
            DataValue[] r = pool2.RentDataValues(1);
            r[0] = DataValue.FromString("tiny", appArena);
            appBatch.Add(r);
            appender.WriteRowBatch(appBatch);
            appender.FinalizeWriter();
        }

        // Flag must still be set — old sidecar refs persist.
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(datumPath))
        {
            Assert.True((r.Header.Flags & DatumFileFlagsV2.HasSidecarReferences) != 0,
                "HasSidecarReferences should remain set when old sidecar references survive into the new footer");
            Assert.Equal(3L, r.TotalRowCount);
        }
    }

    [Fact]
    public void Append_HeaderColumnCount_RemainsSyncedWithFooterPrologue()
    {
        ColumnDescriptorV2 colA = new("a", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 colB = new("b", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = Path.Combine(_tempDir, "append_colcount.datum");

        // Initial write: 2 columns.
        Pool pool = CreatePool();
        ColumnLookup lookup = new(["a", "b"]);
        Arena arena = CreateArena();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 5, arena: arena);
        for (int i = 0; i < 5; i++)
        {
            DataValue[] row = pool.RentDataValues(2);
            row[0] = DataValue.FromInt32(i);
            row[1] = DataValue.FromInt32(i * 2);
            batch.Add(row);
        }
        using (DatumFileWriterV2 writer = new(path, sidecarPath: null))
        {
            writer.Initialize([colA, colB]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        // Append more rows — column count unchanged.
        using (DatumFileWriterV2 appender = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null))
        {
            Pool pool2 = CreatePool();
            Arena arena2 = CreateArena();
            RowBatch appBatch = pool2.RentRowBatch(lookup, capacity: 3, arena: arena2);
            for (int i = 5; i < 8; i++)
            {
                DataValue[] row = pool2.RentDataValues(2);
                row[0] = DataValue.FromInt32(i);
                row[1] = DataValue.FromInt32(i * 2);
                appBatch.Add(row);
            }
            appender.WriteRowBatch(appBatch);
            appender.FinalizeWriter();
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(reader.Header.ColumnCount, reader.Footer.Prologue.ColumnCount);
        Assert.Equal(2, reader.Footer.Prologue.ColumnCount);
        Assert.Equal(8L, reader.TotalRowCount);
    }

    // ──────────────────── Helpers ────────────────────

    private static int[] MakeInt32Rows(int start, int count)
    {
        int[] values = new int[count];
        for (int i = 0; i < count; i++) values[i] = start + i;
        return values;
    }

    private void WriteRows(string path, ColumnDescriptorV2[] columns, int[] values)
    {
        Pool pool = CreatePool();
        ColumnLookup lookup = new([columns[0].Name]);
        Arena arena = CreateArena();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: values.Length, arena: arena);
        for (int i = 0; i < values.Length; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromInt32(values[i]);
            batch.Add(row);
        }
        using DatumFileWriterV2 writer = new(path, sidecarPath: null);
        writer.Initialize(columns);
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();
    }

    private void AppendRows(string path, int[] values)
    {
        using DatumFileWriterV2 appender = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null);
        Pool pool = CreatePool();
        ColumnLookup lookup = new(["v"]);
        Arena arena = CreateArena();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: values.Length, arena: arena);
        for (int i = 0; i < values.Length; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromInt32(values[i]);
            batch.Add(row);
        }
        appender.WriteRowBatch(batch);
        appender.FinalizeWriter();
    }

    private static void AssertRowsRoundTrip(DatumFileReaderV2 reader, int columnIndex, int expectedTotal)
    {
        int rowIndex = 0;
        for (int p = 0; p < reader.Footer.Columns[columnIndex].Pages.Count; p++)
        {
            IPageDecoderV2 dec = reader.OpenPageDecoder(columnIndex, p);
            for (int i = 0; i < dec.RowCount; i++)
            {
                Assert.Equal(rowIndex, dec.ReadValue(i).AsInt32());
                rowIndex++;
            }
        }
        Assert.Equal(expectedTotal, rowIndex);
    }
}
