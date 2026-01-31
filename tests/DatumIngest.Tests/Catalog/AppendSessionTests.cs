using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR9 tests for streaming append sessions
/// (<see cref="IAppendSession"/>). Cover commit / abort
/// semantics, schema validation, single-session-at-a-time
/// serialization, and torn-tail recovery on the reader side
/// (so a crashed-mid-session writer doesn't block reads).
/// </summary>
public sealed class AppendSessionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr9_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        return Task.CompletedTask;
    }

    // ──────────────────── In-memory ────────────────────

    [Fact]
    public async Task InMemory_Session_CommitMakesRowsVisible()
    {
        Pool pool = CreatePool();
        InMemoryTableProvider provider = new(pool, "t",
            columns: ["a"], rows: [[1]]);

        await using IAppendSession s = provider.BeginAppend();
        await s.WriteAsync(MakeBatch(pool, ["a"], [[2], [3]]));
        // Pre-commit: row count is unchanged.
        Assert.Equal(1, provider.GetRowCount());
        await s.CommitAsync();

        Assert.Equal(3, provider.GetRowCount());
    }

    [Fact]
    public async Task InMemory_Session_DisposeWithoutCommitAborts()
    {
        Pool pool = CreatePool();
        InMemoryTableProvider provider = new(pool, "t",
            columns: ["a"], rows: [[1]]);

        await using (IAppendSession s = provider.BeginAppend())
        {
            await s.WriteAsync(MakeBatch(pool, ["a"], [[42]]));
            // No CommitAsync — abort.
        }

        Assert.Equal(1, provider.GetRowCount());
    }

    [Fact]
    public async Task InMemory_Session_WriteAfterCommitThrows()
    {
        Pool pool = CreatePool();
        InMemoryTableProvider provider = new(pool, "t",
            columns: ["a"], rows: []);

        await using IAppendSession s = provider.BeginAppend();
        await s.WriteAsync(MakeBatch(pool, ["a"], [[1]]));
        await s.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await s.WriteAsync(MakeBatch(pool, ["a"], [[2]])));
    }

    [Fact]
    public async Task InMemory_Session_DoubleCommitThrows()
    {
        Pool pool = CreatePool();
        InMemoryTableProvider provider = new(pool, "t",
            columns: ["a"], rows: []);

        await using IAppendSession s = provider.BeginAppend();
        await s.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await s.CommitAsync());
    }

    [Fact]
    public async Task InMemory_Session_SchemaMismatchOnWriteThrows()
    {
        Pool pool = CreatePool();
        InMemoryTableProvider provider = new(pool, "t",
            columns: ["a", "b"], rows: []);

        await using IAppendSession s = provider.BeginAppend();
        // Wrong column count.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await s.WriteAsync(MakeBatch(pool, ["a"], [[1]])));
        // Wrong column name.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await s.WriteAsync(MakeBatch(pool, ["a", "wrong"], [[1, 2]])));
    }

    [Fact]
    public async Task InMemory_Session_SecondBeginAppendBlocksUntilFirstReleases()
    {
        Pool pool = CreatePool();
        InMemoryTableProvider provider = new(pool, "t",
            columns: ["a"], rows: []);

        IAppendSession s1 = provider.BeginAppend();

        Task<IAppendSession> secondBegin = Task.Run(() => provider.BeginAppend());
        // The second BeginAppend is blocked on the semaphore.
        Task firstWait = Task.Delay(50);
        Task winner = await Task.WhenAny(secondBegin, firstWait);
        Assert.Same(firstWait, winner);

        await s1.DisposeAsync();
        IAppendSession s2 = await secondBegin;
        await s2.DisposeAsync();
    }

    // ──────────────────── Datum file ────────────────────

    [Fact]
    public async Task Datum_Session_CommitWritesRowsAndAbortsKeepsOldFooter()
    {
        string path = WriteSimpleDatumFile("session_commit.datum");
        Pool pool = CreatePool();
        using DatumFileTableProviderV2 provider = new(new TableDescriptor("t", path), pool);

        long before = provider.GetRowCount();

        await using (IAppendSession s = provider.BeginAppend())
        {
            await s.WriteAsync(MakeBatch(pool, ["a", "b"], [[100, 1000]]));
            await s.CommitAsync();
        }
        Assert.Equal(before + 1, provider.GetRowCount());

        // Now open another session, write, then ABORT (no commit).
        await using (IAppendSession s = provider.BeginAppend())
        {
            await s.WriteAsync(MakeBatch(pool, ["a", "b"], [[999, 9990]]));
            // Disposed without CommitAsync.
        }
        Assert.Equal(before + 1, provider.GetRowCount()); // unchanged
    }

    [Fact]
    public async Task Datum_Session_AppendRowsAsyncWrapperRoutesThroughBeginAppend()
    {
        // The default-impl AppendRowsAsync on ITableProvider wraps
        // BeginAppend. After PR9 the providers no longer override
        // AppendRowsAsync, so this verifies the wrapper still works
        // end-to-end.
        string path = WriteSimpleDatumFile("session_wrapper.datum");
        Pool pool = CreatePool();
        using DatumFileTableProviderV2 provider = new(new TableDescriptor("t", path), pool);

        long before = provider.GetRowCount();
        RowBatch batch = MakeBatch(pool, ["a", "b"], [[7, 70], [8, 80]]);
        // AppendRowsAsync is a default-interface implementation since
        // PR9 — call it through the interface.
        await ((ITableProvider)provider).AppendRowsAsync(AsyncOf(batch), CancellationToken.None);

        Assert.Equal(before + 2, provider.GetRowCount());
    }

    [Fact]
    public async Task Datum_Session_AbortLeavesFileReadableAcrossReopen()
    {
        // Crash simulation: open a session, write rows, dispose
        // without CommitAsync. The file now has bytes past the
        // committed tail with no new tail. A fresh
        // DatumFileReaderV2.Open must succeed (using torn-tail
        // recovery to find the previous good tail) and surface the
        // pre-session state.
        string path = WriteSimpleDatumFile("session_abort_recovery.datum");

        long preSessionLength = new FileInfo(path).Length;
        long preSessionRowCount;
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(path))
        {
            preSessionRowCount = r.TotalRowCount;
        }

        Pool pool = CreatePool();
        using (DatumFileTableProviderV2 provider = new(new TableDescriptor("t", path), pool))
        {
            await using IAppendSession s = provider.BeginAppend();
            await s.WriteAsync(MakeBatch(pool, ["a", "b"], [[100, 1000], [200, 2000]]));
            // No commit — DisposeAsync aborts.
        }

        // The file's physical length grew (writer flushed page bytes)
        // but the previous tail is still authoritative.
        Assert.True(new FileInfo(path).Length >= preSessionLength,
            "expected physical length to be at least pre-session length");

        // Reopen with a fresh reader — must succeed via torn-tail recovery.
        using DatumFileReaderV2 reopened = DatumFileReaderV2.Open(path);
        Assert.Equal(preSessionRowCount, reopened.TotalRowCount);
    }

    [Fact]
    public void Datum_Reader_TornFile_ScansBackToLastGoodTail()
    {
        // Direct test: take a clean .datum file, append garbage past
        // the tail, and verify the reader still opens it (using
        // recovery) and reports the original state.
        string path = WriteSimpleDatumFile("torn_reader.datum");
        long preSessionLength = new FileInfo(path).Length;

        long originalRowCount;
        using (DatumFileReaderV2 r = DatumFileReaderV2.Open(path))
        {
            originalRowCount = r.TotalRowCount;
        }

        // Simulate torn write: append a few KiB of arbitrary bytes
        // without writing a new tail. Must include enough bytes that
        // the reader's last-8 check fails (the appended bytes happen
        // not to end in FMTD).
        byte[] garbage = new byte[8192];
        new Random(42).NextBytes(garbage);
        // Defensively zero the trailing 4 bytes so they can't form FMTD.
        garbage[^4] = 0; garbage[^3] = 0; garbage[^2] = 0; garbage[^1] = 0;
        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Position = preSessionLength;
            fs.Write(garbage);
        }

        Assert.True(new FileInfo(path).Length > preSessionLength);

        // Reader must succeed via recovery and surface the pre-torn state.
        using DatumFileReaderV2 reopened = DatumFileReaderV2.Open(path);
        Assert.Equal(originalRowCount, reopened.TotalRowCount);

        // Subsequent writer reopen physically truncates the garbage.
        using (DatumFileWriterV2 _ = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null))
        {
            // Just open + dispose; OpenForAppend's RecoverIfTorn truncated.
        }
        Assert.Equal(preSessionLength, new FileInfo(path).Length);
    }

    [Fact]
    public async Task Datum_Session_CatalogBeginAppendDispatches()
    {
        string path = WriteSimpleDatumFile("catalog_session.datum");
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.AddFile(path, name: "t");

        long before = catalog["t"].GetRowCount();
        await using (IAppendSession s = catalog.BeginAppend("t"))
        {
            await s.WriteAsync(MakeBatch(pool, ["a", "b"], [[55, 550]]));
            await s.CommitAsync();
        }
        Assert.Equal(before + 1, catalog["t"].GetRowCount());
    }

    [Fact]
    public void Datum_Session_OnReadOnlyTable_ThrowsInvalidOperation()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        Assert.Throws<InvalidOperationException>(() =>
            catalog.BeginAppend("information_schema.tables"));
    }

    // ──────────────────── Helpers ────────────────────

    private static RowBatch MakeBatch(Pool pool, string[] columns, int[][] rows)
    {
        ColumnLookup lookup = new(columns);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: rows.Length, arena: arena);
        foreach (int[] row in rows)
        {
            DataValue[] values = pool.RentDataValues(columns.Length);
            for (int c = 0; c < columns.Length; c++)
            {
                values[c] = DataValue.FromInt32(row[c]);
            }
            batch.Add(values);
        }
        return batch;
    }

    private static async IAsyncEnumerable<RowBatch> AsyncOf(params RowBatch[] batches)
    {
        foreach (RowBatch b in batches) yield return b;
        await Task.CompletedTask;
    }

    private string WriteSimpleDatumFile(string fileName)
    {
        ColumnDescriptorV2 colA = new("a", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 colB = new("b", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["a", "b"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 3, arena: arena);
        for (int i = 0; i < 3; i++)
        {
            DataValue[] row = pool.RentDataValues(2);
            row[0] = DataValue.FromInt32(i);
            row[1] = DataValue.FromInt32(i * 10);
            batch.Add(row);
        }

        string path = Path.Combine(_tempDir, fileName);
        using (DatumFileWriterV2 writer = new(path, sidecarPath: null))
        {
            writer.Initialize([colA, colB]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }
        return path;
    }
}
