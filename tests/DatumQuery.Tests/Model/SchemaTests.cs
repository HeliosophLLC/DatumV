using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Tests.Model;

public class SchemaTests
{
    [Fact]
    public void SchemaStoresColumns()
    {
        Schema schema = new Schema([
            new ColumnInfo("id", DataKind.Scalar, nullable: false),
            new ColumnInfo("name", DataKind.String, nullable: true),
        ]);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("id", schema.Columns[0].Name);
        Assert.Equal(DataKind.Scalar, schema.Columns[0].Kind);
        Assert.False(schema.Columns[0].Nullable);
        Assert.True(schema.Columns[1].Nullable);
    }

    [Fact]
    public void SchemaLooksUpColumnByName()
    {
        Schema schema = new Schema([
            new ColumnInfo("alpha", DataKind.Scalar, nullable: false),
            new ColumnInfo("beta", DataKind.String, nullable: true),
        ]);

        ColumnInfo? column = schema.FindColumn("beta");

        Assert.NotNull(column);
        Assert.Equal("beta", column.Name);
        Assert.Equal(DataKind.String, column.Kind);
    }

    [Fact]
    public void SchemaLookupIsCaseInsensitive()
    {
        Schema schema = new Schema([
            new ColumnInfo("Name", DataKind.String, nullable: false),
        ]);

        ColumnInfo? column = schema.FindColumn("name");

        Assert.NotNull(column);
        Assert.Equal("Name", column.Name);
    }

    [Fact]
    public void SchemaReturnsNullForMissingColumn()
    {
        Schema schema = new Schema([
            new ColumnInfo("id", DataKind.Scalar, nullable: false),
        ]);

        ColumnInfo? column = schema.FindColumn("missing");

        Assert.Null(column);
    }

    [Fact]
    public void SchemaColumnsAreImmutable()
    {
        Schema schema = new Schema([
            new ColumnInfo("id", DataKind.Scalar, nullable: false),
        ]);

        Assert.IsAssignableFrom<IReadOnlyList<ColumnInfo>>(schema.Columns);
    }

    [Fact]
    public void ColumnInfoRecordEquality()
    {
        ColumnInfo a = new("id", DataKind.Scalar, nullable: false);
        ColumnInfo b = new("id", DataKind.Scalar, nullable: false);
        ColumnInfo c = new("id", DataKind.Scalar, nullable: true);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void SchemaRejectsEmptyColumnList()
    {
        Assert.Throws<ArgumentException>(() => new Schema([]));
    }

    [Fact]
    public void SchemaRejectsDuplicateColumnNames()
    {
        Assert.Throws<ArgumentException>(() => new Schema([
            new ColumnInfo("id", DataKind.Scalar, nullable: false),
            new ColumnInfo("id", DataKind.String, nullable: true),
        ]));
    }

    [Fact]
    public void SchemaRejectsDuplicateColumnNamesCaseInsensitive()
    {
        Assert.Throws<ArgumentException>(() => new Schema([
            new ColumnInfo("Name", DataKind.String, nullable: false),
            new ColumnInfo("name", DataKind.String, nullable: true),
        ]));
    }
}
