using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Tests for the <see cref="RowBatch"/> pooled row-major batch.
/// </summary>
public class RowBatchTests : ServiceTestBase
{
    private static Row MakeRow(string name, float value)
    {
        return new Row([name], [DataValue.FromFloat32(value)]);
    }

    private static Row MakeRow(string nameColumn, string nameValue, string ageColumn, float ageValue)
    {
        return new Row([nameColumn, ageColumn], [DataValue.FromString(nameValue), DataValue.FromFloat32(ageValue)]);
    }


    /// <summary>
    /// Verifies that <see cref="RowBatch.Add"/> increments the count and
    /// stores the row so it can be retrieved by the indexer.
    /// </summary>
    [Fact]
    public void AddIncrementsCountAndStoresRow()
    {
        RowBatch batch = RowBatch.Rent(4);
        Row row = MakeRow("value", 42.0f);

        batch.Add(row);

        Assert.Equal(1, batch.Count);
        Assert.Equal(42.0f, batch[0]["value"].AsFloat32());

        batch.Return();
    }

    /// <summary>
    /// Verifies that <see cref="RowBatch.IsFull"/> returns <c>true</c> once
    /// the count equals the capacity.
    /// </summary>
    [Fact]
    public void IsFullReturnsTrueWhenCountEqualsCapacity()
    {
        RowBatch batch = RowBatch.Rent(2);

        Assert.False(batch.IsFull);

        batch.Add(MakeRow("x", 1.0f));
        Assert.False(batch.IsFull);

        batch.Add(MakeRow("x", 2.0f));
        Assert.True(batch.IsFull);

        batch.Return();
    }

    /// <summary>
    /// Verifies that the indexer returns the correct row for each position
    /// after multiple adds.
    /// </summary>
    [Fact]
    public void IndexerReturnsCorrectRows()
    {
        RowBatch batch = RowBatch.Rent(3);

        Row first = MakeRow("name", "Alice", "age", 30.0f);
        Row second = MakeRow("name", "Bob", "age", 25.0f);
        Row third = MakeRow("name", "Carol", "age", 28.0f);

        batch.Add(first);
        batch.Add(second);
        batch.Add(third);

        Assert.Equal("Alice", batch[0]["name"].AsString());
        Assert.Equal("Bob", batch[1]["name"].AsString());
        Assert.Equal("Carol", batch[2]["name"].AsString());

        batch.Return();
    }

    /// <summary>
    /// Verifies that the indexer throws <see cref="ArgumentOutOfRangeException"/>
    /// when given a negative index.
    /// </summary>
    [Fact]
    public void IndexerThrowsForNegativeIndex()
    {
        RowBatch batch = RowBatch.Rent(4);
        batch.Add(MakeRow("x", 1.0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => batch[-1]);

        batch.Return();
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
        RowBatch batch = RowBatch.Rent(8);
        batch.Add(MakeRow("x", 1.0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => batch[index]);

        batch.Return();
    }

    /// <summary>
    /// Verifies that <see cref="RowBatch.Add"/> throws
    /// <see cref="InvalidOperationException"/> when the batch is already full.
    /// </summary>
    [Fact]
    public void AddThrowsWhenBatchIsFull()
    {
        RowBatch batch = RowBatch.Rent(1);
        batch.Add(MakeRow("x", 1.0f));

        Assert.Throws<InvalidOperationException>(() => batch.Add(MakeRow("x", 2.0f)));

        batch.Return();
    }

    /// <summary>
    /// Verifies that <see cref="RowBatch.Return"/> returns the backing array
    /// to the pool and resets the count to zero.
    /// </summary>
    [Fact]
    public void ReturnResetsCountToZero()
    {
        RowBatch batch = RowBatch.Rent(4);
        batch.Add(MakeRow("x", 1.0f));
        batch.Add(MakeRow("x", 2.0f));

        Assert.Equal(2, batch.Count);

        batch.Return();

        Assert.Equal(0, batch.Count);
    }

    /// <summary>
    /// Verifies that calling <see cref="RowBatch.Return"/> twice does not
    /// throw — the operation is idempotent.
    /// </summary>
    [Fact]
    public void ReturnIsIdempotent()
    {
        RowBatch batch = RowBatch.Rent(4);
        batch.Add(MakeRow("x", 1.0f));

        batch.Return();
        batch.Return();

        Assert.Equal(0, batch.Count);
    }
}
