using DatumQuery.Model;

namespace DatumQuery.Tests.Model;

public class RowTests
{
    [Fact]
    public void RowStoresNamedValues()
    {
        Row row = new Row(
            ["name", "age"],
            [DataValue.FromString("Alice"), DataValue.FromScalar(30.0f)]);

        Assert.Equal(2, row.FieldCount);
        Assert.Equal("Alice", row["name"].AsString());
        Assert.Equal(30.0f, row["age"].AsScalar());
    }

    [Fact]
    public void RowAccessByOrdinal()
    {
        Row row = new Row(
            ["x", "y"],
            [DataValue.FromScalar(1.0f), DataValue.FromScalar(2.0f)]);

        Assert.Equal(1.0f, row[0].AsScalar());
        Assert.Equal(2.0f, row[1].AsScalar());
    }

    [Fact]
    public void RowThrowsOnInvalidColumnName()
    {
        Row row = new Row(
            ["name"],
            [DataValue.FromString("test")]);

        Assert.Throws<KeyNotFoundException>(() => row["missing"]);
    }

    [Fact]
    public void RowThrowsOnInvalidOrdinal()
    {
        Row row = new Row(
            ["name"],
            [DataValue.FromString("test")]);

        Assert.Throws<ArgumentOutOfRangeException>(() => row[1]);
    }

    [Fact]
    public void RowReturnsColumnNames()
    {
        Row row = new Row(
            ["alpha", "beta", "gamma"],
            [DataValue.FromScalar(1.0f), DataValue.FromScalar(2.0f), DataValue.FromScalar(3.0f)]);

        Assert.Equal(["alpha", "beta", "gamma"], row.ColumnNames);
    }

    [Fact]
    public void RowWithLazyValueDoesNotForceOnAccess()
    {
        int forceCount = 0;
        LazyDataValue lazy = new(() =>
        {
            forceCount++;
            return DataValue.FromString("expensive");
        }, DataKind.String);

        Row row = new Row(
            ["cheap", "expensive_col"],
            [DataValue.FromScalar(1.0f), lazy.Value]);

        // Accessing the cheap column should not affect the lazy value
        DataValue cheap = row["cheap"];
        Assert.Equal(1.0f, cheap.AsScalar());
    }

    [Fact]
    public void RowNamesAreCaseInsensitive()
    {
        Row row = new Row(
            ["Name"],
            [DataValue.FromString("Alice")]);

        Assert.Equal("Alice", row["name"].AsString());
        Assert.Equal("Alice", row["NAME"].AsString());
    }

    [Fact]
    public void RowRejectsNameValueCountMismatch()
    {
        Assert.Throws<ArgumentException>(() => new Row(
            ["a", "b"],
            [DataValue.FromScalar(1.0f)]));
    }

    [Fact]
    public void TryGetValueReturnsFalseForMissingColumn()
    {
        Row row = new Row(
            ["name"],
            [DataValue.FromString("test")]);

        bool found = row.TryGetValue("missing", out DataValue? result);

        Assert.False(found);
        Assert.Null(result);
    }

    [Fact]
    public void TryGetValueReturnsTrueForExistingColumn()
    {
        Row row = new Row(
            ["name"],
            [DataValue.FromString("test")]);

        bool found = row.TryGetValue("name", out DataValue? result);

        Assert.True(found);
        Assert.NotNull(result);
        Assert.Equal("test", result.AsString());
    }
}
