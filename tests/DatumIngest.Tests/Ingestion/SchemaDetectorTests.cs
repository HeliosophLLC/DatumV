using DatumIngest.Pooling;
using DatumIngest.Ingestion;
using DatumIngest.Model;

namespace DatumIngest.Tests.Ingestion;

public sealed class SchemaDetectorTests
{
    private static RowBatch MakeBatch(Pool pool, IReadOnlyList<string> names, params DataValue[][] rows)
    {
        ColumnLookup columnLookup = new(names);

        RowBatch batch = pool.RentRowBatch(columnLookup, 1024);

        foreach (DataValue[] values in rows)
            batch.Add(values);

        return batch;
    }

    [Fact]
    public void InfersTypesFromFirstBatch()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        string[] names = ["id", "score"];

        RowBatch batch = MakeBatch(pool, names,
            [DataValue.FromInt32(1), DataValue.FromFloat64(1.5)],
            [DataValue.FromInt32(2), DataValue.FromFloat64(2.7)]);

        SchemaDetector detector = new();
        detector.Detect(batch);

        Assert.True(detector.IsDetected);
        Assert.Equal(2, detector.Schema.Columns.Count);
        Assert.Equal("id", detector.Schema.Columns[0].Name);
        Assert.Equal(DataKind.Int32, detector.Schema.Columns[0].Kind);
        Assert.Equal("score", detector.Schema.Columns[1].Name);
        Assert.Equal(DataKind.Float64, detector.Schema.Columns[1].Kind);
    }

    [Fact]
    public void HandlesNullInFirstRow()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        string[] names = ["x", "y"];

        RowBatch batch = MakeBatch(pool, names,
            [DataValue.FromInt32(1), DataValue.Null(DataKind.Unknown)],
            [DataValue.FromInt32(2), DataValue.FromFloat64(3.14)]);

        SchemaDetector detector = new();
        detector.Detect(batch);

        Assert.Equal(DataKind.Float64, detector.Schema.Columns[1].Kind);
    }

    [Fact]
    public void ShortCircuitsAfterFirstDetection()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        string[] names = ["v"];

        RowBatch first = MakeBatch(pool, names, [DataValue.FromInt32(1)]);
        RowBatch second = MakeBatch(pool, names, [DataValue.FromFloat64(2.0)]);

        SchemaDetector detector = new();
        detector.Detect(first);
        detector.Detect(second);

        // Kind stays from the first batch — the second Detect call is a no-op.
        Assert.Equal(DataKind.Int32, detector.Schema.Columns[0].Kind);
    }

    [Fact]
    public void EmptyBatch_NoDetection()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        ColumnLookup columnLookup = new(Array.Empty<string>());
        RowBatch empty = pool.RentRowBatch(columnLookup, 1024);

        SchemaDetector detector = new();
        detector.Detect(empty);

        Assert.False(detector.IsDetected);
        Assert.Throws<InvalidOperationException>(() => _ = detector.Schema);
    }

    [Fact]
    public void AllColumnsNullable()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        string[] names = ["a", "b"];

        RowBatch batch = MakeBatch(pool, names,
            [DataValue.FromInt32(1), DataValue.FromBoolean(true)]);

        SchemaDetector detector = new();
        detector.Detect(batch);

        Assert.True(detector.Schema.Columns[0].Nullable);
        Assert.True(detector.Schema.Columns[1].Nullable);
    }
}
