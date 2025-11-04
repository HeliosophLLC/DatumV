using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Unit tests for <see cref="SpillReaderWriter"/>. Each test exercises the round-trip
/// (write batches → dispose write side → replay) and confirms that arena-backed payloads
/// survive the disk hop via the consolidated arena. Tests intentionally rent input batches
/// with distinct per-batch arenas to verify cross-arena consolidation works.
/// </summary>
public sealed class SpillReaderWriterTests : ServiceTestBase
{
    /// <summary>
    /// Rents a fresh batch with its own arena and appends the rows. Each call produces a
    /// batch whose <see cref="RowBatch.Arena"/> is distinct from any previous batch's arena.
    /// </summary>
    private static RowBatch MakeBatch(Pool pool, ColumnLookup lookup, params DataValue[][] rows)
    {
        RowBatch batch = pool.RentRowBatch(lookup, capacity: rows.Length);
        foreach (DataValue[] row in rows)
        {
            batch.Add(row);
        }
        return batch;
    }

    /// <summary>
    /// Consumes the spiller's replay, materialising every row's values into independent
    /// snapshots so the assertions can be made after the replay enumerator (and any
    /// pool-backed buffers) have been released.
    /// </summary>
    private static async Task<List<DataValue[]>> ReplayToList(
        SpillReaderWriter spiller, ExecutionContext context, ColumnLookup outputLookup)
    {
        List<DataValue[]> rows = [];
        await foreach (RowBatch batch in spiller.ReplayAsync(context, outputLookup))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                DataValue[] copy = new DataValue[row.FieldCount];
                for (int j = 0; j < row.FieldCount; j++)
                {
                    copy[j] = row[j];
                }
                rows.Add(copy);
            }
            context.Pool.ReturnRowBatch(batch);
        }
        return rows;
    }

    [Fact]
    public async Task Roundtrip_SingleBatch_InlineValuesOnly_PreservesAllValues()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["i", "f", "b"]);
        ExecutionContext context = CreateExecutionContext();

        RowBatch input = MakeBatch(pool, lookup,
            [DataValue.FromInt32(1), DataValue.FromFloat32(1.5f), DataValue.FromBoolean(true)],
            [DataValue.FromInt32(2), DataValue.FromFloat32(2.5f), DataValue.FromBoolean(false)],
            [DataValue.FromInt32(3), DataValue.FromFloat32(3.5f), DataValue.FromBoolean(true)]);

        using SpillReaderWriter spiller = new(pool, lookup);
        spiller.Write(input);

        List<DataValue[]> replayed = await ReplayToList(spiller, context, lookup);

        Assert.Equal(3, replayed.Count);
        Assert.Equal(1, replayed[0][0].AsInt32());
        Assert.Equal(1.5f, replayed[0][1].AsFloat32());
        Assert.True(replayed[0][2].AsBoolean());
        Assert.Equal(2, replayed[1][0].AsInt32());
        Assert.False(replayed[1][2].AsBoolean());
        Assert.Equal(3, replayed[2][0].AsInt32());
        Assert.True(replayed[2][2].AsBoolean());
    }

    [Fact]
    public async Task Roundtrip_MultipleBatches_DistinctArenas_AllInline_PreservesOrder()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["n"]);
        ExecutionContext context = CreateExecutionContext();

        RowBatch b1 = MakeBatch(pool, lookup,
            [DataValue.FromInt32(10)], [DataValue.FromInt32(11)]);
        RowBatch b2 = MakeBatch(pool, lookup,
            [DataValue.FromInt32(20)]);
        RowBatch b3 = MakeBatch(pool, lookup,
            [DataValue.FromInt32(30)], [DataValue.FromInt32(31)], [DataValue.FromInt32(32)]);

        // Each rented batch has its own arena — confirm they're distinct.
        Assert.NotSame(b1.Arena, b2.Arena);
        Assert.NotSame(b2.Arena, b3.Arena);

        using SpillReaderWriter spiller = new(pool, lookup);
        spiller.Write(b1);
        spiller.Write(b2);
        spiller.Write(b3);

        List<DataValue[]> replayed = await ReplayToList(spiller, context, lookup);

        Assert.Equal(new[] { 10, 11, 20, 30, 31, 32 }, replayed.Select(r => r[0].AsInt32()));
    }

    [Fact]
    public async Task Roundtrip_ArenaBackedStrings_AcrossMultipleBatches_PreservesPayloads()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["s"]);
        ExecutionContext context = CreateExecutionContext();

        // Strings > 16 UTF-8 bytes are arena-backed; offsets land in each batch's own arena.
        const string longA = "this string is definitely longer than sixteen bytes A";
        const string longB = "another long string that exceeds the inline threshold B";
        const string longC = "third arena-backed payload, distinct content again C";

        RowBatch b1 = pool.RentRowBatch(lookup, capacity: 2);
        b1.Add([DataValue.FromString(longA, b1.Arena)]);
        b1.Add([DataValue.FromString(longB, b1.Arena)]);

        RowBatch b2 = pool.RentRowBatch(lookup, capacity: 1);
        b2.Add([DataValue.FromString(longC, b2.Arena)]);

        Assert.NotSame(b1.Arena, b2.Arena);

        using SpillReaderWriter spiller = new(pool, lookup);
        Arena consolidated = spiller.ConsolidatedArena;
        spiller.Write(b1);
        spiller.Write(b2);

        List<DataValue[]> replayed = await ReplayToList(spiller, context, lookup);

        Assert.Equal(3, replayed.Count);
        // After the writer returned the input batches, only the consolidated arena holds the
        // payload bytes — resolving against it must yield the original strings intact.
        Assert.Equal(longA, replayed[0][0].AsString(consolidated));
        Assert.Equal(longB, replayed[1][0].AsString(consolidated));
        Assert.Equal(longC, replayed[2][0].AsString(consolidated));
    }

    [Fact]
    public async Task Roundtrip_MixedKinds_AndNullValues_PreservesAll()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["id", "name", "amount", "flag"]);
        ExecutionContext context = CreateExecutionContext();

        RowBatch input = pool.RentRowBatch(lookup, capacity: 2);
        input.Add([
            DataValue.FromInt32(1),
            DataValue.FromString("a much longer name than fits inline", input.Arena),
            DataValue.FromFloat64(3.14),
            DataValue.FromBoolean(true),
        ]);
        input.Add([
            DataValue.FromInt32(2),
            DataValue.Null(DataKind.String),
            DataValue.Null(DataKind.Float64),
            DataValue.FromBoolean(false),
        ]);

        using SpillReaderWriter spiller = new(pool, lookup);
        Arena consolidated = spiller.ConsolidatedArena;
        spiller.Write(input);

        List<DataValue[]> replayed = await ReplayToList(spiller, context, lookup);

        Assert.Equal(2, replayed.Count);
        Assert.Equal(1, replayed[0][0].AsInt32());
        Assert.Equal("a much longer name than fits inline", replayed[0][1].AsString(consolidated));
        Assert.Equal(3.14, replayed[0][2].AsFloat64());
        Assert.True(replayed[0][3].AsBoolean());

        Assert.Equal(2, replayed[1][0].AsInt32());
        Assert.True(replayed[1][1].IsNull);
        Assert.Equal(DataKind.String, replayed[1][1].Kind);
        Assert.True(replayed[1][2].IsNull);
        Assert.Equal(DataKind.Float64, replayed[1][2].Kind);
        Assert.False(replayed[1][3].AsBoolean());
    }

    [Fact]
    public void Write_ReleasesInputBatchArena()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["x"]);

        RowBatch input = pool.RentRowBatch(lookup, capacity: 1);
        Arena inputArena = input.Arena;
        Assert.Equal(1, inputArena.ReferenceCount);
        input.Add([DataValue.FromInt32(42)]);

        using SpillReaderWriter spiller = new(pool, lookup);
        spiller.Write(input);

        // Spiller returned the batch immediately after writing, so its per-batch arena's
        // reference is back to zero (or the arena has been re-pooled). Either way,
        // the spiller does not retain a reference to the per-batch arena.
        Assert.Equal(0, inputArena.ReferenceCount);
    }

    [Fact]
    public async Task ReplayBatch_SharesConsolidatedArena()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["s"]);
        ExecutionContext context = CreateExecutionContext();

        RowBatch input = pool.RentRowBatch(lookup, capacity: 1);
        input.Add([DataValue.FromString("a long arena-backed payload", input.Arena)]);

        using SpillReaderWriter spiller = new(pool, lookup);
        spiller.Write(input);

        await foreach (RowBatch replay in spiller.ReplayAsync(context, lookup))
        {
            Assert.Same(spiller.ConsolidatedArena, replay.Arena);
            pool.ReturnRowBatch(replay);
        }
    }

    [Fact]
    public async Task ConsolidatedArena_RefcountIncreasesPerReplayBatch()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["x"]);
        ExecutionContext context = CreateExecutionContext();

        // Force several output batches by writing many rows under a small batch size.
        RowBatch input = pool.RentRowBatch(lookup, capacity: 5);
        for (int i = 0; i < 5; i++)
        {
            input.Add([DataValue.FromInt32(i)]);
        }

        using SpillReaderWriter spiller = new(pool, lookup);
        spiller.Write(input);

        Arena consolidated = spiller.ConsolidatedArena;
        int spillerOnly = consolidated.ReferenceCount;

        // Create a context with a tiny batch size so replay yields multiple batches.
        ExecutionContext smallBatchContext = CreateExecutionContext(batchSize: 2);

        await foreach (RowBatch replay in spiller.ReplayAsync(smallBatchContext, lookup))
        {
            // While the batch is alive its rent of the arena bumps the count above the
            // spiller's baseline.
            Assert.True(consolidated.ReferenceCount > spillerOnly);
            pool.ReturnRowBatch(replay);
        }

        // After all replay batches are returned the count is back to the spiller's hold.
        Assert.Equal(spillerOnly, consolidated.ReferenceCount);
    }

    [Fact]
    public async Task Replay_ConsumerBreaksMidIteration_DoesNotDoubleReturn()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["x"]);
        // Small batch size so replay yields multiple batches; we'll abandon iteration
        // after the first one to exercise the iterator-dispose-mid-yield path.
        ExecutionContext context = CreateExecutionContext(batchSize: 2);

        RowBatch input = pool.RentRowBatch(lookup, capacity: 6);
        for (int i = 0; i < 6; i++)
        {
            input.Add([DataValue.FromInt32(i)]);
        }

        using SpillReaderWriter spiller = new(pool, lookup);
        spiller.Write(input);

        Arena consolidated = spiller.ConsolidatedArena;
        int spillerOnly = consolidated.ReferenceCount;

        // The bug being locked in: nulling outputBatch *after* yield means a consumer
        // that takes ownership (returns the batch) then breaks out leaves the producer's
        // local still pointing at the now-disposed batch. The producer's finally would
        // then double-return it and throw ObjectDisposedException. Fixed by transferring
        // ownership *before* the yield.
        int batchesReceived = 0;
        await foreach (RowBatch replay in spiller.ReplayAsync(context, lookup))
        {
            batchesReceived++;
            // Take ownership: return the batch, exactly like a real consumer would.
            pool.ReturnRowBatch(replay);
            // Then abandon iteration. The await foreach's hidden DisposeAsync runs the
            // producer's finally; it must not double-return the batch we just disposed.
            break;
        }

        Assert.Equal(1, batchesReceived);
        // Consumer's batch released its arena ref via ReturnRowBatch; producer's finally
        // didn't touch the arena because outputBatch was null by then. We're back to the
        // spiller's own reference.
        Assert.Equal(spillerOnly, consolidated.ReferenceCount);
    }

    [Fact]
    public void Dispose_DeletesSpillDirectory()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["x"]);

        RowBatch input = pool.RentRowBatch(lookup, capacity: 1);
        input.Add([DataValue.FromInt32(1)]);

        SpillReaderWriter spiller = new(pool, lookup);
        spiller.Write(input);

        string spillFile = spiller.SpillFilePath;
        string? spillDir = Path.GetDirectoryName(spillFile);
        Assert.True(File.Exists(spillFile));
        Assert.True(Directory.Exists(spillDir));

        spiller.Dispose();

        Assert.False(File.Exists(spillFile));
        Assert.False(Directory.Exists(spillDir));
    }

    [Fact]
    public async Task EmptyBatch_ReturnedWithoutOpeningFile()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["x"]);
        ExecutionContext context = CreateExecutionContext();

        RowBatch empty = pool.RentRowBatch(lookup, capacity: 1);

        using SpillReaderWriter spiller = new(pool, lookup);
        spiller.Write(empty);

        // No file was opened: replay yields nothing.
        Assert.False(File.Exists(spiller.SpillFilePath));

        List<DataValue[]> replayed = await ReplayToList(spiller, context, lookup);
        Assert.Empty(replayed);
    }

    [Fact]
    public async Task Replay_BatchesAndPreservesAllRows()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup lookup = new(["x"]);
        // ArrayPool<Row>.Shared.Rent rounds capacity up to a pool bucket size, so the
        // effective batch capacity may exceed context.BatchSize. We assert on the contract
        // that matters: every row replays in order, and the run is split into >1 batches.
        ExecutionContext context = CreateExecutionContext(batchSize: 64);

        RowBatch input = pool.RentRowBatch(lookup, capacity: 300);
        for (int i = 0; i < 300; i++)
        {
            input.Add([DataValue.FromInt32(i)]);
        }

        using SpillReaderWriter spiller = new(pool, lookup);
        spiller.Write(input);

        List<int> sizes = [];
        List<int> values = [];
        await foreach (RowBatch batch in spiller.ReplayAsync(context, lookup))
        {
            sizes.Add(batch.Count);
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0].AsInt32());
            }
            pool.ReturnRowBatch(batch);
        }

        Assert.Equal(300, values.Count);
        Assert.Equal(Enumerable.Range(0, 300), values);
        Assert.True(sizes.Count > 1, "Replay should produce more than one batch when row count exceeds batch size.");
    }

    [Fact]
    public async Task Roundtrip_RenamedOutputLookup_ApplyCorrectColumnNames()
    {
        Pool pool = GetService<Pool>();
        ColumnLookup writeLookup = new(["original_a", "original_b"]);
        ColumnLookup readLookup = new(["renamed_a", "renamed_b"]);
        ExecutionContext context = CreateExecutionContext();

        RowBatch input = MakeBatch(pool, writeLookup,
            [DataValue.FromInt32(1), DataValue.FromInt32(2)]);

        using SpillReaderWriter spiller = new(pool, writeLookup);
        spiller.Write(input);

        await foreach (RowBatch batch in spiller.ReplayAsync(context, readLookup))
        {
            Assert.Equal("renamed_a", batch.ColumnLookup.ColumnNames[0]);
            Assert.Equal("renamed_b", batch.ColumnLookup.ColumnNames[1]);
            Assert.Equal(1, batch[0][0].AsInt32());
            Assert.Equal(2, batch[0][1].AsInt32());
            pool.ReturnRowBatch(batch);
        }
    }
}
