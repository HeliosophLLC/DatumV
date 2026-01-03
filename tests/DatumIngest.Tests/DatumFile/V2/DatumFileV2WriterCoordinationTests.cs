using System.Buffers.Binary;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.DatumFile.V2;

/// <summary>
/// PR3 tests for writer coordination: WriterId stamping (initial +
/// append, default + custom), tail-CAS sanity check (verifies the
/// captured base tail is unchanged at commit time — meaningful CAS
/// retry trigger under future multi-writer; passive guard today),
/// FileShare changes (concurrent reader survives an in-flight writer
/// session, in-process two-writer rejection).
/// </summary>
public sealed class DatumFileV2WriterCoordinationTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_v4_pr3_{Guid.NewGuid():N}");

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
    public void WriterId_DefaultIsProcessStable()
    {
        // Two consecutive default ids in the same process are equal.
        ulong a = WriterIdentity.Default;
        ulong b = WriterIdentity.Default;
        Assert.Equal(a, b);
        Assert.NotEqual(WriterIdentity.Anonymous, a);
    }

    [Fact]
    public void WriterId_StampedOnInitialWrite()
    {
        string path = WriteSimpleFile("writerid_initial.datum");
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);

        Assert.Equal(WriterIdentity.Default, reader.Footer.Prologue.WriterId);
    }

    [Fact]
    public void WriterId_Custom_RoundTripsThroughInitialWrite()
    {
        string path = Path.Combine(_tempDir, "writerid_custom_initial.datum");
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        const ulong customId = 0xABCDEF0123456789UL;
        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["v"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 3, arena: arena);
        for (int i = 0; i < 3; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromInt32(i);
            batch.Add(row);
        }

        using (DatumFileWriterV2 writer = new(path, sidecarPath: null))
        {
            writer.WriterId = customId;
            writer.Initialize([col]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(customId, reader.Footer.Prologue.WriterId);
    }

    [Fact]
    public void WriterId_Custom_RoundTripsThroughAppend()
    {
        string path = Path.Combine(_tempDir, "writerid_custom_append.datum");
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        const ulong initialId = 0x1111111111111111UL;
        const ulong appendId = 0x2222222222222222UL;

        // Initial write with one writer id.
        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["v"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 3, arena: arena);
        for (int i = 0; i < 3; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromInt32(i);
            batch.Add(row);
        }
        using (DatumFileWriterV2 writer = new(path, sidecarPath: null))
        {
            writer.WriterId = initialId;
            writer.Initialize([col]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }

        // Append with a different writer id.
        using (DatumFileWriterV2 appender = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null))
        {
            appender.WriterId = appendId;
            Pool pool2 = new(new PoolBacking());
            Arena arena2 = new();
            RowBatch appBatch = pool2.RentRowBatch(lookup, capacity: 2, arena: arena2);
            for (int i = 3; i < 5; i++)
            {
                DataValue[] row = pool2.RentDataValues(1);
                row[0] = DataValue.FromInt32(i);
                appBatch.Add(row);
            }
            appender.WriteRowBatch(appBatch);
            appender.FinalizeWriter();
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        // The new prologue carries the appender's id; baseGeneration
        // points at the prior commit (which was made by initialId, but
        // we only see baseGeneration here — the writer chain is
        // implicit via the generation counter).
        Assert.Equal(appendId, reader.Footer.Prologue.WriterId);
        Assert.Equal(2UL, reader.Footer.Prologue.Generation);
        Assert.Equal(1UL, reader.Footer.Prologue.BaseGeneration);
    }

    [Fact]
    public void TailCas_FiresWhenBaseTailRewritten()
    {
        // OpenForAppend captures the base tail at open. Manually
        // rewrite the tail bytes between open and finalize (simulating
        // an out-of-band mutation that the FileShare lock would
        // normally prevent). Finalize should detect the mismatch and
        // throw, proving the protocol catches "someone else committed
        // during my session" — the multi-writer CAS retry trigger.
        string path = Path.Combine(_tempDir, "cas_fire.datum");
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        WriteSimpleFileTo(path, [col]);

        // OpenForAppend grabs the writer handle (FileShare.Read), so
        // we can't open another RW handle. To exercise the CAS check
        // we need to mutate the tail through the SAME handle the
        // writer holds. Cleanest: drop down to reflection-free access
        // by tampering with bytes via a second handle opened with
        // FileShare.ReadWrite — which the writer's FileShare.Read
        // does NOT allow, so this attempt will fail with
        // IOException. That's not a CAS test, that's the FileShare
        // exclusion test.
        //
        // The legitimate way to exercise CAS is to use reflection on
        // the writer's _baseTailBytes — flip a byte in the captured
        // snapshot so the on-disk bytes "don't match" what was
        // captured. This simulates the multi-writer scenario where
        // another writer's commit changed the tail since we opened.
        using DatumFileWriterV2 writer = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null);
        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["v"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        DataValue[] row = pool.RentDataValues(1);
        row[0] = DataValue.FromInt32(99);
        batch.Add(row);
        writer.WriteRowBatch(batch);

        // Tamper with the captured tail snapshot to simulate a
        // mismatch.
        System.Reflection.FieldInfo? field = typeof(DatumFileWriterV2)
            .GetField("_baseTailBytes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        byte[] captured = (byte[])field!.GetValue(writer)!;
        captured[0] ^= 0xFF;  // flip a byte so the on-disk tail no longer matches

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => writer.FinalizeWriter());
        Assert.Contains("Base tail mismatch", ex.Message);
    }

    [Fact]
    public void TailCas_PassesUnderNormalSingleWriter()
    {
        // Round-trip without any tampering — the CAS check must not
        // fire spuriously on the happy path.
        string path = Path.Combine(_tempDir, "cas_pass.datum");
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        WriteSimpleFileTo(path, [col]);

        using (DatumFileWriterV2 appender = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null))
        {
            Pool pool = new(new PoolBacking());
            ColumnLookup lookup = new(["v"]);
            Arena arena = new();
            RowBatch batch = pool.RentRowBatch(lookup, capacity: 5, arena: arena);
            for (int i = 0; i < 5; i++)
            {
                DataValue[] row = pool.RentDataValues(1);
                row[0] = DataValue.FromInt32(100 + i);
                batch.Add(row);
            }
            appender.WriteRowBatch(batch);
            appender.FinalizeWriter();  // no exception
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(8L, reader.TotalRowCount);  // 3 initial + 5 appended
    }

    [Fact]
    public void Reader_OpensWhileWriterIsActive()
    {
        // PR3 FileShare changes: writer holds with FileShare.Read,
        // reader opens with FileShare.ReadWrite. A reader can open the
        // file while a writer is mid-session (between OpenForAppend
        // and FinalizeWriter) and see the LAST COMMITTED state — the
        // writer's uncommitted bytes past the old tail are invisible
        // because the reader uses tail-flip-as-commit semantics.
        string path = Path.Combine(_tempDir, "concurrent_reader.datum");
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        WriteSimpleFileTo(path, [col]);  // initial 3 rows committed

        using DatumFileWriterV2 appender = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null);
        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["v"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 5, arena: arena);
        for (int i = 0; i < 5; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromInt32(50 + i);
            batch.Add(row);
        }
        appender.WriteRowBatch(batch);
        // NOTE: appender has NOT called FinalizeWriter yet.

        // Reader opens concurrently. Sees the committed state (3 rows)
        // — the appender's pages are written past EOF but the tail
        // hasn't been flipped to point at them, so the reader's
        // tail-first parse lands on the original footer.
        using (DatumFileReaderV2 reader = DatumFileReaderV2.Open(path))
        {
            Assert.Equal(3L, reader.TotalRowCount);
            Assert.Equal(1UL, reader.Footer.Prologue.Generation);
        }

        // Now finalize and check post-commit state.
        appender.FinalizeWriter();

        using (DatumFileReaderV2 reader = DatumFileReaderV2.Open(path))
        {
            Assert.Equal(8L, reader.TotalRowCount);
            Assert.Equal(2UL, reader.Footer.Prologue.Generation);
        }
    }

    [Fact]
    public void TwoWriters_SecondOpenIsRejected()
    {
        // PR3 FileShare changes: writer holds with FileShare.Read.
        // Another writer trying to open RW (initial-write OR
        // OpenForAppend) gets denied by the OS file-share rules. This
        // is the writer-lock-via-FileShare convention — no separate
        // lock primitive needed.
        string path = Path.Combine(_tempDir, "two_writers.datum");
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        WriteSimpleFileTo(path, [col]);

        using DatumFileWriterV2 first = DatumFileWriterV2.OpenForAppend(path, sidecarPath: null);

        // Second OpenForAppend on the same path must fail because the
        // first writer holds the file with FileShare.Read (no
        // FileShare.Write allowed).
        Assert.Throws<IOException>(() => DatumFileWriterV2.OpenForAppend(path, sidecarPath: null));
    }

    // ──────────────────── Helpers ────────────────────

    private string WriteSimpleFile(string fileName)
    {
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        string path = Path.Combine(_tempDir, fileName);
        WriteSimpleFileTo(path, [col]);
        return path;
    }

    private static void WriteSimpleFileTo(string path, ColumnDescriptorV2[] columns)
    {
        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new([columns[0].Name]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 3, arena: arena);
        for (int i = 0; i < 3; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromInt32(i);
            batch.Add(row);
        }
        using DatumFileWriterV2 writer = new(path, sidecarPath: null);
        writer.Initialize(columns);
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();
    }
}
