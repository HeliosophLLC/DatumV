using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

public class RowBatchTests
{
    [Fact]
    public void BatchStoresColumnarData()
    {
        Schema schema = new Schema([
            new ColumnInfo("x", DataKind.Float32, nullable: false),
            new ColumnInfo("y", DataKind.Float32, nullable: false),
        ]);

        DataValue[][] columns =
        [
            [DataValue.FromFloat32(1.0f), DataValue.FromFloat32(2.0f)],
            [DataValue.FromFloat32(3.0f), DataValue.FromFloat32(4.0f)],
        ];

        RowBatch batch = new(schema, columns);

        Assert.Equal(2, batch.RowCount);
        Assert.Equal(2, batch.ColumnCount);
    }

    [Fact]
    public void BatchAccessByRowIndex()
    {
        Schema schema = new Schema([
            new ColumnInfo("name", DataKind.String, nullable: false),
            new ColumnInfo("age", DataKind.Float32, nullable: false),
        ]);

        DataValue[][] columns =
        [
            [DataValue.FromString("Alice"), DataValue.FromString("Bob")],
            [DataValue.FromFloat32(30.0f), DataValue.FromFloat32(25.0f)],
        ];

        RowBatch batch = new(schema, columns);

        Row row0 = batch.GetRow(0);
        Assert.Equal("Alice", row0["name"].AsString());
        Assert.Equal(30.0f, row0["age"].AsFloat32());

        Row row1 = batch.GetRow(1);
        Assert.Equal("Bob", row1["name"].AsString());
        Assert.Equal(25.0f, row1["age"].AsFloat32());
    }

    [Fact]
    public void BatchAccessColumnByName()
    {
        Schema schema = new Schema([
            new ColumnInfo("values", DataKind.Float32, nullable: false),
        ]);

        DataValue[][] columns =
        [
            [DataValue.FromFloat32(10.0f), DataValue.FromFloat32(20.0f), DataValue.FromFloat32(30.0f)],
        ];

        RowBatch batch = new(schema, columns);

        ReadOnlySpan<DataValue> column = batch.GetColumn("values");
        Assert.Equal(3, column.Length);
        Assert.Equal(10.0f, column[0].AsFloat32());
        Assert.Equal(30.0f, column[2].AsFloat32());
    }

    [Fact]
    public void BatchAccessColumnByOrdinal()
    {
        Schema schema = new Schema([
            new ColumnInfo("a", DataKind.Float32, nullable: false),
            new ColumnInfo("b", DataKind.Float32, nullable: false),
        ]);

        DataValue[][] columns =
        [
            [DataValue.FromFloat32(1.0f)],
            [DataValue.FromFloat32(2.0f)],
        ];

        RowBatch batch = new(schema, columns);

        ReadOnlySpan<DataValue> col = batch.GetColumn(1);
        Assert.Equal(2.0f, col[0].AsFloat32());
    }

    [Fact]
    public void BatchSliceReturnsSubset()
    {
        Schema schema = new Schema([
            new ColumnInfo("id", DataKind.Float32, nullable: false),
        ]);

        DataValue[][] columns =
        [
            [DataValue.FromFloat32(1.0f), DataValue.FromFloat32(2.0f), DataValue.FromFloat32(3.0f), DataValue.FromFloat32(4.0f)],
        ];

        RowBatch batch = new(schema, columns);
        RowBatch sliced = batch.Slice(1, 2);

        Assert.Equal(2, sliced.RowCount);
        Assert.Equal(2.0f, sliced.GetRow(0)["id"].AsFloat32());
        Assert.Equal(3.0f, sliced.GetRow(1)["id"].AsFloat32());
    }

    [Fact]
    public void BatchRejectsColumnCountMismatch()
    {
        Schema schema = new Schema([
            new ColumnInfo("a", DataKind.Float32, nullable: false),
            new ColumnInfo("b", DataKind.Float32, nullable: false),
        ]);

        DataValue[][] columns =
        [
            [DataValue.FromFloat32(1.0f)],
        ];

        Assert.Throws<ArgumentException>(() => new RowBatch(schema, columns));
    }

    [Fact]
    public void BatchRejectsJaggedColumns()
    {
        Schema schema = new Schema([
            new ColumnInfo("a", DataKind.Float32, nullable: false),
            new ColumnInfo("b", DataKind.Float32, nullable: false),
        ]);

        DataValue[][] columns =
        [
            [DataValue.FromFloat32(1.0f), DataValue.FromFloat32(2.0f)],
            [DataValue.FromFloat32(3.0f)],
        ];

        Assert.Throws<ArgumentException>(() => new RowBatch(schema, columns));
    }

    [Fact]
    public void EmptyBatchHasZeroRows()
    {
        Schema schema = new Schema([
            new ColumnInfo("id", DataKind.Float32, nullable: false),
        ]);

        DataValue[][] columns = [[]];

        RowBatch batch = new(schema, columns);
        Assert.Equal(0, batch.RowCount);
    }

    [Fact]
    public void BatchExposesSchema()
    {
        Schema schema = new Schema([
            new ColumnInfo("id", DataKind.Float32, nullable: false),
        ]);

        DataValue[][] columns = [[DataValue.FromFloat32(1.0f)]];

        RowBatch batch = new(schema, columns);
        Assert.Same(schema, batch.Schema);
    }
}
