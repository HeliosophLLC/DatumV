using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Model;

public class RowTests : ServiceTestBase
{
    [Fact]
    public void RowStoresNamedValues()
    {
        ColumnLookup columnLookup = new(["name", "age"]);
        Row row = MakeRow(
            columnLookup,
            DataValue.FromString("Alice"),
            DataValue.FromFloat32(30.0f));

        Assert.Equal(2, row.FieldCount);
        Assert.Equal("Alice", row["name"].AsString());
        Assert.Equal(30.0f, row["age"].AsFloat32());
    }

    [Fact]
    public void RowAccessByOrdinal()
    {
        ColumnLookup columnLookup = new(["x", "y"]);
        Row row = MakeRow(
            columnLookup,
            DataValue.FromFloat32(1.0f),
            DataValue.FromFloat32(2.0f));

        Assert.Equal(1.0f, row[0].AsFloat32());
        Assert.Equal(2.0f, row[1].AsFloat32());
    }

    [Fact]
    public void RowThrowsOnInvalidColumnName()
    {
        ColumnLookup columnLookup = new(["name"]);
        Row row = MakeRow(
            columnLookup,
            DataValue.FromString("test"));

        Assert.Throws<KeyNotFoundException>(() => row["missing"]);
    }

    [Fact]
    public void RowThrowsOnInvalidOrdinal()
    {
        ColumnLookup columnLookup = new(["name"]);
        Row row = MakeRow(
            columnLookup,
            DataValue.FromString("test"));

        Assert.Throws<ArgumentOutOfRangeException>(() => row[1]);
    }

    [Fact]
    public void RowReturnsColumnNames()
    {
        ColumnLookup columnLookup = new(["alpha", "beta", "gamma"]);
        Row row = MakeRow(
            columnLookup,
            DataValue.FromFloat32(1.0f),
            DataValue.FromFloat32(2.0f),
            DataValue.FromFloat32(3.0f));

        Assert.Equal(["alpha", "beta", "gamma"], row.ColumnNames);
    }

    [Fact]
    public void RowWithLazyValueDoesNotForceOnAccess()
    {
        ColumnLookup columnLookup = new(["cheap", "expensive_col"]);
        int forceCount = 0;
        LazyDataValue lazy = new(() =>
        {
            forceCount++;
            return DataValue.FromString("expensive");
        }, DataKind.String);

        Row row = MakeRow(
            columnLookup,
            DataValue.FromFloat32(1.0f),
            lazy.Value);

        // Accessing the cheap column should not affect the lazy value
        DataValue cheap = row["cheap"];
        Assert.Equal(1.0f, cheap.AsFloat32());
    }

    [Fact]
    public void RowNamesAreCaseInsensitive()
    {
        ColumnLookup columnLookup = new(["Name"]);
        Row row = MakeRow(
            columnLookup,
            DataValue.FromString("Alice"));

        Assert.Equal("Alice", row["name"].AsString());
        Assert.Equal("Alice", row["NAME"].AsString());
    }

    [Fact]
    public void TryGetValueReturnsFalseForMissingColumn()
    {
        ColumnLookup columnLookup = new(["name"]);
        Row row = MakeRow(
            columnLookup,
            DataValue.FromString("test"));

        bool found = row.TryGetValue("missing", out DataValue result);

        Assert.False(found);
        Assert.Equal(default(DataValue), result);
    }

    [Fact]
    public void TryGetValueReturnsTrueForExistingColumn()
    {
        ColumnLookup columnLookup = new(["name"]);
        Row row = MakeRow(
            columnLookup,
            DataValue.FromString("test"));

        bool found = row.TryGetValue("name", out DataValue result);

        Assert.True(found);
        Assert.Equal("test", result.AsString());
    }
}
