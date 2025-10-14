using DatumIngest.Pooling;
using DatumIngest.Ingestion;
using DatumIngest.Model;

namespace DatumIngest.Tests.Ingestion;

public sealed class SchemaDetectorTests
{
    // ───────────────────────── Helpers ─────────────────────────

    private static RowBatch MakeBatch(Pool pool, IReadOnlyList<string> names, params DataValue[][] rows)
    {
        Dictionary<string, int> nameIndex = new(names.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            nameIndex[names[i]] = i;

        RowBatch batch = pool.RentRowBatch(1024);
        foreach (DataValue[] values in rows)
            batch.Add(new Row(names, values, nameIndex));
        return batch;
    }

    private static async IAsyncEnumerable<RowBatch> SingleBatch(RowBatch batch)
    {
        yield return batch;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<RowBatch> MultipleBatches(params RowBatch[] batches)
    {
        foreach (RowBatch batch in batches)
            yield return batch;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<RowBatch> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    // ───────────────────────── Tests ─────────────────────────

    [Fact]
    public async Task InfersTypesFromFirstBatch()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        string[] names = ["id", "score"];

        RowBatch batch = MakeBatch(pool, names,
            [DataValue.FromInt32(1), DataValue.FromFloat64(1.5)],
            [DataValue.FromInt32(2), DataValue.FromFloat64(2.7)]);

        SchemaDetector detector = new();
        int batchCount = 0;

        await foreach (RowBatch b in detector.DetectAndPassthrough(SingleBatch(batch)))
        {
            batchCount++;
            Assert.Equal(2, b.Count);
        }

        Assert.Equal(1, batchCount);
        Assert.Equal(2, detector.Schema.Columns.Count);
        Assert.Equal("id", detector.Schema.Columns[0].Name);
        Assert.Equal(DataKind.Int32, detector.Schema.Columns[0].Kind);
        Assert.Equal("score", detector.Schema.Columns[1].Name);
        Assert.Equal(DataKind.Float64, detector.Schema.Columns[1].Kind);
    }

    [Fact]
    public async Task HandlesNullInFirstRow()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        string[] names = ["x", "y"];

        RowBatch batch = MakeBatch(pool, names,
            [DataValue.FromInt32(1), DataValue.Null(DataKind.Unknown)],
            [DataValue.FromInt32(2), DataValue.FromFloat64(3.14)]);

        SchemaDetector detector = new();
        await foreach (RowBatch _ in detector.DetectAndPassthrough(SingleBatch(batch))) { }

        Assert.Equal(DataKind.Float64, detector.Schema.Columns[1].Kind);
    }

    [Fact]
    public async Task PassesThroughAllBatches()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        string[] names = ["v"];

        RowBatch b1 = MakeBatch(pool, names, [DataValue.FromInt32(1)]);
        RowBatch b2 = MakeBatch(pool, names, [DataValue.FromInt32(2)]);
        RowBatch b3 = MakeBatch(pool, names, [DataValue.FromInt32(3)]);

        SchemaDetector detector = new();
        List<int> values = [];

        await foreach (RowBatch b in detector.DetectAndPassthrough(MultipleBatches(b1, b2, b3)))
        {
            for (int i = 0; i < b.Count; i++)
                values.Add(b[i][0].AsInt32());
        }

        Assert.Equal([1, 2, 3], values);
        Assert.Equal("v", detector.Schema.Columns[0].Name);
    }

    [Fact]
    public async Task EmptyStream_SchemaNotAvailable()
    {
        SchemaDetector detector = new();
        await foreach (RowBatch _ in detector.DetectAndPassthrough(EmptyStream())) { }

        Assert.Throws<InvalidOperationException>(() => _ = detector.Schema);
    }

    [Fact]
    public async Task AllColumnsNullable()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        string[] names = ["a", "b"];

        RowBatch batch = MakeBatch(pool, names,
            [DataValue.FromInt32(1), DataValue.FromBoolean(true)]);

        SchemaDetector detector = new();
        await foreach (RowBatch _ in detector.DetectAndPassthrough(SingleBatch(batch))) { }

        Assert.True(detector.Schema.Columns[0].Nullable);
        Assert.True(detector.Schema.Columns[1].Nullable);
    }
}
