using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.DatumFile.V2;

/// <summary>
/// PR5 tests for chapter tombstone bitmaps + soft-delete rows. Cover
/// the round-trip (delete → re-open → scan skips the row), single and
/// range deletes, idempotency, multi-chapter spans, the
/// <see cref="DatumFileFlagsV2.HasTombstones"/> flag flipping on, and
/// scan-via-provider visibility.
/// </summary>
public sealed class DatumFileV2SoftDeleteTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_v4_softdel_{Guid.NewGuid():N}");

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
    public void SoftDelete_SingleRow_HiddenFromScan()
    {
        // Write 100 rows, soft-delete row 5, scan via the provider —
        // row 5 should be absent from materialized RowBatches.
        string path = WriteSequentialRows("delete_one.datum", 100);

        DatumFileWriterV2.SoftDeleteRows(path, [5L]);

        int[] live = ScanAllRows(path);
        Assert.Equal(99, live.Length);
        Assert.DoesNotContain(5, live);
        Assert.Contains(4, live);
        Assert.Contains(6, live);
    }

    [Fact]
    public void SoftDelete_MultipleRows_OneCommit()
    {
        string path = WriteSequentialRows("delete_multi.datum", 100);

        DatumFileWriterV2.SoftDeleteRows(path, [10L, 20L, 30L, 40L, 50L]);

        int[] live = ScanAllRows(path);
        Assert.Equal(95, live.Length);
        foreach (int deleted in new[] { 10, 20, 30, 40, 50 })
        {
            Assert.DoesNotContain(deleted, live);
        }
    }

    [Fact]
    public void SoftDelete_RangeDelete()
    {
        // MarkRowsDeleted with a contiguous range. All rows in the
        // range should be hidden.
        string path = WriteSequentialRows("delete_range.datum", 100);

        using (DatumFileWriterV2 writer = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null))
        {
            writer.MarkRowsDeleted(startRow: 25, count: 10);
            writer.FinalizeWriter();
        }

        int[] live = ScanAllRows(path);
        Assert.Equal(90, live.Length);
        for (int i = 25; i < 35; i++)
        {
            Assert.DoesNotContain(i, live);
        }
    }

    [Fact]
    public void SoftDelete_HasTombstonesFlag_FlipsOn()
    {
        string path = WriteSequentialRows("delete_flag.datum", 100);

        // Before delete: flag clear.
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(path))
        {
            Assert.Equal(DatumFileFlagsV2.None, r.Header.Flags & DatumFileFlagsV2.HasTombstones);
            Assert.Empty(r.Footer.Prologue.ChapterTombstoneOffsets);
        }

        DatumFileWriterV2.SoftDeleteRows(path, [42L]);

        // After delete: flag set, prologue lists chapter offsets.
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(path))
        {
            Assert.True((r.Header.Flags & DatumFileFlagsV2.HasTombstones) != 0,
                "HasTombstones must flip on after the first soft-delete commit");
            Assert.NotEmpty(r.Footer.Prologue.ChapterTombstoneOffsets);
            // The single chapter that contains row 42 has a non-(-1)
            // offset; chapters with no deletes stay at NoTombstoneBlock.
            int chaptersWithBlocks = r.Footer.Prologue.ChapterTombstoneOffsets
                .Count(o => o != DatumFormatV2.NoTombstoneBlock);
            Assert.Equal(1, chaptersWithBlocks);
        }
    }

    [Fact]
    public void SoftDelete_GenerationCounter_BumpsByOne()
    {
        string path = WriteSequentialRows("delete_gen.datum", 100);

        ulong gen0;
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(path))
        {
            gen0 = r.Footer.Prologue.Generation;
        }

        DatumFileWriterV2.SoftDeleteRows(path, [10L, 20L, 30L]);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(gen0 + 1, reader.Footer.Prologue.Generation);
        Assert.Equal(gen0, reader.Footer.Prologue.BaseGeneration);
    }

    [Fact]
    public void SoftDelete_IsIdempotent_AtSchemaLevel()
    {
        // Marking the same row deleted twice doesn't change the visible
        // result. Each call still commits (generation bumps), but the
        // second commit's tombstone bitmap is identical to the first.
        string path = WriteSequentialRows("delete_idempotent.datum", 100);

        DatumFileWriterV2.SoftDeleteRows(path, [50L]);
        DatumFileWriterV2.SoftDeleteRows(path, [50L]);

        int[] live = ScanAllRows(path);
        Assert.Equal(99, live.Length);
        Assert.DoesNotContain(50, live);
    }

    [Fact]
    public void SoftDelete_OutOfRange_Throws()
    {
        string path = WriteSequentialRows("delete_oor.datum", 100);

        using DatumFileWriterV2 writer = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.MarkRowDeleted(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.MarkRowDeleted(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.MarkRowsDeleted(95, 10)); // crosses end
    }

    [Fact]
    public void SoftDelete_AfterAppend_AppliesToAppendedRows()
    {
        // Append rows, then soft-delete some of the newly appended
        // ones. The new rows are addressable by their post-append
        // logical indices.
        string path = WriteSequentialRows("delete_after_append.datum", 50);

        using (DatumFileWriterV2 appender = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null))
        {
            Pool pool = CreatePool();
            ColumnLookup lookup = new(["v"]);
            Arena arena = new();
            RowBatch batch = pool.RentRowBatch(lookup, capacity: 50, arena: arena);
            for (int i = 50; i < 100; i++)
            {
                DataValue[] row = pool.RentDataValues(1);
                row[0] = DataValue.FromInt32(i);
                batch.Add(row);
            }
            appender.WriteRowBatch(batch);
            appender.FinalizeWriter();
        }

        DatumFileWriterV2.SoftDeleteRows(path, [55L, 75L, 99L]);

        int[] live = ScanAllRows(path);
        Assert.Equal(97, live.Length);
        Assert.DoesNotContain(55, live);
        Assert.DoesNotContain(75, live);
        Assert.DoesNotContain(99, live);
        Assert.Contains(0, live);
        Assert.Contains(50, live);
        Assert.Contains(98, live);
    }

    [Fact]
    public void SoftDelete_AppendAfterDelete_PreservesExistingTombstones()
    {
        // After a soft-delete, append more rows. The tombstoned rows
        // from the prior commit must remain hidden — the new prologue
        // carries forward the existing chapter tombstone offsets.
        string path = WriteSequentialRows("delete_then_append.datum", 50);
        DatumFileWriterV2.SoftDeleteRows(path, [10L, 20L]);

        using (DatumFileWriterV2 appender = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null))
        {
            Pool pool = CreatePool();
            ColumnLookup lookup = new(["v"]);
            Arena arena = new();
            RowBatch batch = pool.RentRowBatch(lookup, capacity: 10, arena: arena);
            for (int i = 50; i < 60; i++)
            {
                DataValue[] row = pool.RentDataValues(1);
                row[0] = DataValue.FromInt32(i);
                batch.Add(row);
            }
            appender.WriteRowBatch(batch);
            appender.FinalizeWriter();
        }

        int[] live = ScanAllRows(path);
        Assert.Equal(58, live.Length); // 60 total - 2 deleted
        Assert.DoesNotContain(10, live);
        Assert.DoesNotContain(20, live);
        Assert.Contains(50, live);
        Assert.Contains(59, live);

        // HasTombstones must remain set after the post-delete append.
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.True((reader.Header.Flags & DatumFileFlagsV2.HasTombstones) != 0,
            "HasTombstones should be sticky across appends — old tombstone refs survive");
    }

    [Fact]
    public void SoftDelete_HeaderTotalRowCount_Unchanged()
    {
        // Soft-delete is logical, not physical. The file's
        // TotalRowCount header field continues to count tombstoned
        // rows — they're hidden at materialization but still occupy
        // their original logical positions on disk.
        string path = WriteSequentialRows("delete_total_count.datum", 100);

        DatumFileWriterV2.SoftDeleteRows(path, [10L, 20L, 30L]);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(100L, reader.TotalRowCount);
    }

    [Fact]
    public void SoftDelete_WholePageTombstoned_YieldsNoBatchForThatPage()
    {
        // Write enough rows to span 2 pages with a small page size,
        // delete the entire first page, scan should see only page 2's
        // rows and skip the empty batch entirely.
        string path = Path.Combine(_tempDir, "delete_whole_page.datum");
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["v"]);
        Arena arena = new();
        const int rowCount = 64;
        const int pageSize = 32;
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

        // Delete every row in page 0 (rows 0..31).
        long[] toDelete = Enumerable.Range(0, pageSize).Select(i => (long)i).ToArray();
        DatumFileWriterV2.SoftDeleteRows(path, toDelete);

        int[] live = ScanAllRows(path);
        Assert.Equal(32, live.Length);
        for (int i = 0; i < 32; i++) Assert.Contains(i + 32, live);
        for (int i = 0; i < 32; i++) Assert.DoesNotContain(i, live);
    }

    [Fact]
    public void SoftDelete_ChapterTombstoneBlock_IsCopyOnWrite()
    {
        // Each soft-delete commit writes a fresh 8 KiB block at a new
        // file offset; the OLD block stays referenced by any reader
        // that captured the prior footer. We test by capturing the
        // tombstone offset across two commits and verifying it changed.
        string path = WriteSequentialRows("delete_cow.datum", 100);

        DatumFileWriterV2.SoftDeleteRows(path, [10L]);
        long offset1;
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(path))
        {
            offset1 = r.Footer.Prologue.ChapterTombstoneOffsets[0];
            Assert.NotEqual(DatumFormatV2.NoTombstoneBlock, offset1);
        }

        DatumFileWriterV2.SoftDeleteRows(path, [20L]);
        long offset2;
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(path))
        {
            offset2 = r.Footer.Prologue.ChapterTombstoneOffsets[0];
        }

        Assert.NotEqual(offset1, offset2);
        Assert.True(offset2 > offset1,
            "second commit's tombstone block should land at a higher (later) file offset");
    }

    // ──────────────────── Helpers ────────────────────

    private string WriteSequentialRows(string fileName, int rowCount)
    {
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = Path.Combine(_tempDir, fileName);

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

        using DatumFileWriterV2 writer = new(path, sidecarPath: null);
        writer.Initialize([col]);
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();
        return path;
    }

    /// <summary>
    /// Scans every page of a file via the table provider, materializing
    /// only the live (non-tombstoned) rows. Returns the values of the
    /// single Int32 "v" column in scan order.
    /// </summary>
    private static int[] ScanAllRows(string path)
    {
        TableDescriptor descriptor = new("t", path);
        using DatumFileTableProviderV2 provider = new(descriptor, new Pool(new PoolBacking()));

        List<int> values = new();
        IAsyncEnumerable<RowBatch> scan = provider.ScanAsync(
            requiredColumns: null,
            filterHint: null,
            targetArena: null,
            cancellationToken: default);

        IAsyncEnumerator<RowBatch> e = scan.GetAsyncEnumerator();
        try
        {
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                RowBatch batch = e.Current;
                for (int i = 0; i < batch.Count; i++)
                {
                    values.Add(batch[i][0].AsInt32());
                }
            }
        }
        finally
        {
            e.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return values.ToArray();
    }
}
