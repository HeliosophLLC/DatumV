using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Tests for the <see cref="RowBatch"/> pooled row-major batch.
/// </summary>
public class RowBatchTests : ServiceTestBase
{
    private static Pool pool = new(new PoolBacking());


    /// <summary>
    /// Verifies that <see cref="RowBatch.Add(DataValue[])"/> increments the count and
    /// stores the row so it can be retrieved by the indexer.
    /// </summary>
    [Fact]
    public void AddIncrementsCountAndStoresRow()
    {
        ColumnLookup columnLookup = new(["value"]);
        RowBatch batch = pool.RentRowBatch(columnLookup, 4);

        batch.Add([DataValue.FromFloat32(42.0f)]);

        Assert.Equal(1, batch.Count);
        Assert.Equal(42.0f, batch[0]["value"].AsFloat32());

        pool.ReturnRowBatch(batch);
    }

    /// <summary>
    /// Verifies that <see cref="RowBatch.IsFull"/> returns <c>true</c> once
    /// the count equals the capacity.
    /// </summary>
    [Fact]
    public void IsFullReturnsTrueWhenCountEqualsCapacity()
    {
        ColumnLookup columnLookup = new(["x"]);
        RowBatch batch = pool.RentRowBatch(columnLookup, 2);

        Assert.False(batch.IsFull);

        batch.Add([DataValue.FromFloat32(1.0f)]);
        Assert.False(batch.IsFull);

        batch.Add([DataValue.FromFloat32(2.0f)]);
        Assert.True(batch.IsFull);

        pool.ReturnRowBatch(batch);
    }

    /// <summary>
    /// Verifies that the indexer returns the correct row for each position
    /// after multiple adds.
    /// </summary>
    [Fact]
    public void IndexerReturnsCorrectRows()
    {
        ColumnLookup columnLookup = new(["name", "age"]);
        RowBatch batch = pool.RentRowBatch(columnLookup, 3);

        batch.Add([DataValue.FromString("Alice"), DataValue.FromFloat32(30.0f)]);
        batch.Add([DataValue.FromString("Bob"), DataValue.FromFloat32(25.0f)]);
        batch.Add([DataValue.FromString("Carol"), DataValue.FromFloat32(28.0f)]);

        Assert.Equal("Alice", batch[0]["name"].AsString());
        Assert.Equal("Bob", batch[1]["name"].AsString());
        Assert.Equal("Carol", batch[2]["name"].AsString());

        pool.ReturnRowBatch(batch);
    }

    /// <summary>
    /// Verifies that the indexer throws <see cref="ArgumentOutOfRangeException"/>
    /// when given a negative index.
    /// </summary>
    [Fact]
    public void IndexerThrowsForNegativeIndex()
    {
        ColumnLookup columnLookup = new(["x"]);
        RowBatch batch = pool.RentRowBatch(columnLookup, 4);
        batch.Add([DataValue.FromFloat32(1.0f)]);

        Assert.Throws<IndexOutOfRangeException>(() => batch[-1]);

        pool.ReturnRowBatch(batch);
    }
    
    /// <summary>
    /// Verifies that the indexer throws <see cref="ArgumentOutOfRangeException"/>
    /// when the index equals or exceeds the current count.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void IndexerThrowsForOutOfRangeIndex(int index)
    {
        ColumnLookup columnLookup = new(["x"]);
        RowBatch batch = pool.RentRowBatch(columnLookup, 8);
        batch.Add([DataValue.FromFloat32(1.0f)]);

        Assert.Throws<ArgumentOutOfRangeException>(() => batch[index]);

        pool.ReturnRowBatch(batch);
    }

    /// <summary>
    /// Verifies that <see cref="RowBatch.Add(DataValue[])"/> throws
    /// <see cref="InvalidOperationException"/> when the batch is already full.
    /// </summary>
    [Fact]
    public void AddThrowsWhenBatchIsFull()
    {
        ColumnLookup columnLookup = new(["x"]);
        RowBatch batch = pool.RentRowBatch(columnLookup, 1);
        batch.Add([DataValue.FromFloat32(1.0f)]);

        Assert.Throws<InvalidOperationException>(() => batch.Add([DataValue.FromFloat32(2.0f)]));

        pool.ReturnRowBatch(batch);
    }

    /// <summary>
    /// Verifies that <see cref="Pool.ReturnRowBatch(RowBatch)"/> returns the backing array
    /// to the pool and resets the count to zero.
    /// </summary>
    [Fact]
    public void ReturnResetsCountToZero()
    {
        ColumnLookup columnLookup = new(["x"]);
        RowBatch batch = pool.RentRowBatch(columnLookup, 4);
        batch.Add([DataValue.FromFloat32(1.0f)]);
        batch.Add([DataValue.FromFloat32(2.0f)]);

        Assert.Equal(2, batch.Count);

        pool.ReturnRowBatch(batch);

        Assert.Equal(0, batch.Count);
    }

    /// <summary>
    /// Verifies that calling <see cref="Pool.ReturnRowBatch"/> twice throws
    /// and <see cref="ObjectDisposedException" />.
    /// </summary>
    [Fact]
    public void DoubleReturnThrowsDisposedException()
    {
        ColumnLookup columnLookup = new(["x"]);
        RowBatch batch = pool.RentRowBatch(columnLookup, 4);
        batch.Add([DataValue.FromFloat32(1.0f)]);

        pool.ReturnRowBatch(batch);

        Assert.Throws<ObjectDisposedException>(() => pool.ReturnRowBatch(batch));
    }

    
    /// <summary>
    /// Verifies that <see cref="RowBatch.ReturnBatch"/> returns all contained
    /// <see cref="DataValue"/> arrays to the <see cref="LocalBufferPool"/>.
    /// </summary>
    [Fact]
    public void ReturnBatch_ReturnsAllDataValueArrays()
    {
        Pool pool = CreatePool();
        int rowCount = 100;
        int columnCount = 3;

        ColumnLookup columnLookup = new(["c0", "c1", "c2"]);
        RowBatch batch = pool.RentRowBatch(columnLookup, rowCount);

        for (int i = 0; i < rowCount; i++)
        {
            DataValue[] values = pool.RentDataValues(columnCount);
            values[0] = DataValue.FromFloat32(i);
            values[1] = DataValue.FromFloat32(i * 10);
            values[2] = DataValue.FromFloat32(i * 100);
            batch.Add(values);
        }

        Assert.Equal(rowCount, pool.Backing.DataValueArrayRentCount);
        Assert.Equal(0, pool.Backing.DataValueArrayReturnCount);

        pool.ReturnRowBatch(batch);

        // All DataValue[] arrays should have been returned.
        Assert.Equal(rowCount, pool.Backing.DataValueArrayReturnCount);
    }

    /// <summary>
    /// Verifies that accessing a row's DataValue[] after the batch has been returned
    /// throws under POOL_DIAGNOSTICS — the array was returned to the pool and may
    /// have been re-rented by another consumer.
    /// </summary>
    [Fact]
    public void ReturnBatch_AccessAfterReturn_Throws()
    {
        Pool pool = CreatePool();
        ColumnLookup columnLookup = new(["a", "b"]);
        RowBatch batch = pool.RentRowBatch(columnLookup, 10);
        DataValue[] values = pool.RentDataValues(2);
        values[0] = DataValue.FromFloat32(1);
        values[1] = DataValue.FromFloat32(2);

        batch.Add(values);

        // Capture the row before returning.
        Row row = batch[0];

        pool.ReturnRowBatch(batch);

#if POOL_DIAGNOSTICS
        // Under POOL_DIAGNOSTICS, accessing the returned array throws.
        Assert.Throws<InvalidOperationException>(() => _ = row.RawValues);
#endif
    }
}
