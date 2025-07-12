using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="SortedValueIndexSet"/> collection operations.
/// </summary>
public sealed class SortedValueIndexSetTests
{
    [Fact]
    public void TryGetIndex_ExistingColumn_ReturnsTrue()
    {
        ValueIndexEntry[] entries = [new(DataValue.FromScalar(1.0f), 0, 0)];
        Dictionary<string, SortedValueIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new SortedValueIndex(entries)
        };

        SortedValueIndexSet set = new(indexes);

        Assert.True(set.TryGetIndex("id", out SortedValueIndex? index));
        Assert.NotNull(index);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void TryGetIndex_NonExistentColumn_ReturnsFalse()
    {
        Dictionary<string, SortedValueIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new SortedValueIndex(Array.Empty<ValueIndexEntry>())
        };

        SortedValueIndexSet set = new(indexes);

        Assert.False(set.TryGetIndex("name", out _));
    }

    [Fact]
    public void TryGetIndex_CaseInsensitive()
    {
        Dictionary<string, SortedValueIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MyColumn"] = new SortedValueIndex(Array.Empty<ValueIndexEntry>())
        };

        SortedValueIndexSet set = new(indexes);

        Assert.True(set.TryGetIndex("mycolumn", out _));
        Assert.True(set.TryGetIndex("MYCOLUMN", out _));
    }

    [Fact]
    public void HasColumn_ExistingColumn_ReturnsTrue()
    {
        Dictionary<string, SortedValueIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new SortedValueIndex(Array.Empty<ValueIndexEntry>())
        };

        SortedValueIndexSet set = new(indexes);

        Assert.True(set.HasColumn("id"));
        Assert.False(set.HasColumn("nonexistent"));
    }

    [Fact]
    public void Count_ReturnsNumberOfColumns()
    {
        Dictionary<string, SortedValueIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new SortedValueIndex(Array.Empty<ValueIndexEntry>()),
            ["name"] = new SortedValueIndex(Array.Empty<ValueIndexEntry>()),
        };

        SortedValueIndexSet set = new(indexes);

        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void ColumnNames_ReturnsAllColumns()
    {
        Dictionary<string, SortedValueIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new SortedValueIndex(Array.Empty<ValueIndexEntry>()),
            ["name"] = new SortedValueIndex(Array.Empty<ValueIndexEntry>()),
        };

        SortedValueIndexSet set = new(indexes);

        Assert.Equal(2, set.ColumnNames.Count);
        Assert.Contains("id", set.ColumnNames);
        Assert.Contains("name", set.ColumnNames);
    }
}
