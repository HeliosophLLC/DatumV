using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Tests for <see cref="ColumnBatch"/> — construction, reading, writing,
/// arena-backed strings, materialisation, row adapter, and disposal.
/// </summary>
public class ColumnBatchTests
{
    [Fact]
    public void CreateAndWriteNumericValues()
    {
        string[] names = ["id", "score"];
        using ColumnBatch batch = ColumnBatch.Create(names, rowCapacity: 4);

        batch.SetValue(0, 0, DataValue.FromInt32(1));
        batch.SetValue(1, 0, DataValue.FromFloat64(9.5));
        batch.SetValue(0, 1, DataValue.FromInt32(2));
        batch.SetValue(1, 1, DataValue.FromFloat64(8.0));
        batch.SetRowCount(2);

        Assert.Equal(2, batch.ColumnCount);
        Assert.Equal(2, batch.RowCount);
        Assert.Equal(DataValue.FromInt32(1), batch.GetValue(0, 0));
        Assert.Equal(DataValue.FromFloat64(9.5), batch.GetValue(0, 1));
        Assert.Equal(DataValue.FromInt32(2), batch.GetValue(1, 0));
        Assert.Equal(DataValue.FromFloat64(8.0), batch.GetValue(1, 1));
    }

    [Fact]
    public void GetColumnReturnsSlicedSpan()
    {
        string[] names = ["value"];
        using ColumnBatch batch = ColumnBatch.Create(names, rowCapacity: 8);

        batch.SetValue(0, 0, DataValue.FromInt32(10));
        batch.SetValue(0, 1, DataValue.FromInt32(20));
        batch.SetValue(0, 2, DataValue.FromInt32(30));
        batch.SetRowCount(3);

        ReadOnlySpan<DataValue> column = batch.GetColumn(0);

        Assert.Equal(3, column.Length);
        Assert.Equal(DataValue.FromInt32(10), column[0]);
        Assert.Equal(DataValue.FromInt32(20), column[1]);
        Assert.Equal(DataValue.FromInt32(30), column[2]);
    }

    [Fact]
    public void ArenaBackedStringRoundTrips()
    {
        string[] names = ["name"];
        using ColumnBatch batch = ColumnBatch.Create(names, rowCapacity: 4);

        (int offset, int length) = batch.Arena.AppendString("hello");
        batch.SetValue(0, 0, DataValue.FromStringSlice(offset, length));
        batch.SetRowCount(1);

        Assert.Equal("hello", batch.MaterializeString(0, 0));
    }

    [Fact]
    public void ManagedStringAlsoWorksViaMaterialize()
    {
        string[] names = ["name"];
        using ColumnBatch batch = ColumnBatch.Create(names, rowCapacity: 4);

        batch.SetValue(0, 0, DataValue.FromString("direct"));
        batch.SetRowCount(1);

        Assert.Equal("direct", batch.MaterializeString(0, 0));
    }

    [Fact]
    public void GetStringBytesReturnsRawUtf8()
    {
        string[] names = ["text"];
        using ColumnBatch batch = ColumnBatch.Create(names, rowCapacity: 4);

        (int offset, int length) = batch.Arena.AppendString("abc");
        batch.SetValue(0, 0, DataValue.FromStringSlice(offset, length));
        batch.SetRowCount(1);

        ReadOnlySpan<byte> bytes = batch.GetStringBytes(0, 0);
        Assert.Equal(3, bytes.Length);
        Assert.Equal((byte)'a', bytes[0]);
        Assert.Equal((byte)'b', bytes[1]);
        Assert.Equal((byte)'c', bytes[2]);
    }

    [Fact]
    public void TryGetColumnOrdinalResolvesNames()
    {
        string[] names = ["alpha", "beta"];
        using ColumnBatch batch = ColumnBatch.Create(names, rowCapacity: 1);

        Assert.True(batch.TryGetColumnOrdinal("alpha", out int ordinal));
        Assert.Equal(0, ordinal);
        Assert.True(batch.TryGetColumnOrdinal("BETA", out ordinal));
        Assert.Equal(1, ordinal);
        Assert.False(batch.TryGetColumnOrdinal("gamma", out _));
    }

    [Fact]
    public void GetRowProducesSelfContainedRow()
    {
        string[] names = ["id", "name"];
        using ColumnBatch batch = ColumnBatch.Create(names, rowCapacity: 4);

        batch.SetValue(0, 0, DataValue.FromInt32(42));
        (int offset, int length) = batch.Arena.AppendString("arena-string");
        batch.SetValue(1, 0, DataValue.FromStringSlice(offset, length));
        batch.SetRowCount(1);

        Row row = batch.GetRow(0);

        Assert.Equal(2, row.FieldCount);
        Assert.Equal(DataValue.FromInt32(42), row[0]);

        // Arena-backed string materialised into a real string.
        Assert.Equal("arena-string", row[1].AsString());
    }

    [Fact]
    public void FromRowBatchTransposesCorrectly()
    {
        RowBatch rowBatch = RowBatch.Rent(4);
        rowBatch.Add(new Row(["a", "b"], [DataValue.FromInt32(1), DataValue.FromString("x")]));
        rowBatch.Add(new Row(["a", "b"], [DataValue.FromInt32(2), DataValue.FromString("y")]));

        using ColumnBatch columnBatch = ColumnBatch.FromRowBatch(rowBatch);
        rowBatch.Return();

        Assert.Equal(2, columnBatch.ColumnCount);
        Assert.Equal(2, columnBatch.RowCount);
        Assert.Equal(DataValue.FromInt32(1), columnBatch.GetValue(0, 0));
        Assert.Equal(DataValue.FromInt32(2), columnBatch.GetValue(1, 0));
        Assert.Equal(DataValue.FromString("x"), columnBatch.GetValue(0, 1));
        Assert.Equal(DataValue.FromString("y"), columnBatch.GetValue(1, 1));
    }

    [Fact]
    public void GetColumnNameReturnsCorrectName()
    {
        string[] names = ["first", "second"];
        using ColumnBatch batch = ColumnBatch.Create(names, rowCapacity: 1);

        Assert.Equal("first", batch.GetColumnName(0));
        Assert.Equal("second", batch.GetColumnName(1));
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        ColumnBatch batch = ColumnBatch.Create(["col"], rowCapacity: 4);
        batch.SetValue(0, 0, DataValue.FromInt32(1));
        batch.SetRowCount(1);
        batch.Dispose();
        batch.Dispose();
    }

    [Fact]
    public void EmptyBatchHasZeroRows()
    {
        using ColumnBatch batch = ColumnBatch.Create(["x"], rowCapacity: 16);

        Assert.Equal(0, batch.RowCount);
        Assert.Equal(16, batch.RowCapacity);
        Assert.Equal(1, batch.ColumnCount);
    }
}
